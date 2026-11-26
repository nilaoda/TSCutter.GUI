using System;
using System.IO;
using System.Linq;

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

    public static string DataDirectory => Path.Combine(DataRoot, DataDirectoryName);
    public static string OptOutFilePath => Path.Combine(DataDirectory, OptOutFileName);

    private static bool _portableUnavailable;

    /// <summary>Set when portable storage could not be created, for display in the UI.</summary>
    public static string? FallbackMessage { get; private set; }

    public static bool HasMarker => File.Exists(Path.Combine(InstallRoot, MarkerFileName));
    public static bool IsPortable => !_portableUnavailable && HasMarker;
    public static bool IsOptedOut => File.Exists(OptOutFilePath);

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

        if (!Directory.Exists(DataDirectory))
            return;
        WriteOptOut();
        RestoreFromPortableData();
    }

    private static void EnsurePortableData()
    {
        try
        {
            if (Directory.Exists(DataDirectory))
                return;
            Directory.CreateDirectory(DataDirectory);
            RestrictDirectory(DataDirectory);

            var configRoot = Path.Combine(ProfileRoot(Environment.SpecialFolder.ApplicationData), AppDirectoryName);
            CopyFileIfPresent(configRoot, DataDirectory, "config.json");
            CopyFileIfPresent(configRoot, DataDirectory, "remote-editing.json");
            CopyFileIfPresent(configRoot, DataDirectory, "saved-frames.json");

            var recovery = Path.Combine(ProfileRoot(Environment.SpecialFolder.LocalApplicationData),
                AppDirectoryName, "Recovery");
            if (Directory.Exists(recovery))
                CopyDirectory(recovery, Path.Combine(DataDirectory, "Recovery"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only install folder cannot hold portable data. Fall back to
            // the user profile so the app still starts with writable settings.
            _portableUnavailable = true;
            FallbackMessage = $"Portable data folder is unavailable: {ex.Message}. Using the user profile instead.";
            Console.Error.WriteLine(FallbackMessage);
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
        File.Copy(source, target, overwrite: false);
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
            File.Copy(file, target, overwrite: false);
            RestrictFile(target);
        }
    }

    /// <summary>Keeps portable data private on Unix; the profile folders already are.</summary>
    internal static void RestrictFile(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
            return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Console.Error.WriteLine($"Could not restrict {path}: {ex.Message}");
        }
    }

    internal static void RestrictDirectory(string path)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(path))
            return;
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Console.Error.WriteLine($"Could not restrict {path}: {ex.Message}");
        }
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
