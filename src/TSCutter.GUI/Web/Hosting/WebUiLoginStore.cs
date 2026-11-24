using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using TSCutter.GUI.Models.Application;

namespace TSCutter.GUI.Web;

internal static class WebUiPassword
{
    private const int Iterations = 600_000;

    internal static bool ValidUserName(string? name) => name is { Length: >= 1 and <= 32 } &&
        name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');

    internal static string Hash(string password)
    {
        if (password.Length is < 8 or > 128)
            throw new ArgumentException("A password must contain between 8 and 128 characters.");
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"{Iterations}.{Convert.ToHexString(salt)}.{Convert.ToHexString(hash)}";
    }

    internal static bool Verify(string password, string stored)
    {
        if (password.Length > 128 || !TryParse(stored, out var iterations, out var salt, out var expected))
            return false;
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    internal static bool ValidHash(string? stored) => TryParse(stored, out _, out _, out _);

    private static bool TryParse(string? stored, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = hash = [];
        if (stored is null)
            return false;
        var parts = stored.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out iterations) || iterations is < 100_000 or > 1_000_000 ||
            parts[1].Length != 32 || parts[2].Length != 64)
            return false;
        try
        {
            salt = Convert.FromHexString(parts[1]);
            hash = Convert.FromHexString(parts[2]);
            return true;
        }
        catch (FormatException) { return false; }
    }
}

internal sealed record WebUiLogin(string UserId, string UserName, string SessionId);

internal sealed class WebUiLoginStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, WebUiAccount> accounts = [];
    private readonly Dictionary<string, Session> sessions = [];
    private readonly string dummyHash = WebUiPassword.Hash(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
    private sealed record Session(string UserId, string Origin, DateTimeOffset Created);

    internal IReadOnlyList<string> UpdateAccounts(IEnumerable<WebUiAccount> configured)
    {
        var next = configured.ToArray();
        if (next.Length > 32 || next.Any(account => account is null || !Guid.TryParseExact(account.Id, "N", out var id) || id.ToString("N") != account.Id ||
                !WebUiPassword.ValidUserName(account.UserName) || !WebUiPassword.ValidHash(account.PasswordHash) ||
                account.InputDirectory is { } directory && !Path.IsPathFullyQualified(directory) ||
                account.OutputDirectory is { } output && !Path.IsPathFullyQualified(output)) ||
            next.Select(account => account.Id).Distinct(StringComparer.Ordinal).Count() != next.Length ||
            next.Select(account => account.UserName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != next.Length)
            throw new ArgumentException("Invalid or duplicate Web UI accounts.");

        lock (gate)
        {
            var invalidated = accounts.Values.Where(old => !next.Any(account => account.Id == old.Id &&
                account.Enabled && account.PasswordHash == old.PasswordHash && account.UserName == old.UserName))
                .Select(account => account.Id).ToArray();
            foreach (var key in sessions.Where(pair => invalidated.Contains(pair.Value.UserId)).Select(pair => pair.Key).ToArray())
                sessions.Remove(key);
            // 账号设置可能比子进程刚回传的偏好稍旧，更新口令时保留最新偏好。
            for (var index = 0; index < next.Length; index++)
                if (accounts.TryGetValue(next[index].Id, out var previous) && previous.Preferences is not null)
                    next[index] = next[index] with
                    {
                        Preferences = previous.Preferences
                    };
            accounts.Clear();
            foreach (var account in next)
                accounts.Add(account.Id, account);
            return invalidated;
        }
    }

    internal WebUiAccount? FindAccount(string id)
    {
        lock (gate)
            return accounts.GetValueOrDefault(id);
    }

    internal void SetPreferences(string id, string preferences)
    {
        lock (gate)
            if (accounts.TryGetValue(id, out var account))
                accounts[id] = account with
                {
                    Preferences = preferences
                };
    }

    internal WebUiAccount[] GetAccounts()
    {
        lock (gate)
            return accounts.Values.ToArray();
    }

    internal void SetDirectories(string id, string? inputDirectory, string? outputDirectory)
    {
        if (inputDirectory is not null && !Path.IsPathFullyQualified(inputDirectory) ||
            outputDirectory is not null && !Path.IsPathFullyQualified(outputDirectory))
            throw new ArgumentException("Workspace directories must be absolute paths.");
        lock (gate)
        {
            if (!accounts.TryGetValue(id, out var account))
                throw new InvalidOperationException("The account no longer exists.");
            accounts[id] = account with
            {
                InputDirectory = inputDirectory,
                OutputDirectory = outputDirectory
            };
        }
    }

    internal bool TryLogin(string name, string password, string origin, out string? credential)
    {
        WebUiAccount? account;
        lock (gate)
            account = accounts.Values.FirstOrDefault(item => item.Enabled &&
                string.Equals(item.UserName, name, StringComparison.OrdinalIgnoreCase));
        // 不存在的账号也进行一次口令推导，避免按耗时判断用户名是否存在。
        var valid = WebUiPassword.Verify(password, account?.PasswordHash ?? dummyHash);
        credential = null;
        if (!valid || account is null)
            return false;
        lock (gate)
        {
            if (!accounts.TryGetValue(account.Id, out var current) || !current.Enabled || current.PasswordHash != account.PasswordHash || current.UserName != account.UserName)
                return false;
            var now = DateTimeOffset.UtcNow;
            foreach (var key in sessions.Where(pair => now - pair.Value.Created >= TimeSpan.FromDays(365)).Select(pair => pair.Key).ToArray())
                sessions.Remove(key);
            // 每个账号保留有限的浏览器登录，清除最早的凭据。
            var previous = sessions.Where(pair => pair.Value.UserId == account.Id).OrderBy(pair => pair.Value.Created).ToArray();
            foreach (var entry in previous.Take(Math.Max(0, previous.Length - 31)))
                sessions.Remove(entry.Key);
            credential = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            sessions.Add(Digest(credential), new Session(account.Id, origin, now));
            return true;
        }
    }

    internal WebUiLogin? Authenticate(string? credential, string origin)
    {
        if (credential is null || credential.Length != 64)
            return null;
        var digest = Digest(credential);
        lock (gate)
        {
            if (!sessions.TryGetValue(digest, out var session) || session.Origin != origin ||
                DateTimeOffset.UtcNow - session.Created >= TimeSpan.FromDays(365) ||
                !accounts.TryGetValue(session.UserId, out var account) || !account.Enabled)
                return null;
            return new WebUiLogin(account.Id, account.UserName, digest);
        }
    }

    internal void Logout(string sessionId)
    {
        lock (gate)
            sessions.Remove(sessionId);
    }

    private static string Digest(string credential) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));
}
