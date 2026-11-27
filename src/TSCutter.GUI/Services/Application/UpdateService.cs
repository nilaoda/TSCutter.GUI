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
    private const string StagingPrefix = ".TSCutter.GUI.update-";
    private const char LockOwnerSeparator = '|';
    // A portable folder on a shared drive may be locked by another machine, whose
    // processes cannot be inspected locally. Treat a recent foreign lock as active.
    private static readonly TimeSpan ForeignLockExpiry = TimeSpan.FromHours(2);

    private readonly HttpClient _http;
    private readonly string? _installRootOverride;

    public UpdateService(HttpClient? httpClient = null) : this(httpClient, null) { }

    /// <summary>
    /// Uses a supplied install root instead of discovering it from the running
    /// executable. Keeps tests independent of where the test host SDK lives.
    /// </summary>
    internal UpdateService(string installRoot) : this(null, installRoot) { }

    internal UpdateService(HttpClient? httpClient, string? installRootOverride)
    {
        _http = httpClient ?? new HttpClient();
        _installRootOverride = installRootOverride;
    }

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

        // Windows and Linux only replace files inside the install folder, so they
        // stage there. macOS swaps the whole bundle, so staging must be a sibling
        // to keep the rename on one filesystem (an external volume is common).
        var currentInstallRoot = _installRootOverride ?? GetCurrentInstallRoot();
        var packageDirectory = Path.Combine(GetStagingParent(currentInstallRoot),
            StagingPrefix + Guid.NewGuid().ToString("N"));
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
            var installRoot = _installRootOverride ?? GetInstallRoot(layout);

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

    /// <summary>Install directory for the currently running platform.</summary>
    internal static string GetCurrentInstallRoot()
    {
        var processPath = Environment.ProcessPath ?? AppContext.BaseDirectory;
        if (OperatingSystem.IsMacOS())
        {
            var bundle = new DirectoryInfo(Path.GetDirectoryName(processPath)!);
            while (bundle is not null && !bundle.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                bundle = bundle.Parent;
            if (bundle is not null)
                return bundle.FullName;
        }
        return Path.GetDirectoryName(processPath) ?? AppContext.BaseDirectory;
    }

    /// <summary>
    /// Folder that holds a staging directory for the given install root. Windows
    /// stages inside the install folder because only files in that folder are
    /// replaced; Unix stages beside it so whole-directory renames stay local.
    /// </summary>
    internal static string GetStagingParent(string installRoot, StagedUpdateLayout? layout = null)
    {
        // Windows and Linux only replace files inside the install folder, so they
        // stage there. A macOS .app is swapped as a whole, so its staging folder
        // must be a sibling to keep the rename on the same filesystem.
        var macBundle = layout == StagedUpdateLayout.MacAppBundle
            || (layout is null && OperatingSystem.IsMacOS());
        if (!macBundle)
            return installRoot;
        return Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(installRoot)) ?? installRoot;
    }

    internal static string GetInstallRoot(StagedUpdateLayout layout)
    {
        var processPath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory,
            layout == StagedUpdateLayout.WindowsExe ? WindowsExecutable : UnixExecutable);
        if (layout != StagedUpdateLayout.MacAppBundle)
            return Path.GetDirectoryName(processPath) ?? AppContext.BaseDirectory;
        var directory = new DirectoryInfo(Path.GetDirectoryName(processPath) ?? AppContext.BaseDirectory);
        while (directory is not null && !directory.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            directory = directory.Parent;
        return directory?.FullName ?? Path.GetDirectoryName(processPath) ?? AppContext.BaseDirectory;
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
        // The new install is running; its staging files are no longer needed.
        TryDeleteDirectory(staged.PackageDirectory);
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
        start.ArgumentList.Add(staged.PackageDirectory);
        // The helper runs from a temp copy of the exe. Pass the active data
        // directory so its lock and result file live where the app will look.
        start.ArgumentList.Add(AppDataPaths.ActiveDataDirectory);
        foreach (var arg in relaunchArgs)
            start.ArgumentList.Add(arg);
        if (Process.Start(start) is null)
            throw new InvalidOperationException("Could not start the update helper.");
    }

    /// <summary>Entrypoint for the hidden --apply-update helper argument.</summary>
    public static int RunWindowsHelper(string pidArgument, string stagedExe, string targetExe,
        string payloadDirectory, string packageDirectory, IReadOnlyList<string> relaunchArgs,
        string? dataDirectory = null)
    {
        if (!string.IsNullOrEmpty(dataDirectory))
            AppDataPaths.UseDataDirectory(dataDirectory);
        var backup = targetExe + ".old";
        var installDirectory = Path.GetDirectoryName(targetExe)!;
        var noticeTarget = Path.Combine(installDirectory, NoticeFileName);
        var noticeBackup = noticeTarget + ".old";
        Process? relaunched = null;
        var replaced = false;
        var noticeReplaced = false;
        try
        {
            if (!int.TryParse(pidArgument, out var parentPid))
                throw new ArgumentException("Invalid update helper arguments.");
            WaitForExit(parentPid);

            TryDelete(backup);
            ReplaceWithRetry(stagedExe, targetExe);
            replaced = true;
            var notice = Path.Combine(payloadDirectory, NoticeFileName);
            if (File.Exists(notice))
            {
                TryDelete(noticeBackup);
                ReplaceWithRetry(notice, noticeTarget);
                noticeReplaced = true;
            }
            // Do not discard the backup until the new executable has actually started.
            relaunched = Relaunch(targetExe, relaunchArgs, installDirectory);
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
                Kill(relaunched);
                throw new InvalidOperationException("The updated application exited with an error.");
            }
            relaunched.Dispose();
            relaunched = null;
            WriteHelperResult(success: true, "");
            TryDelete(backup);
            if (noticeReplaced)
                TryDelete(noticeBackup);
            TryDeleteDirectory(packageDirectory);
            ReleaseUpdateLock();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Kill(relaunched);
            WriteHelperResult(success: false, ex.Message);
            if (replaced)
            {
                if (File.Exists(backup))
                {
                    TryDelete(targetExe);
                    TryRestore(backup, targetExe);
                }
                if (noticeReplaced && File.Exists(noticeBackup))
                {
                    TryDelete(noticeTarget);
                    TryRestore(noticeBackup, noticeTarget);
                }
                // Show the error in the restored version instead of just closing.
                Relaunch(targetExe, relaunchArgs, installDirectory);
            }
            TryDeleteDirectory(packageDirectory);
            ReleaseUpdateLock();
            return 1;
        }
    }

    private static void Kill(Process? process)
    {
        if (process is null)
            return;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not stop the failed update: {ex.Message}");
        }
    }

    /// <summary>
    /// Swaps the whole install directory (or macOS bundle) with the staged one.
    /// This removes files that no longer exist in the new version and leaves no
    /// half-updated installation: either the new tree is in place or the old one
    /// is restored.
    /// </summary>
    internal static void SwapDirectories(string source, string target, string backup, Action? afterSwap = null)
    {
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException(source);
        if (Directory.Exists(backup))
            TryDeleteDirectory(backup);
        // Directory.Move cannot create a missing destination parent. The default
        // backup path is <package>/rollback/install, so create "rollback" first.
        var backupParent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(backup));
        if (!string.IsNullOrEmpty(backupParent))
            Directory.CreateDirectory(backupParent);
        Directory.Move(target, backup);
        try
        {
            Directory.Move(source, target);
            afterSwap?.Invoke();
        }
        catch
        {
            if (Directory.Exists(backup))
            {
                TryDeleteDirectory(target);
                Directory.Move(backup, target);
            }
            throw;
        }
    }

    internal static void ApplyUnixUpdate(StagedUpdate staged, IReadOnlyList<string> relaunchArgs,
        string? backupRoot = null, bool relaunch = true)
    {
        var bundle = staged.Layout == StagedUpdateLayout.MacAppBundle;
        var sourceRoot = bundle
            ? Path.Combine(staged.PayloadDirectory, MacBundle)
            : staged.PayloadDirectory;
        // The backup lives in the staging folder beside the install root, so all
        // renames stay on the same filesystem.
        backupRoot ??= Path.Combine(staged.PackageDirectory, "rollback");

        try
        {
            if (bundle)
            {
                // A .app bundle is self-contained: swap it as a whole so files
                // removed in the new release do not linger inside the bundle.
                // Portable data lives beside the bundle and is untouched, so a
                // signed bundle never has to be modified at runtime.
                // The marker lives at the bundle root. Preserve the installation's
                // choice: keep it when portable, remove it from the incoming bundle
                // when the user opted out. Release packages do not carry a marker.
                var stagedMarker = Path.Combine(sourceRoot, AppDataPaths.MarkerFileName);
                if (IsPortableOptedOut(staged))
                {
                    if (File.Exists(stagedMarker))
                        TryDelete(stagedMarker);
                }
                else
                {
                    var marker = Path.Combine(staged.InstallRoot, AppDataPaths.MarkerFileName);
                    if (File.Exists(marker))
                        File.Copy(marker, stagedMarker, overwrite: true);
                }
                SwapDirectories(sourceRoot, staged.InstallRoot, Path.Combine(backupRoot, "install"),
                    () => FinishUnixUpdate(staged, relaunchArgs, relaunch));
            }
            else
            {
                // A Linux package contains only its own files at the zip root and may
                // be extracted beside unrelated user files. Replace just those files.
                ApplyLinuxFiles(staged, sourceRoot, backupRoot, relaunchArgs, relaunch);
            }
        }
        finally
        {
            // Backups are discarded after success and moved back after a failure.
            TryDeleteDirectory(backupRoot);
        }
    }

    private static void ApplyLinuxFiles(StagedUpdate staged, string sourceRoot, string backupRoot,
        IReadOnlyList<string> relaunchArgs, bool relaunch)
    {
        var replaced = new List<(string Backup, string Target)>();
        var created = new List<string>();
        // An update must preserve the install's choice: a marker in the package
        // never turns a normal installation portable.
        var installHasMarker = File.Exists(Path.Combine(staged.InstallRoot, AppDataPaths.MarkerFileName));
        try
        {
            foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(sourceRoot, source);
                if (relative == AppDataPaths.MarkerFileName && !installHasMarker)
                    continue;
                var target = Path.Combine(staged.InstallRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    var backup = Path.Combine(backupRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Move(target, backup, overwrite: true);
                    replaced.Add((backup, target));
                }
                else
                {
                    created.Add(target);
                }

                // Stage the new file next to its target so the final rename cannot
                // cross a filesystem boundary.
                var temporary = target + ".new." + Guid.NewGuid().ToString("N");
                try
                {
                    File.Copy(source, temporary, overwrite: true);
                    File.Move(temporary, target, overwrite: true);
                }
                finally
                {
                    TryDelete(temporary);
                }
            }

            FinishUnixUpdate(staged, relaunchArgs, relaunch);
        }
        catch
        {
            foreach (var target in created)
                TryDelete(target);
            foreach (var (backup, target) in replaced.AsEnumerable().Reverse())
                TryRestore(backup, target);
            throw;
        }
    }

    private static bool IsPortableOptedOut(StagedUpdate staged)
    {
        var root = staged.Layout == StagedUpdateLayout.MacAppBundle
            ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(staged.InstallRoot)) ?? staged.InstallRoot
            : staged.InstallRoot;
        return File.Exists(Path.Combine(root, AppDataPaths.DataDirectoryName, AppDataPaths.OptOutFileName));
    }

    private static void FinishUnixUpdate(StagedUpdate staged, IReadOnlyList<string> relaunchArgs, bool relaunch)
    {
        if (!OperatingSystem.IsWindows())
        {
            var relativeExecutable = staged.Layout == StagedUpdateLayout.MacAppBundle
                ? Path.Combine("Contents", "MacOS", UnixExecutable)
                : UnixExecutable;
            SetExecutableBit(Path.Combine(staged.InstallRoot, relativeExecutable));
        }
        if (!relaunch)
            return;
        var launchedExecutable = staged.Layout == StagedUpdateLayout.MacAppBundle
            ? Path.Combine(staged.InstallRoot, "Contents", "MacOS", UnixExecutable)
            : Path.Combine(staged.InstallRoot, UnixExecutable);
        var launched = Relaunch(launchedExecutable, relaunchArgs, staged.InstallRoot)
            ?? throw new InvalidOperationException("The updated application did not start.");
        using (launched)
        {
            // Same early-exit check as the Windows helper: an app that starts and
            // immediately fails must roll the update back, not keep the new files.
            bool healthy;
            try
            {
                launched.WaitForExit(2000);
                healthy = !launched.HasExited || launched.ExitCode == 0;
            }
            catch (InvalidOperationException)
            {
                healthy = false;
            }
            if (!healthy)
            {
                Kill(launched);
                throw new InvalidOperationException("The updated application exited with an error.");
            }
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

    /// <summary>Fails early when the folder that will hold staging and the swap cannot be modified.</summary>
    internal static void EnsureDirectoryIsWritable(string directory)
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

    /// <summary>
    /// Removes staging folders and helper copies left behind by an earlier update.
    /// Called on startup, when files still in use by the previous process can be deleted.
    /// </summary>
    internal static void CleanupStaleTempFiles()
    {
        try
        {
            // Never clean up while another instance is applying an update.
            if (File.Exists(UpdateLockFilePath))
            {
                var owner = File.ReadAllText(UpdateLockFilePath).Trim();
                if (TryParseLockOwner(owner, out var machine, out var identity)
                    && ((!string.IsNullOrEmpty(machine) && IsForeignLockActive(UpdateLockFilePath, machine))
                        || AppInstanceIdentity.IsRunning(identity)))
                    return;
                TryDelete(UpdateLockFilePath);
            }

            var cutoff = DateTime.UtcNow.AddDays(-1);
            var installRoot = GetCurrentInstallRoot();
            // Windows/Linux stage inside the install folder; macOS stages beside
            // the bundle. Check both so leftovers from either layout are removed.
            RemoveStagingFolders(installRoot, cutoff);
            var installParent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(installRoot));
            if (installParent is not null
                && !string.Equals(installParent, installRoot, StringComparison.Ordinal))
                RemoveStagingFolders(installParent, cutoff);

            // Legacy temp layout from earlier builds: check each package folder,
            // not the shared Updates root, so an active download is not removed.
            var updatesRoot = Path.Combine(Path.GetTempPath(), "TSCutter.GUI", "Updates");
            if (Directory.Exists(updatesRoot))
                foreach (var tag in Directory.EnumerateDirectories(updatesRoot))
                {
                    foreach (var package in Directory.EnumerateDirectories(tag))
                        if (Directory.GetCreationTimeUtc(package) < cutoff)
                            TryDeleteDirectory(package);
                    if (!Directory.EnumerateFileSystemEntries(tag).Any())
                        TryDeleteDirectory(tag);
                }
            foreach (var helper in Directory.EnumerateFiles(Path.GetTempPath(), "TSCutter.GUI.Updater.*.exe"))
                if (File.GetCreationTimeUtc(helper) < cutoff)
                    TryDelete(helper);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to clean stale update files: {ex.Message}");
        }
    }

    private static void RemoveStagingFolders(string directory, DateTime cutoff)
    {
        if (!Directory.Exists(directory))
            return;
        foreach (var path in Directory.EnumerateDirectories(directory, StagingPrefix + "*"))
            if (Directory.GetCreationTimeUtc(path) < cutoff)
                TryDeleteDirectory(path);
    }

    internal static string? UpdateLockFilePathOverride { get; set; }

    internal static string UpdateLockFilePath
    {
        get => UpdateLockFilePathOverride ?? AppDataPaths.GetPath(
            Environment.SpecialFolder.LocalApplicationData, "update.lock");
        set => UpdateLockFilePathOverride = value;
    }

    internal static bool TryAcquireUpdateLock()
    {
        try
        {
            if (File.Exists(UpdateLockFilePath))
            {
                var owner = File.ReadAllText(UpdateLockFilePath).Trim();
                if (TryParseLockOwner(owner, out var machine, out var identity))
                {
                    if (!string.IsNullOrEmpty(machine) && IsForeignLockActive(UpdateLockFilePath, machine))
                        return false;
                    if (identity != AppInstanceIdentity.Current && AppInstanceIdentity.IsRunning(identity))
                        return false;
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(UpdateLockFilePath)!);
            File.WriteAllText(UpdateLockFilePath,
                Environment.MachineName + LockOwnerSeparator + AppInstanceIdentity.Current.FileKey);
            AppDataPaths.RestrictFile(UpdateLockFilePath);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to acquire update lock: {ex.Message}");
            return false;
        }
    }

    /// <summary>Reads both the old bare file key and the machine-qualified lock owner.</summary>
    private static bool TryParseLockOwner(string text, out string? machine, out AppInstanceIdentity identity)
    {
        machine = null;
        identity = default;
        var separator = text.IndexOf(LockOwnerSeparator);
        var key = separator < 0 ? text : text[(separator + 1)..];
        if (!AppInstanceIdentity.TryParseFileKey(key, out identity))
            return false;
        if (separator >= 0)
        {
            machine = text[..separator];
            if (string.IsNullOrWhiteSpace(machine))
                return false;
        }
        return true;
    }

    private static bool IsForeignLockActive(string path, string machine) =>
        !string.Equals(machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
        && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < ForeignLockExpiry;

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

    private static string ResultFilePath => AppDataPaths.GetPath(
        Environment.SpecialFolder.LocalApplicationData, ResultFileName);

    internal static void WriteHelperResult(bool success, string message)
    {
        try
        {
            var path = ResultFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, success ? "ok\n" : "error\n" + message);
            AppDataPaths.RestrictFile(path);
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
