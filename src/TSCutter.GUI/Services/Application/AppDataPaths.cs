using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace TSCutter.GUI.Services.Application;

/// <summary>
/// Resolves persistent application data locations. When a "TSCutter.GUI.portable"
/// marker file sits next to the executable, every data path is redirected into a
/// "data" folder; otherwise the normal user profile folders are used. On macOS
/// the data folder is beside the .app bundle so writing to it never modifies a
/// (possibly signed) bundle.
/// </summary>
public static class AppDataPaths
{
    public const string MarkerFileName = "TSCutter.GUI.portable";
    public const string OptOutFileName = "TSCutter.GUI.portable.disabled";
    public const string DataDirectoryName = "data";
    public const string AppDirectoryName = "TSCutter.GUI";
    // Written only after every intended file has been copied. Its absence means
    // either a foreign folder or an interrupted migration that must be retried.
    private const string CompletionMarkerFileName = ".tscutter-portable-ready";

    // Test seams; production code leaves these null.
    internal static string? InstallRootOverride { get; set; }
    internal static string? ApplicationDataRootOverride { get; set; }
    internal static string? LocalApplicationDataRootOverride { get; set; }

    /// <summary>Directory that contains the executable (or the macOS .app bundle).</summary>
    public static string InstallRoot
    {
        get
        {
            if (InstallRootOverride is not null)
                return InstallRootOverride;
            var processPath = Environment.ProcessPath ?? AppContext.BaseDirectory;
            if (OperatingSystem.IsMacOS())
            {
                var bundle = new DirectoryInfo(Path.GetDirectoryName(processPath) ?? AppContext.BaseDirectory);
                while (bundle is not null && !bundle.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                    bundle = bundle.Parent;
                if (bundle is not null)
                    return bundle.FullName;
            }
            return Path.GetDirectoryName(processPath) ?? AppContext.BaseDirectory;
        }
    }

    /// <summary>Parent of the install folder: the install folder itself, or next to the macOS bundle.</summary>
    public static string DataRoot => OperatingSystem.IsMacOS() && InstallRootOverride is null
        ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(InstallRoot)) ?? InstallRoot
        : InstallRoot;

    /// <summary>Used by the Windows update helper so it shares the parent's storage context.</summary>
    internal static string? DataDirectoryOverride { get; private set; }

    public static string DataDirectory => DataDirectoryOverride ?? Path.Combine(DataRoot, DataDirectoryName);
    public static string OptOutFilePath => Path.Combine(DataDirectory, OptOutFileName);
    private static string CompletionFilePath => Path.Combine(DataDirectory, CompletionMarkerFileName);

    /// <summary>Directory that currently holds the settings, portable or profile-based.</summary>
    public static string ActiveDataDirectory => GetPath(Environment.SpecialFolder.ApplicationData);

    private static bool _portableUnavailable;

    /// <summary>Raw reason portable storage was unavailable, for localized display in the UI.</summary>
    public static string? FallbackMessage { get; private set; }

    public static bool HasMarker => File.Exists(Path.Combine(InstallRoot, MarkerFileName));
    public static bool IsPortable =>
        DataDirectoryOverride is not null || (!_portableUnavailable && HasMarker);
    public static bool IsOptedOut => File.Exists(OptOutFilePath);

    internal static void UseDataDirectory(string directory) => DataDirectoryOverride = directory;

    internal static void ResetForTests()
    {
        DataDirectoryOverride = null;
        _portableUnavailable = false;
        FallbackMessage = null;
    }

    /// <summary>
    /// Returns an app-data path for the given profile root. In portable mode both
    /// application-data roots map to the same data folder beside the install.
    /// </summary>
    public static string GetPath(Environment.SpecialFolder folder, params string[] segments)
    {
        var root = IsPortable
            ? DataDirectory
            : Path.Combine(ProfileRoot(folder), AppDirectoryName);
        return segments.Length == 0 ? root : Path.Combine(new[] { root }.Concat(segments).ToArray());
    }

    private static string ProfileRoot(Environment.SpecialFolder folder) => folder switch
    {
        Environment.SpecialFolder.ApplicationData =>
            ApplicationDataRootOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.SpecialFolder.LocalApplicationData =>
            LocalApplicationDataRootOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        _ => Environment.GetFolderPath(folder)
    };

    /// <summary>
    /// Called once at startup. Seeds portable data when the marker is present, or,
    /// when the marker has been deleted, restores profile data and records the
    /// opt-out so later in-place updates do not turn portable mode back on.
    /// </summary>
    public static void Initialize()
    {
        if (HasMarker)
        {
            if (_portableUnavailable)
                return;
            TryDelete(OptOutFilePath);
            EnsurePortableData();
            return;
        }

        // A data folder only belongs to us if migration actually completed.
        // This avoids touching an unrelated folder that happens to be named data.
        if (DataDirectoryOverride is not null || !File.Exists(CompletionFilePath))
            return;
        WriteOptOut();
        RestoreFromPortableData();
    }

    private static void EnsurePortableData()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var probe = Path.Combine(DataDirectory,
                ".tscutter-write-test-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            TryDelete(probe);
            RestrictDirectory(DataDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only install folder cannot hold portable data. Fall back to
            // the user profile so the app still starts with writable settings.
            _portableUnavailable = true;
            FallbackMessage = ex.Message;
            Console.Error.WriteLine(
                $"Portable data folder is unavailable: {ex.Message}. Using the user profile instead.");
            return;
        }

        if (File.Exists(CompletionFilePath))
            return;
        try
        {
            var configRoot = Path.Combine(ProfileRoot(Environment.SpecialFolder.ApplicationData), AppDirectoryName);
            CopyFileIfPresent(configRoot, DataDirectory, "config.json");
            CopyFileIfPresent(configRoot, DataDirectory, "remote-editing.json");
            CopyFileIfPresent(configRoot, DataDirectory, "saved-frames.json");

            var recovery = Path.Combine(ProfileRoot(Environment.SpecialFolder.LocalApplicationData),
                AppDirectoryName, "Recovery");
            if (Directory.Exists(recovery))
                CopyDirectory(recovery, Path.Combine(DataDirectory, "Recovery"));

            File.WriteAllText(CompletionFilePath, "");
            RestrictFile(CompletionFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leave the completion marker absent so the next launch retries.
            Console.Error.WriteLine($"Portable data migration incomplete: {ex.Message}");
        }
    }

    private static void RestoreFromPortableData()
    {
        var configRoot = Path.Combine(ProfileRoot(Environment.SpecialFolder.ApplicationData), AppDirectoryName);
        if (File.Exists(Path.Combine(configRoot, "config.json")))
            return;
        try
        {
            Directory.CreateDirectory(configRoot);
            CopyFileIfPresent(DataDirectory, configRoot, "config.json");
            CopyFileIfPresent(DataDirectory, configRoot, "remote-editing.json");
            CopyFileIfPresent(DataDirectory, configRoot, "saved-frames.json");

            var recovery = Path.Combine(DataDirectory, "Recovery");
            if (Directory.Exists(recovery))
                CopyDirectory(recovery, Path.Combine(ProfileRoot(Environment.SpecialFolder.LocalApplicationData),
                    AppDirectoryName, "Recovery"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not restore portable data to the user profile: {ex.Message}");
        }
    }

    private static void WriteOptOut()
    {
        try
        {
            if (!Directory.Exists(DataDirectory))
                return;
            File.WriteAllText(OptOutFilePath, "");
            RestrictFile(OptOutFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not record the portable opt-out: {ex.Message}");
        }
    }

    private static void CopyFileIfPresent(string sourceDirectory, string targetDirectory, string name)
    {
        var source = Path.Combine(sourceDirectory, name);
        if (!File.Exists(source))
            return;
        Directory.CreateDirectory(targetDirectory);
        var target = Path.Combine(targetDirectory, name);
        // Overwrite so a retry can complete a migration that failed halfway.
        File.Copy(source, target, overwrite: true);
        RestrictFile(target);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        RestrictDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            RestrictFile(target);
        }
    }

    /// <summary>
    /// Keeps portable data private. On Unix owner-only modes are enough; on Windows
    /// the install folder's inherited ACLs may grant access to other users, so the
    /// data files and folders get an explicit protected ACL.
    /// </summary>
    internal static void RestrictFile(string path)
    {
        if (!File.Exists(path))
            return;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var info = new FileInfo(path);
                var security = info.GetAccessControl();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                foreach (var rule in PrivateRules(InheritanceFlags.None))
                    security.AddAccessRule(rule);
                info.SetAccessControl(security);
            }
            else
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or PlatformNotSupportedException or SystemException)
        {
            Console.Error.WriteLine($"Could not restrict {path}: {ex.Message}");
        }
    }

    internal static void RestrictDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var info = new DirectoryInfo(path);
                var security = info.GetAccessControl();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                foreach (var rule in PrivateRules(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit))
                    security.AddAccessRule(rule);
                info.SetAccessControl(security);
            }
            else
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or PlatformNotSupportedException or SystemException)
        {
            Console.Error.WriteLine($"Could not restrict {path}: {ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<FileSystemAccessRule> PrivateRules(InheritanceFlags inheritance)
    {
        var user = WindowsIdentity.GetCurrent().User;
        if (user is not null)
            yield return new FileSystemAccessRule(user, FileSystemRights.FullControl, inheritance,
                PropagationFlags.None, AccessControlType.Allow);
        foreach (var sid in new[]
                 {
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
                 })
            yield return new FileSystemAccessRule(sid, FileSystemRights.FullControl, inheritance,
                PropagationFlags.None, AccessControlType.Allow);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not delete {path}: {ex.Message}");
        }
    }
}
