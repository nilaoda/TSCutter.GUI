using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SkiaSharp;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.Utils.Files;
using TSCutter.GUI.Web;
using Xunit;

namespace TSCutter.GUI.Tests.Application;

public sealed class WebUiServerTests
{
    [Fact]
    public void AccountSessionsAreBoundToTheirOriginAndPasswordChangesOnlyRevokeThatUser()
    {
        var password = WebUiPassword.Hash("test-password");
        var alice = new WebUiAccount { UserName = "alice", PasswordHash = password };
        var bob = new WebUiAccount { UserName = "bob", PasswordHash = password };
        var store = new WebUiLoginStore();
        store.UpdateAccounts([alice, bob]);
        Assert.False(store.TryLogin("alice", "wrong-password", "http://host:5360", out _));
        Assert.True(store.TryLogin("ALICE", "test-password", "http://host:5360", out var aliceCookie));
        Assert.True(store.TryLogin("bob", "test-password", "http://host:5360", out var bobCookie));
        Assert.Equal(alice.Id, store.Authenticate(aliceCookie, "http://host:5360")!.UserId);
        Assert.Null(store.Authenticate(aliceCookie, "http://other:5360"));
        store.SetPreferences(alice.Id, "latest-preferences");
        store.UpdateAccounts([alice with { PasswordHash = WebUiPassword.Hash("another-password") }, bob]);
        Assert.Equal("latest-preferences", store.FindAccount(alice.Id)!.Preferences);
        Assert.Null(store.Authenticate(aliceCookie, "http://host:5360"));
        var bobLogin = Assert.IsType<WebUiLogin>(store.Authenticate(bobCookie, "http://host:5360"));
        store.Logout(bobLogin.SessionId);
        Assert.Null(store.Authenticate(bobCookie, "http://host:5360"));
        Assert.Throws<ArgumentException>(() => store.UpdateAccounts([alice, bob with { UserName = "ALICE" }]));
        Assert.Throws<ArgumentException>(() => store.UpdateAccounts([alice with { Id = "ABCDEF0123456789ABCDEF0123456789" }, bob]));
        Assert.Throws<ArgumentException>(() => store.UpdateAccounts([alice with { InputDirectory = "relative-media" }, bob]));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AccountsUseSharedDefaultsOrCustomOutputsAndKeepSeparateProcessesAndWorkspaces(bool allowLan, bool enableIpv6)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var token = timeout.Token;
        var root = Directory.CreateTempSubdirectory("tscutter-users-test-");
        try
        {
            var workspace = new MediaWorkspace(root.CreateSubdirectory("input").FullName, root.CreateSubdirectory("output").FullName);
            var executable = Environment.GetEnvironmentVariable("TSCUTTER_WEB_TEST_EXECUTABLE");
            if (!string.IsNullOrEmpty(executable))
                File.Copy(Path.Combine(AppContext.BaseDirectory, "Application", "Fixtures", "WebUiClip.mp4"), Path.Combine(workspace.InputDirectory, "clip.mp4"));
            var hash = WebUiPassword.Hash("test-password");
            var alice = new WebUiAccount { UserName = "alice", PasswordHash = hash };
            var bob = new WebUiAccount { UserName = "bob", PasswordHash = hash,
                OutputDirectory = root.CreateSubdirectory("bob-output").FullName };
            var carol = new WebUiAccount { UserName = "carol", PasswordHash = hash };
            var accounts = new[] { alice, bob, carol };
            var store = new WebUiLoginStore();
            store.UpdateAccounts(accounts);
            var preferences = WebUiPreferences.CreateSnapshot(new AppConfig { Language = "en-US" });
            var options = new WebUiServerOptions(workspace, 0, allowLan, 2, preferences, "Standard", "en-US", false,
                executable ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", typeof(App).Assembly.Location, EnableIpv6: enableIpv6);
            await using var server = new WebUiServer(options, store);
            await server.StartAsync(token);
            // 双栈入口仍须支持 IPv4，并用 IPv6 验证账号登录、首帧及工作区重连。
            using var ipv4 = new HttpClient(new HttpClientHandler { UseProxy = false });
            Assert.Equal("login", await ipv4.GetStringAsync($"http://127.0.0.1:{server.Port}/auth", token));
            var address = allowLan && enableIpv6 && System.Net.Sockets.Socket.OSSupportsIPv6
                ? WebUiHttp.GetAddresses(true, true).FirstOrDefault(item => item.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                    ?? IPAddress.IPv6Loopback : IPAddress.Loopback;
            if (!enableIpv6 && System.Net.Sockets.Socket.OSSupportsIPv6)
            {
                using var ipv6 = new System.Net.Sockets.TcpClient(System.Net.Sockets.AddressFamily.InterNetworkV6);
                await Assert.ThrowsAnyAsync<System.Net.Sockets.SocketException>(async () =>
                    await ipv6.ConnectAsync(IPAddress.IPv6Loopback, server.Port, token));
            }
            var host = address.ToString();
            var origin = new UriBuilder("http", host, server.Port).Uri.GetLeftPart(UriPartial.Authority);
            using var first = new Browser(origin);
            using var second = new Browser(origin);
            using var third = new Browser(origin);
            Assert.Equal("login", await first.Http.GetStringAsync("/auth", token));
            using (var rejected = await first.Http.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = "alice",
                ["password"] = "bad-password"
            }), token))
                Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            await first.LoginAsync("alice", token);
            await second.LoginAsync("bob", token);
            await third.LoginAsync("carol", token);
            using var aliceSocket = await first.ConnectAsync(token);
            using var bobSocket = await second.ConnectAsync(token);
            Assert.Equal("ready", Encoding.UTF8.GetString((await ReceiveAsync(aliceSocket, token)).Bytes));
            Assert.Equal("ready", Encoding.UTF8.GetString((await ReceiveAsync(bobSocket, token)).Bytes));
            await WaitForPictureAsync(aliceSocket, token);
            await WaitForPictureAsync(bobSocket, token);
            var initialStatus = server.GetUserStatus(accounts);
            var aliceProcess = initialStatus.Single(user => user.Id == alice.Id).ProcessId;
            Assert.NotEqual(aliceProcess, initialStatus.Single(user => user.Id == bob.Id).ProcessId);

            using (var duplicate = await first.ConnectAsync(token))
            {
                Assert.Equal(WebSocketMessageType.Close, (await ReceiveAsync(duplicate, token)).Type);
                Assert.Equal(WebUiSession.EditorAlreadyConnected, duplicate.CloseStatus);
            }
            using (var limited = await third.ConnectAsync(token))
            {
                Assert.Equal(WebSocketMessageType.Close, (await ReceiveAsync(limited, token)).Type);
                Assert.Equal(WebUiServer.WorkerLimit, limited.CloseStatus);
            }

            var aliceOutput = workspace.OutputDirectory;
            var bobOutput = bob.OutputDirectory!;
            Assert.Equal(aliceOutput, initialStatus.Single(user => user.Id == alice.Id).OutputDirectory);
            Assert.Equal(aliceOutput, initialStatus.Single(user => user.Id == carol.Id).OutputDirectory);
            Assert.Equal(bobOutput, initialStatus.Single(user => user.Id == bob.Id).OutputDirectory);
            Assert.Empty(Directory.GetDirectories(workspace.OutputDirectory));
            Assert.False(new MediaWorkspace(workspace.InputDirectory, aliceOutput).AllowsOutput(Path.Combine(bobOutput, "other.ts")));

            if (!string.IsNullOrEmpty(executable))
            {
                await WebUiTests.VerifyPublishedEditingAsync(aliceSocket, aliceOutput, token);
                await WebUiTests.VerifyPublishedEditingAsync(bobSocket, bobOutput, token);
                Assert.Single(Directory.GetFiles(aliceOutput, "*.mp4"));
                Assert.Single(Directory.GetFiles(bobOutput, "*.mp4"));
                Assert.Empty(Directory.GetDirectories(workspace.OutputDirectory));
            }

            // 重连复用原进程；退出登录和重置密码不清除工作区。
            var title = ReleaseInfo.WindowTitle + (!string.IsNullOrEmpty(executable) ? " - clip.mp4" : string.Empty);
            using (var logout = await first.Http.PostAsync("/logout", new StringContent(string.Empty), token))
                logout.EnsureSuccessStatusCode();
            Assert.Equal("login", await first.Http.GetStringAsync("/auth", token));
            while (server.GetUserStatus(accounts).Single(user => user.Id == alice.Id).Connected)
                await Task.Delay(30, token);
            await first.LoginAsync("alice", token);
            using (var again = await first.ConnectAsync(token))
            {
                Assert.Equal("ready", Encoding.UTF8.GetString((await ReceiveAsync(again, token)).Bytes));
                Assert.Equal(WebUiSession.TitlePrefix + title, Encoding.UTF8.GetString((await ReceiveAsync(again, token)).Bytes));
                Assert.Equal(aliceProcess, server.GetUserStatus(accounts).Single(user => user.Id == alice.Id).ProcessId);
            }
            await server.UpdateAccountsAsync([alice with { PasswordHash = WebUiPassword.Hash("changed-password") }, bob, carol]);
            Assert.Equal("login", await first.Http.GetStringAsync("/auth", token));
            Assert.Equal("remembered", await second.Http.GetStringAsync("/auth", token));
            Assert.True(server.GetUserStatus(accounts).Single(user => user.Id == bob.Id).Running);
            await server.UpdateAccountsAsync([alice with { Enabled = false }, bob, carol]);
            Assert.False(server.GetUserStatus(accounts).Single(user => user.Id == alice.Id).Running);
            Assert.True(server.GetUserStatus(accounts).Single(user => user.Id == bob.Id).Running);
            Assert.True(Directory.Exists(aliceOutput));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task ChangingWorkspaceFoldersOnlyRestartsThatAccountAndKeepsItsLogin()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        var root = Directory.CreateTempSubdirectory("tscutter-source-scope-");
        try
        {
            var defaults = new MediaWorkspace(root.CreateSubdirectory("default").FullName, root.CreateSubdirectory("output").FullName);
            var alternative = root.CreateSubdirectory("alternative").FullName;
            var privateMedia = root.CreateSubdirectory("bob-media").FullName;
            var customOutput = root.CreateSubdirectory("alice-output").FullName;
            var hash = WebUiPassword.Hash("test-password");
            var alice = new WebUiAccount { UserName = "alice", PasswordHash = hash };
            var bob = new WebUiAccount { UserName = "bob", PasswordHash = hash, InputDirectory = privateMedia };
            var accounts = new[] { alice, bob };
            var store = new WebUiLoginStore();
            store.UpdateAccounts(accounts);
            var preferences = WebUiPreferences.CreateSnapshot(new AppConfig { Language = "en-US" });
            await using var server = new WebUiServer(new WebUiServerOptions(defaults, 0, false, 2, preferences, "Standard", "en-US", false,
                Environment.GetEnvironmentVariable("TSCUTTER_WEB_TEST_EXECUTABLE") ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
                typeof(App).Assembly.Location), store);
            await server.StartAsync(token);
            var origin = $"http://127.0.0.1:{server.Port}";
            using var first = new Browser(origin);
            using var second = new Browser(origin);
            await first.LoginAsync("alice", token);
            await second.LoginAsync("bob", token);
            using var aliceSocket = await first.ConnectAsync(token);
            using var bobSocket = await second.ConnectAsync(token, explicitMedia: true);
            Assert.Equal("ready", Encoding.UTF8.GetString((await ReceiveAsync(aliceSocket, token)).Bytes));
            Assert.Equal("ready", Encoding.UTF8.GetString((await ReceiveAsync(bobSocket, token)).Bytes));
            await WaitForPictureAsync(aliceSocket, token);
            // 等到宿主已处理过一次采集，再选用流畅 JPEG。第一张图片就应采用所选画质。
            var bobTitle = await ReceiveAsync(bobSocket, token);
            Assert.StartsWith(WebUiSession.TitlePrefix, Encoding.UTF8.GetString(bobTitle.Bytes));
            await bobSocket.SendAsync("""{"type":"quality","quality":"smooth"}"""u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, token);
            await bobSocket.SendAsync("""{"type":"video-start","streamId":1,"codec":"jpeg"}"""u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, token);
            var viewport = await ReceiveAsync(bobSocket, token);
            Assert.StartsWith(WebUiSession.ViewportPrefix, Encoding.UTF8.GetString(viewport.Bytes));
            var firstFrame = await ReceiveAsync(bobSocket, token);
            Assert.Equal(WebSocketMessageType.Binary, firstFrame.Type);
            using var picture = SKBitmap.Decode(firstFrame.Bytes);
            Assert.Equal((960, 600), (picture.Width, picture.Height));
            var before = server.GetUserStatus(accounts);
            Assert.All(before, item => Assert.Equal(defaults.OutputDirectory, item.OutputDirectory));
            Assert.Equal(privateMedia, before.Single(item => item.Id == bob.Id).InputDirectory);
            var alicePid = before.Single(item => item.Id == alice.Id).ProcessId;
            var bobPid = before.Single(item => item.Id == bob.Id).ProcessId;

            // 由工作进程实际检查对话框和任务，拒绝修改后仍可正常继续操作。
            await KeyAsync("o", "KeyO", true);
            await WaitForPictureAsync(aliceSocket, token, picture => picture.GetPixel(600, 410).Red > 240);
            Assert.Equal(WebUiDirectoryChange.Busy, await server.UpdateDirectoriesAsync(alice.Id, alternative, null, token));
            Assert.Equal(alicePid, server.GetUserStatus(accounts).Single(item => item.Id == alice.Id).ProcessId);
            foreach (var type in new[] { "down", "up" })
                await aliceSocket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    type,
                    x = 905,
                    y = 604,
                    button = 0,
                    buttons = type == "down" ? 1 : 0
                }).AsMemory(), WebSocketMessageType.Text, true, token);
            await Task.Delay(150, token);
            var changing = server.UpdateDirectoriesAsync(alice.Id, alternative, customOutput, token);
            var closing = ReadCloseAsync();
            if (await Task.WhenAny(changing, closing) == changing)
                Assert.Equal(WebUiDirectoryChange.Restarted, await changing);
            await closing;
            Assert.Equal(WebUiServer.WorkspaceUpdated, aliceSocket.CloseStatus);
            await aliceSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, token);
            Assert.Equal(WebUiDirectoryChange.Restarted, await changing);
            Assert.Equal("remembered", await first.Http.GetStringAsync("/auth", token));
            using (var reconnected = await first.ConnectAsync(token))
            {
                Assert.Equal("ready", Encoding.UTF8.GetString((await ReceiveAsync(reconnected, token)).Bytes));
                var after = server.GetUserStatus(accounts);
                Assert.Equal(alternative, after.Single(item => item.Id == alice.Id).InputDirectory);
                Assert.Equal(customOutput, after.Single(item => item.Id == alice.Id).OutputDirectory);
                Assert.Equal(defaults.OutputDirectory, after.Single(item => item.Id == bob.Id).OutputDirectory);
                Assert.NotEqual(alicePid, after.Single(item => item.Id == alice.Id).ProcessId);
                Assert.Equal(bobPid, after.Single(item => item.Id == bob.Id).ProcessId);
                Assert.True(after.Single(item => item.Id == bob.Id).Connected);
            }
            await server.StopWorkerAsync(alice.Id);
            Assert.Equal(WebUiDirectoryChange.Applied, await server.UpdateDirectoriesAsync(alice.Id, defaults.OutputDirectory, null, token));
            Assert.Equal(WebUiDirectoryChange.Applied, await server.UpdateDirectoriesAsync(alice.Id, alternative, privateMedia, token));
            Assert.Equal(privateMedia, store.FindAccount(alice.Id)!.OutputDirectory);
            Assert.Equal(WebUiDirectoryChange.Applied, await server.UpdateDirectoriesAsync(alice.Id, null, null, token));
            Assert.Null(store.FindAccount(alice.Id)!.InputDirectory);
            Assert.Null(store.FindAccount(alice.Id)!.OutputDirectory);
            Assert.Equal(defaults.InputDirectory, server.GetUserStatus(accounts).Single(item => item.Id == alice.Id).InputDirectory);
            Assert.Equal("remembered", await first.Http.GetStringAsync("/auth", token));

            async Task ReadCloseAsync()
            {
                while ((await ReceiveAsync(aliceSocket, token)).Type != WebSocketMessageType.Close)
                {
                }
            }

            async Task KeyAsync(string key, string code, bool shortcut = false)
            {
                var modifier = OperatingSystem.IsMacOS() ? "Meta" : "Control";
                var modifierCode = OperatingSystem.IsMacOS() ? "MetaLeft" : "ControlLeft";
                if (shortcut)
                    await Send("keyDown", modifier, modifierCode, true);
                foreach (var type in new[] { "keyDown", "keyUp" })
                    await Send(type, key, code, shortcut);
                if (shortcut)
                    await Send("keyUp", modifier, modifierCode, false);

                async Task Send(string type, string value, string physical, bool held) =>
                    await aliceSocket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        type,
                        key = value,
                        code = physical,
                        control = held && !OperatingSystem.IsMacOS(),
                        meta = held && OperatingSystem.IsMacOS()
                    }).AsMemory(), WebSocketMessageType.Text, true, token);
            }
        }
        finally { root.Delete(recursive: true); }
    }

    private sealed class Browser : IDisposable
    {
        private readonly CookieContainer cookies = new();
        private readonly string origin;
        internal HttpClient Http
        {
            get;
        }
        internal Browser(string origin)
        {
            this.origin = origin;
            Http = new HttpClient(new HttpClientHandler { UseProxy = false, CookieContainer = cookies }) { BaseAddress = new Uri(origin) };
            Http.DefaultRequestHeaders.Add("Origin", origin);
        }
        internal async Task LoginAsync(string name, CancellationToken token)
        {
            using var response = await Http.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = name,
                ["password"] = "test-password"
            }), token);
            response.EnsureSuccessStatusCode();
            Assert.Equal("remembered", await response.Content.ReadAsStringAsync(token));
        }
        internal async Task<ClientWebSocket> ConnectAsync(CancellationToken token, bool explicitMedia = false)
        {
            var socket = new ClientWebSocket();
            try
            {
                socket.Options.Proxy = null;
                socket.Options.Cookies = cookies;
                socket.Options.SetRequestHeader("Origin", origin);
                await socket.ConnectAsync(new UriBuilder(origin) { Scheme = "ws", Path = "/connect", Query = explicitMedia ? "media=explicit" : string.Empty }.Uri, token);
                await socket.SendAsync("remembered"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, token);
                return socket;
            }
            catch { socket.Dispose(); throw; }
        }
        public void Dispose() => Http.Dispose();
    }

    private static async Task WaitForPictureAsync(ClientWebSocket socket, CancellationToken token, Func<SKBitmap, bool>? matches = null)
    {
        while (true)
        {
            var packet = await ReceiveAsync(socket, token);
            Assert.NotEqual(WebSocketMessageType.Close, packet.Type);
            if (packet.Bytes is not [0xff, ..])
                continue;
            using var picture = SKBitmap.Decode(packet.Bytes);
            if (picture is not null && (matches is null || matches(picture)))
                return;
        }
    }

    private static async Task<(WebSocketMessageType Type, byte[] Bytes)> ReceiveAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[16384];
        using var bytes = new MemoryStream();
        ValueWebSocketReceiveResult packet;
        do
        {
            packet = await socket.ReceiveAsync(buffer.AsMemory(), token);
            bytes.Write(buffer, 0, packet.Count);
        } while (!packet.EndOfMessage);
        return (packet.MessageType, bytes.ToArray());
    }
}
