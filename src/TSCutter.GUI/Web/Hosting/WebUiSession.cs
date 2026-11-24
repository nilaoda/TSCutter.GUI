using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using TSCutter.GUI.Player.Audio;
using TSCutter.GUI.Services.FrameSearch;
using TSCutter.GUI.Utils.Files;

namespace TSCutter.GUI.Web;

internal sealed partial class WebUiSession(MediaWorkspace workspace, int port, string? accessKey, bool allowLan, bool enableIpv6 = false)
{
    internal const WebSocketCloseStatus EditorAlreadyConnected = WebUiHttp.EditorAlreadyConnected;
    internal const string TitlePrefix = "title:";
    internal const string ViewportPrefix = "viewport:";
    internal static readonly Meter FrameMeter = new("TSCutter.WebUi");
    private static readonly Histogram<double> CaptureDuration = FrameMeter.CreateHistogram<double>("webui.capture.duration", "ms");
    private static readonly Histogram<double> VideoSendDuration = FrameMeter.CreateHistogram<double>("webui.video.send.duration", "ms");
    private static readonly Histogram<double> BrowserPresentDuration = FrameMeter.CreateHistogram<double>("webui.video.browser.present.duration", "ms");
    private static readonly Histogram<double> BrowserDecodeDuration = FrameMeter.CreateHistogram<double>("webui.video.browser.decode.duration", "ms");
    private static readonly Histogram<double> BrowserBufferDuration = FrameMeter.CreateHistogram<double>("webui.video.browser.buffered.duration", "ms");
    private static readonly Histogram<double> BrowserPendingFragments = FrameMeter.CreateHistogram<double>("webui.video.browser.pending.fragments");
    private const int MaxMessageSize = 8192;
    private static readonly TimeSpan AudioInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(40);
    // 采集略快于常见的 30 fps 素材，减少两边时钟错开时漏掉新画面的机会。
    private static readonly TimeSpan Av1FrameInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan H264FrameInterval = TimeSpan.FromSeconds(1.0 / 60);
    private static readonly TimeSpan RestingCaptureInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IdleAfter = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan[] IdleRefreshAfter = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300),
        TimeSpan.FromMilliseconds(600), TimeSpan.FromSeconds(1)];
    private readonly byte[]? expectedKey = accessKey is null ? null : Encoding.UTF8.GetBytes(accessKey);
    private readonly WebUiPairing pairing = new(Environment.GetEnvironmentVariable("TSCUTTER_WEB_MANAGED") == "1");
    private int connected;
    private int audioRequested = 1;
    private WebUiAudioOutput? audioOutput;

    public static WebUiSession? Current
    {
        get; private set;
    }
    public MediaWorkspace Workspace { get; } = workspace;
    internal SavedFrameStore SavedFrames { get; } = SavedFrameStore.CreateInMemory();

    public IAudioOutput CreateAudioOutput()
    {
        var output = new WebUiAudioOutput();
        Interlocked.Exchange(ref audioOutput, output);
        return output;
    }

    public static WebUiSession Configure(string[] args)
    {
        if (args.Length is < 4 or > 6 || args.Skip(4).Any(flag => flag is not ("--lan" or "--ipv6")) ||
            args.Skip(4).Distinct().Count() != args.Length - 4 ||
            args.Skip(4).Contains("--ipv6") && !args.Skip(4).Contains("--lan") ||
            !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            (port != 0 && port is < 1024 or > 65535))
            throw new ArgumentException("Usage: --web-ui <port> <input-directory> <output-directory> [--lan [--ipv6]]");

        var configuredKey = Environment.GetEnvironmentVariable("TSCUTTER_WEB_ACCESS_KEY");
        if (configuredKey is not null && (configuredKey.Length != 64 || !configuredKey.All(Uri.IsHexDigit)))
            throw new ArgumentException("TSCUTTER_WEB_ACCESS_KEY must contain 64 hexadecimal characters.");

        Current = new WebUiSession(new MediaWorkspace(args[2], args[3]), port,
            configuredKey, args.Skip(4).Contains("--lan"), args.Skip(4).Contains("--ipv6"));
        return Current;
    }

    public void Start(IClassicDesktopStyleApplicationLifetime lifetime, Window mainWindow)
    {
        var desktop = new WebUiDesktop(lifetime, mainWindow);
        desktop.Initialize();
        var startupLanguage = Environment.GetEnvironmentVariable(WebUiPreferences.EnvironmentVariable) is not null
            ? App.ConfigService?.CurrentConfig.Language : null;
        var stopping = new CancellationTokenSource();
        lifetime.Exit += (_, _) => { stopping.Cancel(); desktop.Dispose(); };
        StartWorkerReporting(lifetime, mainWindow, stopping.Token);
        var managed = Environment.GetEnvironmentVariable("TSCUTTER_WEB_MANAGED") is "1" or WebUiWorker.ManagedValue;
        _ = Task.Run(async () =>
        {
            while (await Console.In.ReadLineAsync() is { } command)
            {
                try
                {
                    if (command == "snapshot")
                        await Dispatcher.UIThread.InvokeAsync(() => PublishWorkerSnapshot(lifetime, mainWindow));
                    else if (managed && command.StartsWith(WebUiWorkspaceProtocol.Prepare, StringComparison.Ordinal))
                        await Dispatcher.UIThread.InvokeAsync(() => PrepareWorkspaceChange(command[WebUiWorkspaceProtocol.Prepare.Length..], lifetime, mainWindow, desktop), DispatcherPriority.Send);
                    else if (managed && command.StartsWith(WebUiWorkspaceProtocol.Resume, StringComparison.Ordinal))
                        await Dispatcher.UIThread.InvokeAsync(() => ResumeWorkspaceInput(command[WebUiWorkspaceProtocol.Resume.Length..]), DispatcherPriority.Send);
                    else if (command == WebUiPairing.PairCommand)
                        pairing.Rotate();
                    else if (command.StartsWith(WebUiPairing.RevokeCommand, StringComparison.Ordinal))
                        pairing.Revoke(command[WebUiPairing.RevokeCommand.Length..]);
                    else if (managed)
                        await Dispatcher.UIThread.InvokeAsync(() => App.ConfigService?.ApplyTheme(command));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Console.WriteLine("Web UI pairing error");
                }
            }
            if (managed)
                Dispatcher.UIThread.Post(() => lifetime.Shutdown());
        });
        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(desktop, startupLanguage, managed, stopping.Token);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
                Dispatcher.UIThread.Post(() => lifetime.Shutdown(1));
            }
        });
    }

    private async Task RunAsync(WebUiDesktop desktop, string? startupLanguage, bool managed, CancellationToken token)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            WebUiHttp.ConfigureListener(options, allowLan, port, enableIpv6);
        });

        await using var app = builder.Build();
        if (Environment.GetEnvironmentVariable("TSCUTTER_WEB_TRUST_LOOPBACK_PROXY") == "1")
        {
            // 仅采纳同机受信代理转发的协议，局域网客户端不能用请求头冒充 HTTPS。
            app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto });
        }
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(15),
            KeepAliveTimeout = TimeSpan.FromSeconds(15)
        });
        // 直接注册 RequestDelegate，使桌面发布不依赖运行时生成路由绑定代码。
        app.MapGet("/", (RequestDelegate)(context => ServeAsset(context, "index.html", "text/html; charset=utf-8").ExecuteAsync(context)));
        app.MapGet("/remote.js", (RequestDelegate)(context => ServeAsset(context, "remote.js", "text/javascript; charset=utf-8").ExecuteAsync(context)));
        app.MapGet("/touchpad.js", (RequestDelegate)(context => ServeAsset(context, "touchpad.js", "text/javascript; charset=utf-8").ExecuteAsync(context)));
        app.MapGet("/viewport.js", (RequestDelegate)(context => ServeAsset(context, "viewport.js", "text/javascript; charset=utf-8").ExecuteAsync(context)));
        app.MapGet("/video-playback.js", (RequestDelegate)(context => ServeAsset(context, "video-playback.js", "text/javascript; charset=utf-8").ExecuteAsync(context)));
        app.MapGet("/appearance.css", async (HttpContext context) =>
        {
            var variant = await Dispatcher.UIThread.InvokeAsync(() => Avalonia.Application.Current!.ActualThemeVariant);
            var dark = variant == Avalonia.Styling.ThemeVariant.Dark || variant.InheritVariant == Avalonia.Styling.ThemeVariant.Dark;
            context.Response.Headers.CacheControl = "no-store";
            await Results.Text(dark ? ":root{color-scheme:dark}" : ":root{color-scheme:light}", "text/css").ExecuteAsync(context);
        });
        app.MapGet("/startup-locale", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Text(IsSupportedLocale(startupLanguage) ? startupLanguage : string.Empty).ExecuteAsync(context);
        });
        app.MapGet("/remote.css", (RequestDelegate)(context => ServeAsset(context, "remote.css", "text/css; charset=utf-8").ExecuteAsync(context)));
        app.MapGet("/locales/{locale}.json", (RequestDelegate)(context =>
        {
            var locale = context.Request.RouteValues["locale"] as string;
            var result = IsSupportedLocale(locale)
                ? ServeAsset(context, $"Locales.{locale}.json", "application/json; charset=utf-8") : Results.NotFound();
            return result.ExecuteAsync(context);
        }));
        app.MapGet("/auth", (RequestDelegate)(context => AuthStatus(context).ExecuteAsync(context)));
        app.MapPost("/pair", (RequestDelegate)(async context =>
        {
            var result = await PairAsync(context);
            await result.ExecuteAsync(context);
        }));
        app.MapPost("/logout", (RequestDelegate)(context => Logout(context).ExecuteAsync(context)));
        // HTTPS 下 Chromium 可以使用 HTTP/2 扩展 CONNECT 建立 WebSocket。
        app.MapMethods("/connect", [HttpMethods.Get, HttpMethods.Connect],
            (HttpContext context) => ConnectAsync(context, desktop, token));
        await app.StartAsync(token);
        var listeningPort = port == 0 ? new Uri(app.Urls.Single()).Port : port;
        var addresses = GetAddresses(allowLan, enableIpv6).Select(address => address.ToString());
        foreach (var address in addresses)
        {
            var url = new UriBuilder("http", address, listeningPort) { Fragment = accessKey ?? string.Empty };
            Console.WriteLine($"Web UI: {url.Uri.AbsoluteUri}");
        }
        if (accessKey is null)
            pairing.Publish();
        await app.WaitForShutdownAsync(token);
    }

    internal static IPAddress[] GetAddresses(bool allowLan, bool enableIpv6 = false) => WebUiHttp.GetAddresses(allowLan, enableIpv6);

    internal static bool IsSupportedLocale(string? locale) => WebUiHttp.IsSupportedLocale(locale);

    private static IResult ServeAsset(HttpContext context, string asset, string contentType) =>
        WebUiHttp.ServeAsset(context, asset, contentType);

    private async Task ConnectAsync(HttpContext context, WebUiDesktop desktop, CancellationToken stoppingToken)
    {
        if (!context.WebSockets.IsWebSocketRequest || !HasSameOrigin(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stoppingToken);
        connection.CancelAfter(TimeSpan.FromSeconds(5));
        var token = connection.Token;
        var buffer = new byte[MaxMessageSize];
        var ownsDesktop = false;
        try
        {
            var keyLength = await ReceiveAsync(socket, buffer, token);
            var credential = keyLength > 0 ? Encoding.UTF8.GetString(buffer.AsSpan(0, keyLength)) : string.Empty;
            var device = credential == Remembered ? GetRememberedBrowser(context) : null;
            var legacy = expectedKey is not null && keyLength == expectedKey.Length &&
                CryptographicOperations.FixedTimeEquals(buffer.AsSpan(0, keyLength), expectedKey);
            if (device is null && !legacy)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Access denied", token);
                return;
            }
            // 页面刷新时，旧连接可能还在结束编码和释放输入，短暂等待其完成再判定占用。
            for (var retry = 0; Interlocked.CompareExchange(ref connected, 1, 0) != 0; ++retry)
            {
                if (retry >= 10)
                {
                    await socket.CloseOutputAsync(EditorAlreadyConnected, "Another editor is connected", token);
                    return;
                }
                await Task.Delay(50, token);
            }

            ownsDesktop = true;
            // 新网页在 ready 后同步声音、画质和视频模式，等待选择可以省去未使用的首屏 JPEG。
            var explicitMedia = WebUiHttp.UsesExplicitMedia(context.Request);
            Volatile.Write(ref audioRequested, explicitMedia ? 0 : 1);
            connection.CancelAfter(Timeout.InfiniteTimeSpan);
            if (device is not null && !pairing.Attach(device.Id, connection))
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Access denied", token);
                return;
            }
            await socket.SendAsync("ready"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, token);
            // 链路拥塞时只保留最新一帧，避免继续回传已经过时的画面。
            var frames = Channel.CreateBounded<DesktopUpdate>(new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.DropOldest
            });
            using var videoSocket = new WebUiVideoSocket(initialJpeg: !explicitMedia);
            var capturing = CaptureFramesAsync(desktop, frames.Writer, videoSocket, token);
            // 光标只保留最新状态，不等待画面捕获或编码。
            var cursors = Channel.CreateBounded<string>(new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.DropOldest
            });
            var sending = SendMediaAsync(socket, frames.Reader, cursors.Reader, videoSocket, token);
            var receiving = ReceiveInputAsync(socket, buffer, desktop, cursors.Writer, videoSocket, token);
            await Task.WhenAny(capturing, sending, receiving);
            await connection.CancelAsync();
            await Task.WhenAll(capturing, sending, receiving);
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or JsonException)
        {
            socket.Abort();
        }
        finally
        {
            pairing.Detach(connection);
            if (ownsDesktop)
            {
                try
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        desktop.ResetInput();
                        App.MainWindow.PauseWebUiPreview();
                    }, DispatcherPriority.Input, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                }
                finally
                {
                    Volatile.Write(ref connected, 0);
                }
            }
        }
    }

    private static async Task<int> ReceiveAsync(WebSocket socket, byte[] buffer, CancellationToken token)
    {
        var length = 0;
        while (length < buffer.Length)
        {
            var chunk = await socket.ReceiveAsync(buffer.AsMemory(length), token);
            if (chunk.MessageType != WebSocketMessageType.Text)
                return -1;
            length += chunk.Count;
            if (chunk.EndOfMessage)
                return length;
        }
        return -1;
    }

    private async Task ReceiveInputAsync(WebSocket socket, byte[] buffer, WebUiDesktop desktop,
        ChannelWriter<string> cursors, WebUiVideoSocket videoSocket, CancellationToken token)
    {
        var cursorEnabled = false;
        void SendCursor(string style)
        {
            if (cursorEnabled)
                cursors.TryWrite(style);
        }
        desktop.CursorChanged += SendCursor;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var length = await ReceiveAsync(socket, buffer, token);
                if (length <= 0)
                    break;
                using var message = JsonDocument.Parse(buffer.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 4 });
                var input = message.RootElement;
                if (input.ValueKind != JsonValueKind.Object)
                    continue;
                if (input.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
                {
                    switch (type.GetString())
                    {
                        case "cursor":
                            // 客户端显式订阅，旧客户端的消息顺序保持不变。
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                cursorEnabled = input.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True;
                                if (cursorEnabled)
                                    cursors.TryWrite(desktop.CursorStyle);
                            }, DispatcherPriority.Input, token);
                            continue;
                        case "audio":
                            if (input.TryGetProperty("enabled", out var audio) && audio.ValueKind is JsonValueKind.True or JsonValueKind.False)
                                Volatile.Write(ref audioRequested, audio.ValueKind == JsonValueKind.True ? 1 : 0);
                            continue;
                        case "video-start":
                            if (TryReadStreamId(input, out var startId) &&
                                input.TryGetProperty("codec", out var codecValue) && codecValue.ValueKind == JsonValueKind.String &&
                                codecValue.GetString() is { } codec && codec is ("av1" or "h264" or "jpeg"))
                                videoSocket.Start(startId, codec,
                                    input.TryGetProperty("feedback", out var feedback) && feedback.ValueKind == JsonValueKind.True,
                                    input.TryGetProperty("manualRecovery", out var manual) && manual.ValueKind == JsonValueKind.True);
                            continue;
                        case "video-stop":
                            if (TryReadStreamId(input, out var stopId))
                                videoSocket.Stop(stopId);
                            continue;
                        case "video-playback":
                            if (TryReadStreamId(input, out var reportId) && videoSocket.Stream?.Id == reportId)
                            {
                                RecordPlaybackMetric(input, "presentMs", BrowserPresentDuration, 60_000);
                                RecordPlaybackMetric(input, "decodeMs", BrowserDecodeDuration, 60_000);
                                RecordPlaybackMetric(input, "bufferedMs", BrowserBufferDuration, 60_000);
                                RecordPlaybackMetric(input, "pending", BrowserPendingFragments, 90);
                                videoSocket.ReportPlayback(input);
                            }
                            continue;
                    }
                }
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (workspaceChangeRequest is null)
                        desktop.HandleInput(input);
                }, DispatcherPriority.Input, token);
            }
        }
        finally { desktop.CursorChanged -= SendCursor; }
    }

    private static bool TryReadStreamId(JsonElement message, out int id)
    {
        id = 0;
        return message.TryGetProperty("streamId", out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out id) && id > 0;
    }

    private static void RecordPlaybackMetric(JsonElement report, string name,
        Histogram<double> metric, double maximum)
    {
        if (metric.Enabled && report.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out var number) && number >= 0 && number <= maximum)
            metric.Record(number);
    }

    private sealed record DesktopUpdate(string Title, PixelSize Viewport, byte[]? Frame);

    private static async Task CaptureFramesAsync(WebUiDesktop desktop, ChannelWriter<DesktopUpdate> updates,
        WebUiVideoSocket videoSocket, CancellationToken token)
    {
        byte[]? previousFrame = null;
        string? previousTitle = null;
        var previousViewport = default(PixelSize);
        var previousQuality = 0;
        WebUiVideoTransport? previousTransport = null;
        var primingFrames = 0;
        var refreshStep = IdleRefreshAfter.Length;
        WebUiMediaEncoder? encoder = null;
        var encoding = Task.CompletedTask;
        var lastVisualChange = Stopwatch.GetTimestamp();
        long lastCapture = 0;
        long lastMetadataCheck = 0;
        long capturedScene = -1;
        long? sceneFollowupAt = null;
        using var timer = new PeriodicTimer(FrameInterval);
        using var buffers = new WebUiFrameBuffers();
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                var transport = videoSocket.Transport;
                var stream = transport.Stream;
                // JPEG 和停止状态都没有视频流，也必须按请求变化补齐用户主动选择后的首帧。
                var changedTransport = !ReferenceEquals(transport, previousTransport);
                if (changedTransport)
                {
                    await encoding;
                    videoSocket.Reset();
                    previousFrame = null;
                    primingFrames = stream is not null ? 3 : 0;
                    refreshStep = IdleRefreshAfter.Length;
                    previousTransport = transport;
                }

                var interval = stream?.Codec switch
                {
                    "h264" => H264FrameInterval,
                    "av1" => Av1FrameInterval,
                    _ => FrameInterval
                };
                if (stream is not null)
                    interval = videoSocket.Feedback.CaptureInterval(interval);
                if (timer.Period != interval)
                    timer.Period = interval;
                var now = Stopwatch.GetTimestamp();
                // 长时间没有有效回执时停止视频并通知网页，不能永远停在被耗尽的发送预算上。
                if (stream is not null && videoSocket.Feedback.HasStalled(stream, now))
                {
                    videoSocket.Stop(stream.Id, notify: true, reason: "feedback-timeout");
                    continue;
                }
                // 拥塞时在合成和编码前跳过，不把陈旧画面堵在待发送队列中。
                if (stream is null && !transport.JpegEnabled ||
                    stream is not null && (!videoSocket.Feedback.CanCapture(stream, now) || videoSocket.Frames.Reader.Count >= 2))
                {
                    // 暂停的只是画面，后台载入完成等标题变化仍应更新到浏览器标签。
                    if (Stopwatch.GetElapsedTime(lastMetadataCheck, now) >= TimeSpan.FromMilliseconds(500))
                    {
                        lastMetadataCheck = now;
                        var metadataTitle = await Dispatcher.UIThread.InvokeAsync(() => desktop.Title, DispatcherPriority.Background, token);
                        if (metadataTitle != previousTitle)
                        {
                            previousTitle = metadataTitle;
                            updates.TryWrite(new DesktopUpdate(metadataTitle, previousViewport, null));
                        }
                    }
                    continue;
                }
                var resumed = stream is not null && videoSocket.Feedback.TakeRefreshRequest();
                if (resumed)
                {
                    lastVisualChange = now;
                    refreshStep = 0;
                }
                var refreshDue = stream is not null && refreshStep < IdleRefreshAfter.Length &&
                    Stopwatch.GetElapsedTime(lastVisualChange, now) >= IdleRefreshAfter[refreshStep];
                var force = changedTransport || primingFrames > 0 || resumed || refreshDue || (stream is null && previousFrame is null);
                var sceneVersion = desktop.SceneVersion;
                var sceneChanged = sceneVersion != capturedScene;
                var sceneFollowup = sceneFollowupAt is { } followup &&
                    Stopwatch.GetElapsedTime(followup, now) >= TimeSpan.FromMilliseconds(50);
                if (!force && !sceneChanged && !sceneFollowup)
                {
                    // 新场景立即采集，连续变化时保持频率；静止后省去整屏合成，低频兜底自绘内容。
                    var active = Stopwatch.GetElapsedTime(desktop.LastInputTimestamp, now) < IdleAfter ||
                        Stopwatch.GetElapsedTime(lastVisualChange, now) < IdleAfter;
                    if (!active && Stopwatch.GetElapsedTime(lastCapture, now) < RestingCaptureInterval)
                        continue;
                }
                lastCapture = now;

                var videoLimit = stream is not null ? (PixelSize?)WebUiMediaEncoder.VideoLimit : null;
                var (title, captured) = await Dispatcher.UIThread.InvokeAsync(
                    () =>
                    {
                        var start = CaptureDuration.Enabled ? Stopwatch.GetTimestamp() : 0;
                        var result = desktop.Capture(videoLimit, reset: capturedScene < 0);
                        if (start != 0)
                            CaptureDuration.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                        return (desktop.Title, result);
                    }, DispatcherPriority.Background, token);
                using var capturedFrame = captured;
                var rendered = captured.Rendered;
                var viewport = captured.Viewport;
                var quality = captured.JpegQuality;
                var bitrate = stream is not null ? videoSocket.Feedback.VideoBitrate(stream, captured.VideoBitrate) : captured.VideoBitrate;
                var viewportChanged = rendered && previousViewport != viewport;
                if (rendered)
                {
                    capturedScene = sceneVersion;
                    // 场景提交通知可能早于离屏位图更新，变化停止后再采一次收尾画面。
                    sceneFollowupAt = sceneChanged ? Stopwatch.GetTimestamp() : sceneFollowup ? null : sceneFollowupAt;
                }
                // 后台合成与上一帧编码重叠，UI 线程只记录指令；每次循环最多持有一份场景。
                if (rendered)
                    buffers.Rasterize(captured);
                // 等待过久的采集结果由下一轮的新画面替代。
                var waiting = Stopwatch.GetTimestamp();
                await encoding;
                if (stream is not null && Stopwatch.GetElapsedTime(waiting) > TimeSpan.FromMilliseconds(100))
                    continue;
                if (stream is not null && (stream != videoSocket.Stream || videoSocket.Frames.Reader.Count >= 2 ||
                    !videoSocket.Feedback.CanCapture(stream, Stopwatch.GetTimestamp())))
                    continue;
                var pixelsChanged = false;
                var qualityChanged = quality != previousQuality;
                var image = rendered ? buffers.SnapshotIfChanged(force || viewportChanged || qualityChanged, out pixelsChanged) : null;
                var frameChanged = false;
                if (image is not null)
                {
                    previousQuality = quality;
                    if (pixelsChanged || qualityChanged || viewportChanged)
                    {
                        lastVisualChange = now;
                        refreshStep = 0;
                    }
                    else if (refreshDue)
                        ++refreshStep;
                    // 补帧不会延长活跃期；静止一秒后仍按原来的低频采样节省资源。
                    if (stream is not null)
                    {
                        encoding = Task.Run(() =>
                        {
                            using (image)
                            {
                                try
                                {
                                    encoder ??= new WebUiMediaEncoder();
                                    encoder.Video(image, bitrate, stream, videoSocket);
                                }
                                catch (Exception error) when (WebUiMediaEncoder.IsMediaError(error))
                                {
                                    Console.Error.WriteLine($"Video WebSocket encoding failed ({stream.Codec}): {error.Message}");
                                    videoSocket.Stop(stream.Id, notify: true);
                                }
                            }
                        });
                        if (primingFrames > 0)
                            --primingFrames;
                    }
                    else
                    {
                        using var jpegImage = image;
                        using var encoded = jpegImage.Encode(SKEncodedImageFormat.Jpeg, quality);
                        frameChanged = previousFrame is null || !encoded.AsSpan().SequenceEqual(previousFrame);
                        if (frameChanged)
                            previousFrame = encoded.ToArray();
                    }
                }

                if (viewportChanged)
                    previousViewport = viewport;
                if (frameChanged || viewportChanged || title != previousTitle)
                {
                    previousTitle = title;
                    updates.TryWrite(new DesktopUpdate(title, previousViewport, stream is not null ? null : previousFrame));
                }
            }
        }
        finally
        {
            try
            {
                await encoding;
            }
            finally { encoder?.Dispose(); }
        }
    }

    private async Task SendMediaAsync(WebSocket socket, ChannelReader<DesktopUpdate> updates,
        ChannelReader<string> cursors, WebUiVideoSocket videoSocket, CancellationToken token)
    {
        string? sentTitle = null;
        byte[]? sentFrame = null;
        var sentViewport = default(PixelSize);
        var audio = new byte[2 + WebUiAudioOutput.PacketFrames * AudioOutputFormat.Channels * sizeof(short)];
        using var timer = new PeriodicTimer(AudioInterval);
        var nextAudio = timer.WaitForNextTickAsync(token).AsTask();
        var nextUpdate = updates.WaitToReadAsync(token).AsTask();
        var nextCursor = cursors.WaitToReadAsync(token).AsTask();
        var socketFrames = videoSocket.Frames.Reader;
        var nextVideo = socketFrames.WaitToReadAsync(token).AsTask();
        async ValueTask NotifyVideoFallbackAsync()
        {
            var fallback = videoSocket.TakeFallback();
            if (fallback is not null)
            {
                // 原回退消息保持不变；旧网页忽略诊断消息后仍可正常切回 JPEG。
                await SendTextAsync(socket, FormattableString.Invariant($"video-failure:{fallback.StreamId}:{fallback.Reason}"), token);
                await SendTextAsync(socket, FormattableString.Invariant($"video-fallback:{fallback.StreamId}"), token);
            }
        }

        while (!token.IsCancellationRequested)
        {
            await Task.WhenAny(nextAudio, nextUpdate, nextVideo, nextCursor);
            if (nextCursor.IsCompleted)
            {
                if (!await nextCursor)
                    return;
                if (cursors.TryRead(out var cursor))
                    await SendTextAsync(socket, "cursor:" + cursor, token);
                nextCursor = cursors.WaitToReadAsync(token).AsTask();
            }
            await NotifyVideoFallbackAsync();
            if (nextAudio.IsCompleted)
            {
                if (!await nextAudio)
                    return;
                if (Volatile.Read(ref audioOutput) is { } output)
                {
                    // 静音时仍消费音频以推进宿主播放时钟，但不把无人收听的 PCM 送上网络。
                    var length = output.ReadPacket(audio, Volatile.Read(ref audioRequested) != 0);
                    if (length != 0 && Volatile.Read(ref audioRequested) != 0)
                        await socket.SendAsync(audio.AsMemory(0, length), WebSocketMessageType.Binary, true, token);
                }
                nextAudio = timer.WaitForNextTickAsync(token).AsTask();
            }

            if (nextUpdate.IsCompleted)
            {
                if (!await nextUpdate)
                    return;
                if (updates.TryRead(out var update))
                {
                    if (update.Title != sentTitle)
                    {
                        await SendTextAsync(socket, TitlePrefix + update.Title, token);
                        sentTitle = update.Title;
                    }
                    var resized = update.Viewport != sentViewport && update.Viewport.Width > 0;
                    if (resized)
                    {
                        await SendTextAsync(socket, FormattableString.Invariant(
                            $"{ViewportPrefix}{update.Viewport.Width},{update.Viewport.Height}"), token);
                        sentViewport = update.Viewport;
                    }
                    if (update.Frame is not null && videoSocket.Transport is { Stream: null, JpegEnabled: true } &&
                        (resized || !ReferenceEquals(update.Frame, sentFrame)))
                    {
                        // 回退可能在等待音频或标题写完时发生，首张 JPEG 之前必须通知浏览器。
                        await NotifyVideoFallbackAsync();
                        await socket.SendAsync(update.Frame.AsMemory(), WebSocketMessageType.Binary, true, token);
                        sentFrame = update.Frame;
                    }
                }
                nextUpdate = updates.WaitToReadAsync(token).AsTask();
            }

            if (nextVideo.IsCompleted)
            {
                if (!await nextVideo)
                    return;
                if (socketFrames.TryRead(out var frame) && videoSocket.IsCurrent(frame))
                {
                    var start = VideoSendDuration.Enabled ? Stopwatch.GetTimestamp() : 0;
                    await socket.SendAsync(frame, WebSocketMessageType.Binary, true, token);
                    if (start != 0)
                        VideoSendDuration.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                }
                nextVideo = socketFrames.WaitToReadAsync(token).AsTask();
            }
        }
    }

    private static ValueTask SendTextAsync(WebSocket socket, string text, CancellationToken token) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text).AsMemory(), WebSocketMessageType.Text, true, token);
}
