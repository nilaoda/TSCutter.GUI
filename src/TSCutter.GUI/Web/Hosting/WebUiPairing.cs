using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.AspNetCore.Http;

namespace TSCutter.GUI.Web;

internal sealed class WebUiPairing(bool managed)
{
    internal const string StateVariable = "TSCUTTER_WEB_PAIRING_STATE";
    internal const string StateMessage = "Web UI pairing state: ";
    internal const string CodeMessage = "Web UI pairing code: ";
    internal const string PairCommand = "pair";
    internal const string RevokeCommand = "revoke:";
    private const int MaxAttempts = 8;
    private const int MaxBrowsers = 32;
    private readonly object gate = new();
    private WebUiPairingState state = ReadState(managed) ?? new(CreateCode(), []);
    private int attempts;
    private string? activeDevice;
    private CancellationTokenSource? activeConnection;

    internal void Publish()
    {
        lock (gate)
            PublishState();
    }

    internal void Rotate()
    {
        CancellationTokenSource? connection;
        lock (gate)
        {
            var code = CreateCode();
            while (code == state.Code)
                code = CreateCode();
            state = new WebUiPairingState(code, []);
            attempts = 0;
            connection = activeConnection;
            PublishState();
        }
        Cancel(connection);
    }

    internal int TryPair(string suppliedCode, string name, string origin, out string? credential)
    {
        lock (gate)
        {
            credential = null;
            // 多次输错只暂停新配对，已配对浏览器仍可使用。刷新码后恢复配对。
            if (attempts >= MaxAttempts)
                return StatusCodes.Status429TooManyRequests;
            if (!FixedEquals(state.Code, suppliedCode))
                return ++attempts >= MaxAttempts ? StatusCodes.Status429TooManyRequests : StatusCodes.Status401Unauthorized;
            if (state.Browsers.Length >= MaxBrowsers)
                return StatusCodes.Status503ServiceUnavailable;

            var id = Guid.NewGuid().ToString("N");
            credential = id + "." + CreateAccessKey();
            var browser = new WebUiBrowserCredential(id, name, origin, Digest(credential));
            state = state with
            {
                Browsers = [.. state.Browsers, browser]
            };
            PublishState();
            return StatusCodes.Status200OK;
        }
    }

    internal WebUiBrowserCredential? VerifyDevice(string? credential, string origin)
    {
        if (credential is null || credential.Length != 97 || credential[32] != '.')
            return null;
        var id = credential[..32];
        lock (gate)
        {
            var browser = state.Browsers.FirstOrDefault(item => item.Id == id);
            return browser is not null && string.Equals(browser.Origin, origin, StringComparison.Ordinal) &&
                FixedEquals(browser.Digest, Digest(credential)) ? browser : null;
        }
    }

    internal bool Attach(string id, CancellationTokenSource connection)
    {
        lock (gate)
        {
            if (!state.Browsers.Any(browser => browser.Id == id))
                return false;
            activeDevice = id;
            activeConnection = connection;
            return true;
        }
    }

    internal void Detach(CancellationTokenSource connection)
    {
        lock (gate)
        {
            if (!ReferenceEquals(activeConnection, connection))
                return;
            activeDevice = null;
            activeConnection = null;
        }
    }

    internal void Revoke(string id)
    {
        CancellationTokenSource? connection = null;
        lock (gate)
        {
            if (!state.Browsers.Any(browser => browser.Id == id))
                return;
            state = state with
            {
                Browsers = state.Browsers.Where(browser => browser.Id != id).ToArray()
            };
            if (activeDevice == id)
                connection = activeConnection;
            PublishState();
        }
        Cancel(connection);
    }

    private void PublishState()
    {
        // 受管进程通过重定向标准输出交还内存快照，宿主退出后不会留下授权。
        if (managed)
            Console.WriteLine(StateMessage + JsonSerializer.Serialize(state, WebUiAuthJsonContext.Default.WebUiPairingState));
        else
            Console.WriteLine(CodeMessage + state.Code);
    }

    private static WebUiPairingState? ReadState(bool managed)
    {
        if (!managed || Environment.GetEnvironmentVariable(StateVariable) is not { Length: > 0 } json)
            return null;
        Environment.SetEnvironmentVariable(StateVariable, null);
        var saved = JsonSerializer.Deserialize(json, WebUiAuthJsonContext.Default.WebUiPairingState);
        return saved is { Code.Length: 6, Browsers.Length: <= MaxBrowsers } && saved.Code.All(char.IsAsciiDigit)
            ? saved : null;
    }

    private static void Cancel(CancellationTokenSource? connection)
    {
        try
        {
            connection?.Cancel();
        }
        catch (ObjectDisposedException) { }
    }

    internal static string CreateAccessKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private static string CreateCode() => RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool FixedEquals(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
}

internal sealed record WebUiBrowserCredential(string Id, string Name, string Origin, string Digest);
internal sealed record WebUiPairingState(string Code, WebUiBrowserCredential[] Browsers);

[JsonSerializable(typeof(WebUiPairingState))]
internal sealed partial class WebUiAuthJsonContext : JsonSerializerContext;
