using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Utils.Files;

namespace TSCutter.GUI.Web;

internal sealed record WebUiServerOptions(MediaWorkspace Workspace, int Port, bool AllowLan, int MaxWorkers,
    string? Preferences, string? Theme, string? Locale, bool Dark, string? Executable = null, string? Assembly = null, bool EnableIpv6 = false);

internal sealed partial class WebUiServer(WebUiServerOptions options, WebUiLoginStore logins) : IAsyncDisposable
{
    internal const WebSocketCloseStatus WorkerLimit = (WebSocketCloseStatus)4013;
    internal const WebSocketCloseStatus WorkspaceUpdated = (WebSocketCloseStatus)4014;
    private readonly ConcurrentDictionary<string, WebUiWorker> workers = [];
    private readonly ConcurrentDictionary<string, Connection> connections = [];
    private readonly SemaphoreSlim management = new(1, 1);
    private readonly CancellationTokenSource stopping = new();
    private readonly HttpClient http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
    private WebApplication? app;
    private Task? maintenance;
    private string? theme = options.Theme;
    private int dark = options.Dark ? 1 : 0;
    private int disposed;
    private sealed record Connection(string SessionId, CancellationTokenSource Cancellation, WebSocket Socket)
    {
        internal int Closing;
        internal SemaphoreSlim Output { get; } = new(1, 1);
        internal TaskCompletionSource<bool> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal WebSocketCloseStatus CloseStatus { get; set; } = WebSocketCloseStatus.EndpointUnavailable;
    }
    internal bool IsRunning => app is not null && !stopping.IsCancellationRequested;
    internal int Port
    {
        get; private set;
    }
    internal event Action? UsersChanged;
    internal event Action<string, string>? PreferencesChanged;

    internal async Task StartAsync(CancellationToken token = default)
    {
        if (app is not null || stopping.IsCancellationRequested)
            throw new InvalidOperationException("The Web UI service has already started or stopped.");
        if (options.MaxWorkers is < 1 or > 16 || options.Port != 0 && options.Port is < 1024 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(options));
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            WebUiHttp.ConfigureListener(server, options.AllowLan, options.Port, options.EnableIpv6);
        });
        var server = builder.Build();
        server.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15), KeepAliveTimeout = TimeSpan.FromSeconds(15) });
        server.MapGet("/", (RequestDelegate)(context => WebUiHttp.ServeAsset(context, "index.html", "text/html; charset=utf-8").ExecuteAsync(context)));
        foreach (var asset in new[] { "remote.js", "video-playback.js", "touchpad.js", "viewport.js", "remote.css" })
        {
            var contentType = asset.EndsWith(".css", StringComparison.Ordinal) ? "text/css; charset=utf-8" : "text/javascript; charset=utf-8";
            server.MapGet("/" + asset, (RequestDelegate)(context => WebUiHttp.ServeAsset(context, asset, contentType).ExecuteAsync(context)));
        }
        server.MapGet("/locales/{locale}.json", (RequestDelegate)(context =>
        {
            var locale = context.Request.RouteValues["locale"] as string;
            return (WebUiHttp.IsSupportedLocale(locale) ? WebUiHttp.ServeAsset(context, $"Locales.{locale}.json", "application/json; charset=utf-8")
                : Results.NotFound()).ExecuteAsync(context);
        }));
        server.MapGet("/startup-locale", (RequestDelegate)(context =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var locale = options.Locale;
            if (Authenticate(context) is { } login && logins.FindAccount(login.UserId)?.Preferences is { } preferences)
            {
                try
                {
                    using var profile = JsonDocument.Parse(preferences);
                    if (profile.RootElement.ValueKind == JsonValueKind.Object &&
                        profile.RootElement.TryGetProperty("Language", out var language) && language.ValueKind == JsonValueKind.String)
                        locale = language.GetString();
                }
                catch (JsonException) { }
            }
            return Results.Text(WebUiHttp.IsSupportedLocale(locale) ? locale : string.Empty).ExecuteAsync(context);
        }));
        server.MapGet("/appearance.css", (RequestDelegate)AppearanceAsync);
        server.MapGet("/auth", (RequestDelegate)(context => AuthStatus(context).ExecuteAsync(context)));
        server.MapPost("/login", (RequestDelegate)(async context => await (await LoginAsync(context)).ExecuteAsync(context)));
        server.MapPost("/logout", (RequestDelegate)(context => Logout(context).ExecuteAsync(context)));
        server.MapGet("/connect", (RequestDelegate)ConnectAsync);
        try
        {
            await server.StartAsync(token);
            Port = options.Port == 0 ? new Uri(server.Urls.Single()).Port : options.Port;
            app = server;
            maintenance = MaintainAsync();
        }
        catch { await server.DisposeAsync(); throw; }
    }

    internal async Task UpdateAccountsAsync(IEnumerable<WebUiAccount> accounts)
    {
        var next = accounts.ToArray();
        await management.WaitAsync();
        try
        {
            // 目录权限必须通过专用流程更新，不能绕过子进程的任务检查。
            if (next.Any(account => logins.FindAccount(account.Id) is { } previous && (previous.InputDirectory != account.InputDirectory || previous.OutputDirectory != account.OutputDirectory)))
                throw new ArgumentException("Use the workspace directory update operation to change media access.");
            var invalidated = logins.UpdateAccounts(next);
            foreach (var id in invalidated)
            {
                Disconnect(id);
                if (logins.FindAccount(id) is not { Enabled: true })
                    await StopWorkerUnderLockAsync(id, WebSocketCloseStatus.PolicyViolation);
            }
        }
        finally { management.Release(); UsersChanged?.Invoke(); }
    }

    internal IReadOnlyList<WebUiUserStatus> GetUserStatus(IEnumerable<WebUiAccount> accounts) => accounts.Select(account =>
    {
        workers.TryGetValue(account.Id, out var worker);
        var configured = logins.FindAccount(account.Id) ?? account;
        return new WebUiUserStatus(account.Id, configured.UserName, configured.Enabled, worker?.IsRunning == true,
            connections.ContainsKey(account.Id), worker?.IsRunning == true && worker.State.Exporting, worker?.State.Title ?? string.Empty,
            worker?.IsRunning == true ? worker.Workspace.OutputDirectory : configured.OutputDirectory ?? options.Workspace.OutputDirectory, worker?.ProcessId,
            worker?.IsRunning == true ? worker.Workspace.InputDirectory : configured.InputDirectory ?? options.Workspace.InputDirectory, awaitingReconnect.ContainsKey(account.Id));
    }).ToArray();

    internal void UpdateTheme(string next, bool isDark)
    {
        theme = next;
        Volatile.Write(ref dark, isDark ? 1 : 0);
        foreach (var worker in workers.Values)
            worker.SendCommand(next);
    }

    private async Task AppearanceAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        string? css = null;
        if (Authenticate(context) is { } login && workers.TryGetValue(login.UserId, out var worker) && worker.IsRunning && worker.Ready.IsCompletedSuccessfully)
        {
            try
            {
                css = await http.GetStringAsync(new Uri(worker.Ready.Result, "/appearance.css"), context.RequestAborted);
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
        }
        await Results.Text(css ?? (Volatile.Read(ref dark) != 0 ? ":root{color-scheme:dark}" : ":root{color-scheme:light}"), "text/css").ExecuteAsync(context);
    }

    private async Task<WebUiWorker> EnsureWorkerAsync(string id, CancellationToken token)
    {
        WebUiWorker worker;
        await management.WaitAsync(token);
        try
        {
            if (stopping.IsCancellationRequested)
                throw new OperationCanceledException(stopping.Token);
            var account = logins.FindAccount(id);
            if (account is not { Enabled: true })
                throw new UnauthorizedAccessException();
            if (workers.TryGetValue(id, out var previous) && previous.IsRunning)
                worker = previous;
            else
            {
                if (workers.Values.Count(item => item.IsRunning) >= options.MaxWorkers)
                    throw new WorkerLimitException();
                if (previous is not null)
                {
                    workers.TryRemove(id, out _);
                    await previous.DisposeAsync();
                }
                // 目录权限由宿主配置，浏览器只能在所分配的工作区内选择路径。
                var workspace = CreateWorkspace(options.Workspace, account);
                worker = new WebUiWorker(workspace, account.Preferences ?? options.Preferences, theme,
                    () => UsersChanged?.Invoke(), preferences =>
                    {
                        logins.SetPreferences(id, preferences);
                        PreferencesChanged?.Invoke(id, preferences);
                    }, options.Executable, options.Assembly);
                workers[id] = worker;
                try
                {
                    worker.Start();
                    awaitingReconnect.TryRemove(id, out _);
                }
                catch
                {
                    workers.TryRemove(id, out _);
                    await worker.DisposeAsync();
                    throw;
                }
                UsersChanged?.Invoke();
            }
        }
        finally { management.Release(); }
        try
        {
            await worker.Ready.WaitAsync(TimeSpan.FromSeconds(15), token);
        }
        catch (TimeoutException) { await StopWorkerAsync(id); throw; }
        return worker;
    }

    private async Task ConnectAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest || !WebUiHttp.HasSameOrigin(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var login = Authenticate(context);
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        if (login is null)
        {
            await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "Access denied");
            return;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping.Token);
        var connection = new Connection(login.SessionId, cancellation, socket);
        if (!connections.TryAdd(login.UserId, connection))
        {
            await CloseAsync(socket, WebUiHttp.EditorAlreadyConnected, "This account is already connected");
            return;
        }
        UsersChanged?.Invoke();
        try
        {
            cancellation.CancelAfter(TimeSpan.FromSeconds(5));
            var hello = new byte[64];
            var received = await socket.ReceiveAsync(hello.AsMemory(), cancellation.Token);
            if (received.MessageType != WebSocketMessageType.Text || !received.EndOfMessage ||
                !hello.AsSpan(0, received.Count).SequenceEqual("remembered"u8) || Authenticate(context)?.SessionId != login.SessionId)
            {
                await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "Access denied");
                return;
            }
            cancellation.CancelAfter(Timeout.InfiniteTimeSpan);
            var worker = await EnsureWorkerAsync(login.UserId, cancellation.Token);
            using var upstream = new ClientWebSocket();
            upstream.Options.Proxy = null;
            upstream.Options.SetRequestHeader("Origin", worker.Ready.Result.GetLeftPart(UriPartial.Authority));
            var endpoint = new UriBuilder(worker.Ready.Result)
            {
                Scheme = "ws",
                Path = "/connect",
                Query = WebUiHttp.UsesExplicitMedia(context.Request) ? "media=explicit" : string.Empty
            }.Uri;
            await upstream.ConnectAsync(endpoint, cancellation.Token);
            await upstream.SendAsync(Encoding.ASCII.GetBytes(worker.AccessKey).AsMemory(), WebSocketMessageType.Text, true, cancellation.Token);
            var outgoing = RelayAsync(socket, upstream, cancellation.Token);
            var incoming = RelayAsync(upstream, socket, cancellation.Token, connection.Output, () => Volatile.Read(ref connection.Closing) != 0);
            await Task.WhenAny(outgoing, incoming);
            await cancellation.CancelAsync();
            try
            {
                await Task.WhenAll(outgoing, incoming);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        catch (WorkerLimitException)
        {
            await CloseAsync(socket, WorkerLimit, "All editor slots are in use");
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException)
        {
            await CloseAsync(socket, cancellation.IsCancellationRequested ? connection.CloseStatus : WebSocketCloseStatus.InternalServerError, "Connection closed");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"Web editor startup failed: {error.Message}");
            await CloseAsync(socket, WebSocketCloseStatus.InternalServerError, "Could not start the editor");
        }
        finally
        {
            connections.TryRemove(new KeyValuePair<string, Connection>(login.UserId, connection));
            connection.Finished.TrySetResult(true);
            if (workers.TryGetValue(login.UserId, out var worker))
                worker.LastDisconnected = DateTimeOffset.UtcNow;
            UsersChanged?.Invoke();
        }
    }

    private static async Task RelayAsync(WebSocket source, WebSocket destination, CancellationToken token, SemaphoreSlim? output = null, Func<bool>? closing = null)
    {
        var buffer = new byte[32 * 1024];
        var acquired = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var packet = await source.ReceiveAsync(buffer.AsMemory(), token);
                if (output is not null && !acquired)
                {
                    await output.WaitAsync(token);
                    acquired = true;
                    if (closing?.Invoke() == true)
                    {
                        output.Release();
                        acquired = false;
                        await Task.Delay(Timeout.Infinite, token);
                    }
                }
                if (packet.MessageType == WebSocketMessageType.Close)
                {
                    await destination.CloseOutputAsync(source.CloseStatus ?? WebSocketCloseStatus.NormalClosure, source.CloseStatusDescription, token);
                    return;
                }
                // 整条消息转发结束后才允许发关闭帧，避免目录切换打断二进制消息的分片。
                await destination.SendAsync(buffer.AsMemory(0, packet.Count), packet.MessageType, packet.EndOfMessage, token);
                if (acquired && packet.EndOfMessage)
                {
                    output!.Release();
                    acquired = false;
                }
            }
        }
        finally
        {
            if (acquired)
                output!.Release();
        }
    }

    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                // 等待关闭握手，避免尚未读取的客户端首包让连接被重置、丢失拒绝原因。
                await socket.CloseAsync(status, reason, timeout.Token);
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException) { socket.Abort(); }
    }

    private void Disconnect(string id, WebSocketCloseStatus status = WebSocketCloseStatus.PolicyViolation)
    {
        if (connections.TryGetValue(id, out var connection))
        {
            connection.CloseStatus = status;
            try
            {
                connection.Cancellation.Cancel();
            }
            catch (ObjectDisposedException) { }
        }
    }

    internal async Task StopWorkerAsync(string id, WebSocketCloseStatus status = WebSocketCloseStatus.EndpointUnavailable, bool reclaimIdle = false)
    {
        await management.WaitAsync();
        try
        {
            if (reclaimIdle && (!workers.TryGetValue(id, out var idle) || connections.ContainsKey(id) ||
                    idle.State.HasWorkspace || idle.State.Exporting || DateTimeOffset.UtcNow - idle.LastDisconnected < TimeSpan.FromMinutes(30)))
                return;
            await StopWorkerUnderLockAsync(id, status);
        }
        finally { management.Release(); UsersChanged?.Invoke(); }
    }

    private async Task StopWorkerUnderLockAsync(string id, WebSocketCloseStatus status)
    {
        Disconnect(id, status);
        awaitingReconnect.TryRemove(id, out _);
        if (workers.TryRemove(id, out var worker))
            await worker.DisposeAsync();
    }

    private async Task MaintainAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stopping.Token))
                foreach (var (id, worker) in workers)
                    // 已载入素材、工程或后台任务的工作区不自动回收，避免丢失未保存编辑。
                    if (!connections.ContainsKey(id) && !worker.State.HasWorkspace && !worker.State.Exporting &&
                        DateTimeOffset.UtcNow - worker.LastDisconnected >= TimeSpan.FromMinutes(30))
                        await StopWorkerAsync(id, reclaimIdle: true);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    internal void StopWithOwner()
    {
        stopping.Cancel();
        foreach (var worker in workers.Values)
            worker.CloseInput();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        StopWithOwner();
        if (maintenance is not null)
            await maintenance;
        await management.WaitAsync();
        try
        {
            var remaining = workers.Values.ToArray();
            workers.Clear();
            await Task.WhenAll(remaining.Select(worker => worker.DisposeAsync().AsTask()));
        }
        finally { management.Release(); }
        if (app is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await app.StopAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
            finally { await app.DisposeAsync(); app = null; }
        }
        http.Dispose();
    }

    private sealed class WorkerLimitException : Exception;
}
