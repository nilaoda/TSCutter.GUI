using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.Utils.Files;

namespace TSCutter.GUI.Services.Application;

public enum StagedUpdateLayout
{
    WindowsExe,
    LinuxFolder,
    MacAppBundle
}

/// <summary>A verified package staged on disk, ready to be applied.</summary>
/// <summary>Result of the detached Windows update helper, read on the next launch.</summary>
public sealed record UpdateHelperResult(bool Success, string Message);

public sealed class StagedUpdate
{
    public required string PayloadDirectory { get; init; }
    public required string PackageDirectory { get; init; }
    public required StagedUpdateLayout Layout { get; init; }
    public required string InstallRoot { get; init; }
    public required string ExecutableName { get; init; }
}

/// <summary>
/// Downloads and verifies an update package, then replaces the running
/// installation. Verification happens before any file is replaced.
/// </summary>
public sealed class UpdateService
{
    private const string NoticeFileName = "THIRD_PARTY_NOTICES.txt";
    private const string WindowsExecutable = "TSCutterGUI.exe";
    private const string UnixExecutable = "TSCutterGUI";
    private const string MacBundle = "TSCutter.GUI.app";
    private const string ResultFileName = "UpdateResult.txt";

    private readonly HttpClient _http;

    public UpdateService(HttpClient? httpClient = null) => _http = httpClient ?? new HttpClient();

    /// <summary>Downloads, verifies and extracts a package without changing the installation.</summary>
    public async Task<StagedUpdate> PrepareAsync(UpdateInfo info, IProgress<double>? progress = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        var asset = info.Asset ?? throw new ArgumentException("The release has no matching package.", nameof(info));
        if (string.IsNullOrEmpty(asset.Sha256))
            throw new InvalidDataException("The release package has no checksum.");
        if (!IsTrustedDownload(asset.Url) || !IsSha256Hex(asset.Sha256))
            throw new InvalidDataException("The release package URL or checksum is not valid.");

        var packageDirectory = Path.Combine(Path.GetTempPath(), "TSCutter.GUI", "Updates",
            info.Tag, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageDirectory);
        var archivePath = Path.Combine(packageDirectory, asset.FileName + ".part");

        try
        {
            await DownloadAsync(asset, archivePath, progress, token).ConfigureAwait(false);

            // The archive, the extracted payload and a rollback copy are kept on disk.
            var required = asset.Size * 2 + 64L * 1024 * 1024;
            if (DiskSpaceUtil.TryGetFreeSpace(packageDirectory) is { } space
                && space.AvailableBytes < required)
                throw new IOException("Not enough free space to install the update.");

            if (asset.Size > 0 && new FileInfo(archivePath).Length != asset.Size)
                throw new InvalidDataException("The downloaded package size does not match the release manifest.");
            string checksum;
            await using (var stream = File.OpenRead(archivePath))
                checksum = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
            if (!string.Equals(checksum, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded package failed checksum verification.");

            var payload = Path.Combine(packageDirectory, "payload");
            ExtractSafely(archivePath, payload);
            var layout = DetectLayout(payload);
            var installRoot = GetInstallRoot(layout);

            return new StagedUpdate
            {
                PayloadDirectory = payload,
                PackageDirectory = packageDirectory,
                Layout = layout,
                InstallRoot = installRoot,
                ExecutableName = layout == StagedUpdateLayout.WindowsExe ? WindowsExecutable : UnixExecutable
            };
        }
        catch
        {
            TryDeleteDirectory(packageDirectory);
            throw;
        }
    }

    private async Task DownloadAsync(UpdateAsset asset, string destination, IProgress<double>? progress,
        CancellationToken token)
    {
        using var response = await _http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
            81920, useAsync: true);
        var total = response.Content.Headers.ContentLength ?? asset.Size;
        var buffer = new byte[81920];
        long received = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            received += count;
            if (total > 0)
                progress?.Report(Math.Min(1.0, (double)received / total));
        }
    }

    /// <summary>Rejects absolute paths and entries that escape the extraction root.</summary>
    private static void ExtractSafely(string archivePath, string payload)
    {
        var root = Path.GetFullPath(payload);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;
            if (Path.IsPathRooted(entry.FullName) || entry.FullName.Contains(".."))
                throw new InvalidDataException("The update package contains an unsafe path.");
            var destination = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidDataException("The update package contains an unsafe path.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    private static bool IsTrustedDownload(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static bool IsSha256Hex(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    internal static StagedUpdateLayout DetectLayout(string payload)
    {
        if (File.Exists(Path.Combine(payload, WindowsExecutable)))
            return StagedUpdateLayout.WindowsExe;
        if (File.Exists(Path.Combine(payload, MacBundle, "Contents", "MacOS", UnixExecutable)))
            return StagedUpdateLayout.MacAppBundle;
        if (File.Exists(Path.Combine(payload, UnixExecutable)))
            return StagedUpdateLayout.LinuxFolder;
        throw new InvalidDataException("The update package does not contain a supported application.");
    }

    internal static string GetInstallRoot(StagedUpdateLayout layout)
    {
        var processPath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory,
            layout == StagedUpdateLayout.WindowsExe ? WindowsExecutable : UnixExecutable);
        if (layout != StagedUpdateLayout.MacAppBundle)
            return Path.GetDirectoryName(processPath)!;
        var directory = new DirectoryInfo(Path.GetDirectoryName(processPath)!);
        while (directory is not null && !directory.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            directory = directory.Parent;
        return directory?.FullName ?? Path.GetDirectoryName(processPath)!;
    }

    /// <summary>
    /// Applies a staged package. On Windows a short-lived copy of the current
    /// executable is started with --apply-update; the caller must shut down
    /// immediately so that the helper can replace the running file.
    /// </summary>
    public Task ApplyAsync(StagedUpdate staged, IReadOnlyList<string>? relaunchArgs = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(staged);
        var arguments = relaunchArgs ?? [];
        if (!TryAcquireUpdateLock())
            throw new IOException("Another instance is already installing an update.");
        if (OperatingSystem.IsWindows() && staged.Layout == StagedUpdateLayout.WindowsExe)
        {
            try
            {
                StartWindowsHelper(staged, arguments);
            }
            catch
            {
                ReleaseUpdateLock();
                throw;
            }
            return Task.CompletedTask;
        }
        try
        {
            ApplyUnixUpdate(staged, arguments);
        }
        finally
        {
            ReleaseUpdateLock();
        }
        return Task.CompletedTask;
    }

    private static void StartWindowsHelper(StagedUpdate staged, IReadOnlyList<string> relaunchArgs)
    {
        var runningExe = Environment.ProcessPath ?? Path.Combine(staged.InstallRoot, WindowsExecutable);
        var helper = Path.Combine(Path.GetTempPath(),
            $"TSCutter.GUI.Updater.{Guid.NewGuid():N}.exe");
        File.Copy(runningExe, helper, overwrite: true);
        var stagedExe = Path.Combine(staged.PayloadDirectory, WindowsExecutable);
        var targetExe = Path.Combine(staged.InstallRoot, Path.GetFileName(runningExe));

        var start = new ProcessStartInfo(helper)
        {
            UseShellExecute = false,
            WorkingDirectory = staged.InstallRoot
        };
        start.ArgumentList.Add("--apply-update");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        start.ArgumentList.Add(stagedExe);
        start.ArgumentList.Add(targetExe);
        start.ArgumentList.Add(staged.PayloadDirectory);
        foreach (var arg in relaunchArgs)
            start.ArgumentList.Add(arg);
        if (Process.Start(start) is null)
            throw new InvalidOperationException("Could not start the update helper.");
    }

    /// <summary>Entrypoint for the hidden --apply-update helper argument.</summary>
    public static int RunWindowsHelper(string pidArgument, string stagedExe, string targetExe,
        string payloadDirectory, IReadOnlyList<string> relaunchArgs)
    {
        try
        {
            if (!int.TryParse(pidArgument, out var parentPid))
                throw new ArgumentException("Invalid update helper arguments.");
            WaitForExit(parentPid);

            var backup = targetExe + ".old";
            TryDelete(backup);
            ReplaceWithRetry(stagedExe, targetExe);
            var notice = Path.Combine(payloadDirectory, NoticeFileName);
            if (File.Exists(notice))
                ReplaceWithRetry(notice, Path.Combine(Path.GetDirectoryName(targetExe)!, NoticeFileName));

            // Do not discard the backup until the new executable has actually started.
            var relaunched = Relaunch(targetExe, relaunchArgs, Path.GetDirectoryName(targetExe)!);
            if (relaunched is null)
                throw new InvalidOperationException("The updated application did not start.");
            bool healthy;
            try
            {
                relaunched.WaitForExit(2000);
                healthy = !relaunched.HasExited || relaunched.ExitCode == 0;
            }
            catch (InvalidOperationException)
            {
                healthy = false;
            }
            if (!healthy)
            {
                try
                {
                    if (!relaunched.HasExited)
                        relaunched.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Could not stop the failed update: {ex.Message}");
                }
                throw new InvalidOperationException("The updated application exited with an error.");
            }
            relaunched.Dispose();
            WriteHelperResult(success: true, "");
            TryDelete(backup);
            ReleaseUpdateLock();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            WriteHelperResult(success: false, ex.Message);
            ReleaseUpdateLock();
            var backup = targetExe + ".old";
            if (File.Exists(backup))
            {
                // A failed relaunch may still be running; it must release the file first.
                TryDelete(targetExe);
                TryRestore(backup, targetExe);
            }
            return 1;
        }
    }

    internal static void ApplyUnixUpdate(StagedUpdate staged, IReadOnlyList<string> relaunchArgs,
        string? backupRoot = null, bool relaunch = true)
    {
        var sourceRoot = staged.Layout == StagedUpdateLayout.MacAppBundle
            ? Path.Combine(staged.PayloadDirectory, MacBundle)
            : staged.PayloadDirectory;
        backupRoot ??= Path.Combine(Path.GetTempPath(), "TSCutter.GUI", "Rollback", Guid.NewGuid().ToString("N"));
        var replaced = new List<(string Backup, string Target)>();
        try
        {
            foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(sourceRoot, source);
                var target = Path.Combine(staged.InstallRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    var backup = Path.Combine(backupRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(target, backup, overwrite: true);
                    replaced.Add((backup, target));
                }
                ReplaceFile(source, target);
            }
            if (!OperatingSystem.IsWindows())
            {
                var relativeExecutable = staged.Layout == StagedUpdateLayout.MacAppBundle
                    ? Path.Combine("Contents", "MacOS", UnixExecutable)
                    : UnixExecutable;
                SetExecutableBit(Path.Combine(staged.InstallRoot, relativeExecutable));
            }
            var launchedExecutable = staged.Layout == StagedUpdateLayout.MacAppBundle
                ? Path.Combine(staged.InstallRoot, "Contents", "MacOS", UnixExecutable)
                : Path.Combine(staged.InstallRoot, UnixExecutable);
            if (relaunch)
                Relaunch(launchedExecutable, relaunchArgs, staged.InstallRoot);
        }
        catch
        {
            foreach (var (backup, target) in replaced.AsEnumerable().Reverse())
                TryRestore(backup, target);
            throw;
        }
    }

    private static Process? Relaunch(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        return Process.Start(start);
    }

    private static void ReplaceWithRetry(string source, string destination)
    {
        var backup = destination + ".old";
        if (File.Exists(backup))
            TryDelete(backup);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                File.Move(destination, backup, overwrite: true);
                break;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
        }
        File.Move(source, destination, overwrite: true);
    }

    private static void ReplaceFile(string source, string destination)
    {
        var temporary = destination + ".new." + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, temporary, overwrite: true);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void SetExecutableBit(string path)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(path))
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute |
                                       UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    private static void WaitForExit(int pid)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.WaitForExit(200);
                if (process.HasExited)
                    return;
            }
            catch (ArgumentException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }
        }
    }

    /// <summary>Fails early when the application folder cannot be modified.</summary>
    internal static void EnsureInstallLocationIsWritable(string directory)
    {
        var probe = Path.Combine(directory, ".tscutter-update-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var stream = File.Create(probe, 1, FileOptions.DeleteOnClose);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new IOException("The application folder is not writable. Use the download page instead.", ex);
        }
        finally
        {
            TryDelete(probe);
        }
    }

    internal static string UpdateLockFilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TSCutter.GUI", "update.lock");

    internal static bool TryAcquireUpdateLock()
    {
        try
        {
            if (File.Exists(UpdateLockFilePath))
            {
                var owner = File.ReadAllText(UpdateLockFilePath).Trim();
                if (AppInstanceIdentity.TryParseFileKey(owner, out var identity)
                    && identity != AppInstanceIdentity.Current
                    && AppInstanceIdentity.IsRunning(identity))
                    return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(UpdateLockFilePath)!);
            File.WriteAllText(UpdateLockFilePath, AppInstanceIdentity.Current.FileKey);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to acquire update lock: {ex.Message}");
            return false;
        }
    }

    internal static void ReleaseUpdateLock()
    {
        try
        {
            if (File.Exists(UpdateLockFilePath))
                File.Delete(UpdateLockFilePath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to release update lock: {ex.Message}");
        }
    }

    private static string ResultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TSCutter.GUI", ResultFileName);

    internal static void WriteHelperResult(bool success, string message)
    {
        try
        {
            var path = ResultFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, success ? "ok\n" : "error\n" + message);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to write update result: {ex.Message}");
        }
    }

    internal static UpdateHelperResult? ReadHelperResult()
    {
        try
        {
            var path = ResultFilePath;
            if (!File.Exists(path))
                return null;
            var lines = File.ReadAllLines(path);
            if (lines.Length == 0)
                return null;
            return new UpdateHelperResult(
                string.Equals(lines[0], "ok", StringComparison.OrdinalIgnoreCase),
                string.Join(Environment.NewLine, lines.Skip(1)));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to read update result: {ex.Message}");
            return null;
        }
    }

    internal static void ClearHelperResult()
    {
        try
        {
            var path = ResultFilePath;
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to clear update result: {ex.Message}");
        }
    }

    private static void TryRestore(string backup, string target)
    {
        try
        {
            File.Move(backup, target, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to restore {target}: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to delete {path}: {ex.Message}");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to delete {path}: {ex.Message}");
        }
    }
}
