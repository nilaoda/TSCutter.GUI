using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace TSCutter.GUI.Web;

internal sealed partial class WebUiSession
{
    private const string Remembered = "remembered";
    private const int MaxPairRequest = 2048;

    private IResult AuthStatus(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var request = context.Request;
        if (!TryReadBrowserCookie(request, out var cookie) || pairing.VerifyDevice(cookie, Origin(request).GetLeftPart(UriPartial.Authority)) is null)
            return Results.Text("pair");

        context.Response.Cookies.Append(CookieName(request), cookie!, BrowserCookie(request));
        return Results.Text(Remembered);
    }

    private async Task<IResult> PairAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var request = context.Request;
        if (!HasSameOrigin(request))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (request.ContentLength is 0 or > MaxPairRequest || !request.HasFormContentType)
            return Results.BadRequest();
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = MaxPairRequest;
        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(context.RequestAborted);
        }
        catch (InvalidDataException) { return Results.BadRequest(); }
        var code = form["code"].ToString();
        if (code.Length != 6 || !code.All(char.IsAsciiDigit))
            return Results.BadRequest();
        if (Volatile.Read(ref connected) != 0)
            return Results.StatusCode(StatusCodes.Status409Conflict);

        var browser = new string(form["name"].ToString().Where(character => !char.IsControl(character)).Take(48).ToArray()).Trim();
        var status = pairing.TryPair(code, browser, Origin(request).GetLeftPart(UriPartial.Authority), out var credential);
        if (credential is null)
            return Results.StatusCode(status);

        context.Response.Cookies.Append(CookieName(request), credential, BrowserCookie(request));
        return Results.Text(Remembered);
    }

    private IResult Logout(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!HasSameOrigin(context.Request))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (GetRememberedBrowser(context) is { } device)
            pairing.Revoke(device.Id);
        context.Response.Cookies.Delete(CookieName(context.Request), BrowserCookie(context.Request));
        return Results.NoContent();
    }

    private WebUiBrowserCredential? GetRememberedBrowser(HttpContext context)
    {
        var request = context.Request;
        if (!TryReadBrowserCookie(request, out var credential))
            return null;
        return pairing.VerifyDevice(credential, Origin(request).GetLeftPart(UriPartial.Authority));
    }

    // Cookie 按域名匹配。端口加入名称，避免同一地址的多个远程实例互相覆盖授权。
    private static string CookieName(HttpRequest request) =>
        (request.IsHttps ? "__Host-TSCutterWeb-" : "TSCutterWeb-") + Origin(request).Port;

    private static bool TryReadBrowserCookie(HttpRequest request, out string? value) =>
        request.Cookies.TryGetValue(CookieName(request), out value);

    private static Uri Origin(HttpRequest request) => WebUiHttp.Origin(request);

    private static bool HasSameOrigin(HttpRequest request) => WebUiHttp.HasSameOrigin(request);

    private static CookieOptions BrowserCookie(HttpRequest request) => WebUiHttp.BrowserCookie(request);
}
