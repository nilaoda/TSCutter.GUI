using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SkiaSharp;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.FrameSearch;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.Utils.Files;
using TSCutter.GUI.Web;
using Xunit;

namespace TSCutter.GUI.Tests.Application;

public sealed class WebUiTests
{
    [Fact]
    public void SharedAddressesIncludeIpv6WithoutUnusableScopedLinks()
    {
        Assert.True(WebUiHttp.IsShareableAddress(IPAddress.Parse("192.0.2.1")));
        Assert.True(WebUiHttp.IsShareableAddress(IPAddress.Parse("2001:db8::1")));
        Assert.True(WebUiHttp.IsShareableAddress(IPAddress.Parse("fd42::1")));
        foreach (var text in new[] { "127.0.0.1", "::1", "0.0.0.0", "::", "fe80::1%3", "ff02::1", "::ffff:192.0.2.1" })
            Assert.False(WebUiHttp.IsShareableAddress(IPAddress.Parse(text)));
        Assert.Equal(new[] { IPAddress.Loopback }, WebUiHttp.GetAddresses(false));
        Assert.Equal(IPAddress.Loopback, WebUiHttp.GetAddresses(true)[0]);
        Assert.All(WebUiHttp.GetAddresses(true), address => Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, address.AddressFamily));
        Assert.Equal("http://[2001:db8::1]:5360/", new UriBuilder("http", "2001:db8::1", 5360).Uri.AbsoluteUri);
    }

    [Fact]
    public void StartupPreferencesInheritActiveEditModesAndKeepDesktopHistoryPrivate()
    {
        var config = new AppConfig
        {
            ClipCutMode = ClipCutMode.KeyFrame,
            ClipListMode = ClipListMode.Keep,
            Language = "zh-TW",
            ShowKeyFrameStrip = false,
            TsZeroStartTime = true,
            LastImportFolder = Path.GetFullPath("private-media"),
            LastExportFolder = Path.GetFullPath("private-exports"),
            WebUiInputDirectory = Path.GetFullPath("previous-shared-root"),
            WebUiAccounts = [new WebUiAccount { UserName = "private-account", PasswordHash = "private-hash" }]
        };
        var json = WebUiPreferences.CreateSnapshot(config, ClipCutMode.FrameAccurate, ClipListMode.Remove);
        var snapshot = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppConfig)!;

        Assert.Equal(ClipCutMode.FrameAccurate, snapshot.ClipCutMode);
        Assert.Equal(ClipListMode.Remove, snapshot.ClipListMode);
        Assert.Equal("zh-TW", snapshot.Language);
        Assert.False(snapshot.AutoDetectLanguage);
        Assert.False(snapshot.ShowKeyFrameStrip);
        Assert.True(snapshot.TsZeroStartTime);
        Assert.Null(snapshot.LastImportFolder);
        Assert.Null(snapshot.LastExportFolder);
        Assert.Null(snapshot.WebUiInputDirectory);
        Assert.Empty(snapshot.WebUiAccounts);
        Assert.False(snapshot.RememberRecentItems);
        Assert.False(snapshot.AutoSaveRecovery);
        Assert.Equal(ClipCutMode.KeyFrame, config.ClipCutMode);
        Assert.NotNull(config.LastImportFolder);
    }

    [Fact]
    public void WorkspaceSeparatesInputsAndOutputsAndRejectsTraversal()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var input = root.CreateSubdirectory("media");
            var output = root.CreateSubdirectory("output");
            var source = Path.Combine(input.FullName, "source.ts");
            File.WriteAllText(source, "source");

            var workspace = new MediaWorkspace(input.FullName, output.FullName);
            Assert.Equal(Path.Combine(workspace.InputDirectory, "source.ts"), workspace.ResolveInput("source.ts"));
            Assert.Equal(Path.Combine(workspace.OutputDirectory, "clip.ts"), workspace.ResolveOutput("clip.ts"));
            Assert.Null(workspace.ResolveOutput("../media/source.ts"));
            Assert.Null(workspace.ResolveInput(source));
            Assert.False(workspace.AllowsOutput(source));
            var overlap = Assert.Throws<MediaWorkspaceException>(() => new MediaWorkspace(root.FullName, output.FullName));
            Assert.Equal(MediaWorkspaceError.OverlappingDirectories, overlap.Error);

            var temporary = Path.Combine(workspace.OutputDirectory, "clip.partial");
            var destination = Path.Combine(workspace.OutputDirectory, "clip.ts");
            File.WriteAllText(temporary, "new");
            File.WriteAllText(destination, "existing");
            var restrictedAccess = new MediaFileAccess(workspace, error => error.ToString());
            var outside = Path.Combine(root.FullName, "outside.ts");
            File.WriteAllText(outside, "outside");
            restrictedAccess.DemandReadable(source);
            restrictedAccess.DemandReadable(destination);
            restrictedAccess.DemandProjectPaths([source], [destination]);
            // 已移动的文件和目录应进入工程重新定位流程，而不是被误判为越界。
            foreach (var relative in new[] { "missing.ts", Path.Combine("moved", "source.ts") })
            {
                var missingSource = Path.Combine(workspace.InputDirectory, relative);
                Assert.Null(workspace.ResolveInput(relative));
                restrictedAccess.DemandProjectPaths([missingSource], [destination]);
                restrictedAccess.DemandReadable(missingSource);
            }
            Assert.Throws<UnauthorizedAccessException>(() => restrictedAccess.DemandReadable(outside));
            Assert.Throws<InvalidDataException>(() => restrictedAccess.DemandProjectPaths([outside], [destination]));
            Assert.Throws<UnauthorizedAccessException>(() => restrictedAccess.DemandOutput(Path.Combine(input.FullName, "new.ts")));
            Assert.Throws<IOException>(() => restrictedAccess.OpenOutput(destination));
            Assert.Throws<IOException>(() => restrictedAccess.PublishExport(temporary, destination));
            Assert.Equal("existing", File.ReadAllText(destination));
            Assert.Equal("new", File.ReadAllText(temporary));

            MediaFileAccess.Current.PublishExport(temporary, destination);
            Assert.Equal("new", File.ReadAllText(destination));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SavedFramesAreReusedWithinEachWorkspaceAndIsolatedBetweenWorkspaces()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var input = root.CreateSubdirectory("media").FullName;
            var alice = new WebUiSession(new MediaWorkspace(input, root.CreateSubdirectory("alice").FullName), 0, null, false);
            var bob = new WebUiSession(new MediaWorkspace(input, root.CreateSubdirectory("bob").FullName), 0, null, false);
            var frame = new SavedFrameTemplate { Name = "Alice template", Jpeg = [1, 2, 3] };
            await alice.SavedFrames.SaveAsync(new SavedFrameLibrary { Templates = [frame] });
            Assert.Equal(frame.Id, Assert.Single((await alice.SavedFrames.LoadAsync()).Templates).Id);
            Assert.Empty((await bob.SavedFrames.LoadAsync()).Templates);
            await bob.SavedFrames.SaveAsync(new SavedFrameLibrary());
            Assert.Single((await alice.SavedFrames.LoadAsync()).Templates);
            Assert.Empty(Directory.GetFiles(root.FullName, "*", SearchOption.AllDirectories));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void AudioPacketsAdvanceTheClockAndFlushBrowserAudioAfterSeeking()
    {
        using var audio = new WebUiAudioOutput();
        var packet = new byte[2 + WebUiAudioOutput.PacketFrames * 4];

        Assert.True(audio.Start());
        audio.SetVolume(0.5);
        Assert.Equal(1, audio.Write([1, -1]));
        Assert.Equal(6, audio.ReadPacket(packet));
        Assert.Equal((byte)'A', packet[0]);
        Assert.Equal(1, packet[1]);
        Assert.Equal(16384, BinaryPrimitives.ReadInt16LittleEndian(packet.AsSpan(2)));
        Assert.Equal(-16384, BinaryPrimitives.ReadInt16LittleEndian(packet.AsSpan(4)));
        Assert.Equal(1UL, audio.ConsumedFrames);

        Assert.Equal(1, audio.Write([0.5f, 0.5f]));
        Assert.True(audio.Stop());
        Assert.True(audio.Flush());
        Assert.Equal(2, audio.ReadPacket(packet));
        Assert.Equal(1, packet[1]);
        Assert.True(audio.Start());
        Assert.Equal(0, audio.ReadPacket(packet));
        Assert.Equal(1UL, audio.ConsumedFrames);

        Array.Fill(packet, (byte)0x5a);
        Assert.Equal(1, audio.Write([0.25f, -0.25f]));
        Assert.Equal(0, audio.ReadPacket(packet, includeAudio: false));
        Assert.Equal(2UL, audio.ConsumedFrames);
        Assert.All(packet.AsSpan(2).ToArray(), value => Assert.Equal((byte)0x5a, value));
        Assert.Equal(1, audio.Write([0.5f, -0.5f]));
        Assert.Equal(6, audio.ReadPacket(packet));
        Assert.Equal(1, packet[1]);
        Assert.Equal(3UL, audio.ConsumedFrames);
    }

    [Fact]
    public unsafe void H264BitrateChangesKeepTheStreamAndScreenContent()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(root.FullName, "screen.mp4");
            using var socket = new WebUiVideoSocket();
            socket.Start(1, "h264");
            using var encoder = new WebUiMediaEncoder();
            using var surface = SKSurface.Create(new SKImageInfo(1920, 1080, SKColorType.Bgra8888, SKAlphaType.Opaque));
            using var motion = new SKBitmap(1280, 720, SKColorType.Bgra8888, SKAlphaType.Opaque);
            var random = new Random(42);
            var initializations = 0;
            var frames = 0;
            using (var output = File.Create(path))
            {
                for (var index = 0; index < 90; ++index)
                {
                    // 高复杂度预览与静止白色面板共存，覆盖弱网降码率时整片 UI 被抹灰的问题。
                    random.NextBytes(new Span<byte>((void*)motion.GetPixels(), motion.RowBytes * motion.Height));
                    surface.Canvas.Clear(SKColors.White);
                    surface.Canvas.DrawBitmap(motion, 0, 64);
                    using var image = surface.Snapshot();
                    encoder.Video(image, index < 30 ? 7_000_000 : index < 60 ? 500_000 : 5_000_000, socket.Stream!, socket);
                    while (socket.Frames.Reader.TryRead(out var frame))
                    {
                        ++frames;
                        if (frame.Span[0] == (byte)'I')
                            ++initializations;
                        output.Write(frame.Span[5..]);
                    }
                }
            }
            Assert.Equal(90, frames);
            Assert.Equal(1, initializations);

            using var input = FormatContext.OpenInputUrl(path);
            input.LoadStreamInfo();
            AVStream* stream = input.GetVideoStream();
            using var decoder = new CodecContext(Codec.FindDecoderById(stream->codecpar->codec_id));
            decoder.FillParameters(new CodecParameters(stream->codecpar));
            FFmpegException.Check(FF.avcodec_open2(decoder, FF.avcodec_find_decoder(stream->codecpar->codec_id), null));
            using var decoded = new Frame();
            var decodedFrames = 0;
            foreach (var packet in input.ReadPackets())
            {
                FFmpegException.Check(FF.avcodec_send_packet(decoder, packet));
                while (FF.avcodec_receive_frame(decoder, decoded) >= 0)
                {
                    ++decodedFrames;
                    // 白色侧栏及底栏必须仍是白色；只验证解码成功无法发现合法码流中的灰块。
                    foreach (var (x, y) in new[] { (1800, 400), (1800, 700), (640, 1000) })
                        Assert.True(decoded.Data[0][y * decoded.Linesize[0] + x] > 200,
                            $"Frame {decodedFrames}: white screen region at ({x}, {y}) was lost.");
                }
            }
            Assert.Equal(90, decodedFrames);
        }
        finally { root.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HeadlessHostStreamsTheRealEditorWindow(bool allowLan, bool enableIpv6)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        var root = Directory.CreateTempSubdirectory();
        var key = WebUiPairing.CreateAccessKey();
        var publishedExecutable = Environment.GetEnvironmentVariable("TSCUTTER_WEB_TEST_EXECUTABLE");
        var usingPublishedExecutable = !string.IsNullOrEmpty(publishedExecutable);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(usingPublishedExecutable ? publishedExecutable! :
                Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        var start = process.StartInfo;
        if (!usingPublishedExecutable)
            start.ArgumentList.Add(typeof(App).Assembly.Location);
        start.ArgumentList.Add("--web-ui");
        start.ArgumentList.Add("0");
        var mediaDirectory = root.CreateSubdirectory("media").FullName;
        var outputDirectory = root.CreateSubdirectory("output").FullName;
        start.ArgumentList.Add(mediaDirectory);
        start.ArgumentList.Add(outputDirectory);
        if (allowLan)
            start.ArgumentList.Add("--lan");
        if (enableIpv6)
            start.ArgumentList.Add("--ipv6");
        start.Environment["TSCUTTER_WEB_ACCESS_KEY"] = key;
        start.Environment["TSCUTTER_WEB_MANAGED"] = "1";
        start.Environment[WebUiPreferences.EnvironmentVariable] = WebUiPreferences.CreateSnapshot(new AppConfig { Language = "en-US" });
        var started = false;

        try
        {
            // 发布包自带 FFmpeg，可用合成的纯色画面和正弦波验证真实剪辑流程。
            if (usingPublishedExecutable)
                File.Copy(Path.Combine(AppContext.BaseDirectory, "Application", "Fixtures", "WebUiClip.mp4"),
                    Path.Combine(mediaDirectory, "clip.mp4"));

            started = process.Start();
            Assert.True(started);
            var errors = process.StandardError.ReadToEndAsync(token);
            const string prefix = "Web UI: ";
            string? line;
            do
            {
                line = await process.StandardOutput.ReadLineAsync(token);
            }
            while (line is not null && !line.StartsWith(prefix, StringComparison.Ordinal));
            Assert.True(line is not null, line is null ? await errors : null);
            var link = new Uri(line![prefix.Length..]);
            if (enableIpv6 && System.Net.Sockets.Socket.OSSupportsIPv6)
                link = new UriBuilder(link) { Host = "::1" }.Uri;
            var origin = link.GetLeftPart(UriPartial.Authority);
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            using var script = await http.GetAsync(origin + "/remote.js", token);
            script.EnsureSuccessStatusCode();
            using var reload = new HttpRequestMessage(HttpMethod.Get, origin + "/remote.js");
            reload.Headers.IfNoneMatch.Add(Assert.IsType<System.Net.Http.Headers.EntityTagHeaderValue>(script.Headers.ETag));
            using var cached = await http.SendAsync(reload, token);
            Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode);
            Assert.Empty(await cached.Content.ReadAsByteArrayAsync(token));
            var page = await http.GetStringAsync(origin, token);
            Assert.Contains("/remote.js", page);
            Assert.Equal("en-US", await http.GetStringAsync($"{origin}/startup-locale", token));
            var endpoint = new UriBuilder(link) { Scheme = "ws", Path = "/connect", Fragment = "" }.Uri;
            using var denied = new ClientWebSocket();
            denied.Options.SetRequestHeader("Origin", origin);
            await denied.ConnectAsync(endpoint, token);
            await denied.SendAsync("invalid"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, token);
            Assert.Equal(WebSocketMessageType.Close, (await ReceiveAsync(denied, token)).Type);
            Assert.Equal(WebSocketCloseStatus.PolicyViolation, denied.CloseStatus);

            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Origin", origin);
            await socket.ConnectAsync(endpoint, token);
            await socket.SendAsync(Encoding.ASCII.GetBytes(key).AsMemory(), WebSocketMessageType.Text, true, token);
            Assert.Equal("ready", Encoding.ASCII.GetString((await ReceiveAsync(socket, token)).Bytes));
            var title = await ReceiveAsync(socket, token);
            Assert.Equal(WebSocketMessageType.Text, title.Type);
            Assert.Equal(WebUiSession.TitlePrefix + ReleaseInfo.WindowTitle, Encoding.UTF8.GetString(title.Bytes));
            var viewport = await ReceiveAsync(socket, token);
            Assert.Equal(WebSocketMessageType.Text, viewport.Type);
            Assert.Equal(WebUiSession.ViewportPrefix + "1280,800", Encoding.UTF8.GetString(viewport.Bytes));
            var frame = await ReceiveAsync(socket, token);
            Assert.Equal(WebSocketMessageType.Binary, frame.Type);
            using var bitmap = SKBitmap.Decode(frame.Bytes);
            Assert.NotNull(bitmap);
            Assert.Equal((1280, 800), (bitmap.Width, bitmap.Height));
            var middle = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
            Assert.Contains(Enumerable.Range(0, 16), index => bitmap.GetPixel(index * bitmap.Width / 16, bitmap.Height / 2) != middle);

            if (usingPublishedExecutable)
                await VerifyPublishedEditingAsync(socket, outputDirectory, token);

            // 有人使用时返回单独的占用状态；断开后原工作区可重新连接。
            using var contender = new ClientWebSocket();
            contender.Options.SetRequestHeader("Origin", origin);
            await contender.ConnectAsync(endpoint, token);
            await contender.SendAsync(Encoding.ASCII.GetBytes(key).AsMemory(), WebSocketMessageType.Text, true, token);
            Assert.Equal(WebSocketMessageType.Close, (await ReceiveAsync(contender, token)).Type);
            Assert.Equal(WebUiSession.EditorAlreadyConnected, contender.CloseStatus);

            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, token);
            using var reconnected = new ClientWebSocket();
            reconnected.Options.SetRequestHeader("Origin", origin);
            await reconnected.ConnectAsync(endpoint, token);
            await reconnected.SendAsync(Encoding.ASCII.GetBytes(key).AsMemory(), WebSocketMessageType.Text, true, token);
            var response = await ReceiveAsync(reconnected, token);
            Assert.Equal(WebSocketMessageType.Text, response.Type);
            Assert.Equal("ready", Encoding.ASCII.GetString(response.Bytes));
            var currentTitle = await ReceiveAsync(reconnected, token);
            Assert.Equal(WebSocketMessageType.Text, currentTitle.Type);
            Assert.Equal(WebUiSession.TitlePrefix + ReleaseInfo.WindowTitle + (usingPublishedExecutable ? " - clip.mp4" : string.Empty),
                Encoding.UTF8.GetString(currentTitle.Bytes));
            using var restored = await ReceivePictureAsync();
            Assert.Equal((1280, 800), (restored.Width, restored.Height));
            if (!usingPublishedExecutable)
            {
                var center = restored.GetPixel(640, 400);
                Assert.Contains(Enumerable.Range(0, 16), index => restored.GetPixel(index * 80, 400) != center);
            }

            async Task<SKBitmap> ReceivePictureAsync()
            {
                while (true)
                {
                    var message = await ReceiveAsync(reconnected, token);
                    if (message.Type == WebSocketMessageType.Binary && message.Bytes is [0xff, 0xd8, ..])
                        return SKBitmap.Decode(message.Bytes)!;
                }
            }
        }
        finally
        {
            if (started && !process.HasExited)
            {
                process.StandardInput.Close();
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(shutdown.Token);
                }
                catch (OperationCanceledException) when (!process.HasExited)
                {
                    process.Kill();
                    await process.WaitForExitAsync();
                }
            }
            root.Delete(recursive: true);
        }
    }

    internal static async Task VerifyPublishedEditingAsync(ClientWebSocket socket, string outputDirectory, CancellationToken token)
    {
        async Task SendAsync(object input) => await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(input).AsMemory(),
            WebSocketMessageType.Text, true, token);

        async Task KeyAsync(string key, string code, bool shortcut = false)
        {
            foreach (var type in new[] { "keyDown", "keyUp" })
                await SendAsync(new
                {
                    type,
                    key,
                    code,
                    control = shortcut && !OperatingSystem.IsMacOS(),
                    meta = shortcut && OperatingSystem.IsMacOS()
                });
        }

        async Task ClickAsync(int x, int y)
        {
            await SendAsync(new
            {
                type = "down",
                x,
                y,
                button = 0,
                buttons = 1
            });
            await SendAsync(new
            {
                type = "up",
                x,
                y,
                button = 0,
                buttons = 0
            });
        }

        string? lastTitle = null;
        async Task WaitForPacketAsync(Func<byte[], bool> matches, string description)
        {
            try
            {
                while (true)
                {
                    var frame = await ReceiveAsync(socket, token);
                    if (frame.Type == WebSocketMessageType.Text)
                    {
                        var titleMessage = Encoding.UTF8.GetString(frame.Bytes);
                        if (titleMessage.StartsWith(WebUiSession.ViewportPrefix, StringComparison.Ordinal))
                            continue;
                        Assert.StartsWith(WebUiSession.TitlePrefix, titleMessage);
                        lastTitle = titleMessage[WebUiSession.TitlePrefix.Length..];
                        continue;
                    }

                    Assert.Equal(WebSocketMessageType.Binary, frame.Type);
                    if (matches(frame.Bytes))
                        return;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw new TimeoutException(description);
            }
        }

        Task WaitForPictureAsync(Func<SKBitmap, bool> matches, string description) => WaitForPacketAsync(bytes =>
        {
            if (bytes is not [0xff, ..])
                return false;
            using var picture = SKBitmap.Decode(bytes);
            return picture is not null && matches(picture);
        }, description);

        static bool IsPicker(SKBitmap picture)
        {
            var center = picture.GetPixel(600, 410);
            return center.Red > 240 && center.Green > 240 && center.Blue > 240;
        }

        await SendAsync(new
        {
            type = "locale",
            locale = "en-US"
        });
        await SendAsync(new
        {
            type = "quality",
            quality = "smooth"
        });
        await WaitForPictureAsync(picture => picture.Width == 960 && picture.Height == 600,
            "The smooth profile did not scale down the streamed frame.");
        await SendAsync(new
        {
            type = "quality",
            quality = "balanced"
        });
        await WaitForPictureAsync(picture => picture.Width == 1280 && picture.Height == 800,
            "Selecting balanced quality did not restore the streamed frame size.");
        await KeyAsync("o", "KeyO", shortcut: true);
        await WaitForPictureAsync(IsPicker, "The shared media picker did not appear.");
        // 拖动标题栏后，原位置露出编辑器，在新位置仍能选中并打开文件。
        await SendAsync(new
        {
            type = "down",
            x = 400,
            y = 184,
            button = 0,
            buttons = 1
        });
        await SendAsync(new
        {
            type = "move",
            x = 580,
            y = 279,
            button = 0,
            buttons = 1
        });
        await SendAsync(new
        {
            type = "up",
            x = 580,
            y = 279,
            button = 0,
            buttons = 0
        });
        await WaitForPictureAsync(picture =>
            IsPicker(picture) && picture.GetPixel(380, 320).Red < 64,
            "The shared media picker did not move with its title bar.");
        await ClickAsync(560, 341);
        await KeyAsync("Enter", "Enter");
        await WaitForPictureAsync(picture =>
        {
            var preview = picture.GetPixel(500, 380);
            return preview.Red > 180 && preview.Blue < 60;
        }, "The published executable did not decode the first source frame.");
        Assert.Equal(ReleaseInfo.WindowTitle + " - clip.mp4", lastTitle);

        await KeyAsync("i", "KeyI");
        await KeyAsync("ArrowRight", "ArrowRight");
        await WaitForPictureAsync(picture =>
        {
            var preview = picture.GetPixel(500, 380);
            return preview.Blue > 180 && preview.Red < 60;
        }, "The editor did not seek to the next source keyframe.");
        await KeyAsync("o", "KeyO");
        await KeyAsync(" ", "Space");
        await WaitForPacketAsync(bytes => bytes is [(byte)'A', _, _, _, ..] && bytes.AsSpan(2).ContainsAnyExcept((byte)0),
            "The published executable did not stream decoded audio samples.");

        await KeyAsync(" ", "Space");
        await KeyAsync("s", "KeyS", shortcut: true);
        await WaitForPictureAsync(IsPicker, "The export destination picker did not appear.");
        await ClickAsync(820, 604);

        string[] files;
        while ((files = Directory.GetFiles(outputDirectory, "*.mp4")).Length == 0)
            await Task.Delay(30, token);
        var clip = await File.ReadAllBytesAsync(Assert.Single(files), token);
        var header = clip.AsSpan().IndexOf("mvhd"u8);
        Assert.InRange(header, 4, clip.Length - 36);
        var body = clip.AsSpan(header + 4);
        var timescale = BinaryPrimitives.ReadUInt32BigEndian(body[(body[0] == 1 ? 20 : 12)..]);
        var duration = body[0] == 1 ? BinaryPrimitives.ReadUInt64BigEndian(body[24..]) :
            BinaryPrimitives.ReadUInt32BigEndian(body[16..]);
        Assert.InRange((double)duration / timescale, 1.9, 2.3);
    }

    private static async Task<(WebSocketMessageType Type, byte[] Bytes)> ReceiveAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[16384];
        using var message = new MemoryStream();
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), token);
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return (result.MessageType, message.ToArray());
    }
}
