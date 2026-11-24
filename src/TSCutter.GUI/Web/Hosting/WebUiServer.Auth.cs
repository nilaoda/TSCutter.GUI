using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace TSCutter.GUI.Web;

internal sealed partial class WebUiServer
{
    private readonly SemaphoreSlim loginSlots = new(2, 2);
    private readonly object attemptsGate = new();
    private readonly Dictionary<string, (DateTimeOffset Since, int Count)> attempts = [];

    private WebUiLogin? Authenticate(HttpContext context) =>
        context.Request.Cookies.TryGetValue(WebUiHttp.CookieName(context.Request), out var cookie)
            ? logins.Authenticate(cookie, WebUiHttp.Origin(context.Request).GetLeftPart(UriPartial.Authority)) : null;

    private IResult AuthStatus(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-TSCutter-Auth"] = "accounts";
        if (Authenticate(context) is not { } login)
            return Results.Text("login");
        context.Response.Headers["X-TSCutter-User"] = login.UserName;
        return Results.Text("remembered");
    }

    private bool AllowLogin(string address)
    {
        lock (attemptsGate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var key in attempts.Where(pair => now - pair.Value.Since >= TimeSpan.FromMinutes(1)).Select(pair => pair.Key).ToArray())
                attempts.Remove(key);
            if (!attempts.TryGetValue(address, out var previous))
            {
                if (attempts.Count >= 1024)
                    return false;
                attempts[address] = (now, 1);
                return true;
            }
            if (previous.Count >= 8)
                return false;
            attempts[address] = (previous.Since, previous.Count + 1);
            return true;
        }
    }

    private async Task<IResult> LoginAsync(HttpContext context)
    {
        const int maximumBody = 2048;
        context.Response.Headers.CacheControl = "no-store";
        var request = context.Request;
        if (!WebUiHttp.HasSameOrigin(request))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!request.HasFormContentType || request.ContentLength is 0 or > maximumBody)
            return Results.BadRequest();
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = maximumBody;
        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(context.RequestAborted);
        }
        catch (Exception error) when (error is InvalidDataException or BadHttpRequestException) { return Results.BadRequest(); }
        var name = form["username"].ToString().Trim();
        var password = form["password"].ToString();
        if (!WebUiPassword.ValidUserName(name) || password.Length is < 1 or > 128)
            return Results.BadRequest();
        if (!AllowLogin(context.Connection.RemoteIpAddress?.ToString() ?? string.Empty))
        {
            context.Response.Headers.RetryAfter = "60";
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }
        await loginSlots.WaitAsync(context.RequestAborted);
        try
        {
            var credential = await Task.Run(() => logins.TryLogin(name, password, WebUiHttp.Origin(request).GetLeftPart(UriPartial.Authority), out var value)
                ? value : null, context.RequestAborted);
            if (credential is null)
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            if (Authenticate(context) is { } previous)
            {
                logins.Logout(previous.SessionId);
                if (connections.TryGetValue(previous.UserId, out var connection) && connection.SessionId == previous.SessionId)
                    Disconnect(previous.UserId);
            }
            context.Response.Cookies.Append(WebUiHttp.CookieName(request), credential, WebUiHttp.BrowserCookie(request));
            return Results.Text("remembered");
        }
        finally { loginSlots.Release(); }
    }

    private IResult Logout(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!WebUiHttp.HasSameOrigin(context.Request))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (Authenticate(context) is { } login)
        {
            logins.Logout(login.SessionId);
            if (connections.TryGetValue(login.UserId, out var connection) && connection.SessionId == login.SessionId)
                Disconnect(login.UserId);
        }
        context.Response.Cookies.Delete(WebUiHttp.CookieName(context.Request), WebUiHttp.BrowserCookie(context.Request));
        return Results.NoContent();
    }
}
