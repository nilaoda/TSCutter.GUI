using System;
using System.IO;
using System.Linq;

namespace TSCutter.GUI.Services.Application;

/// <summary>
/// Resolves persistent application data locations. When a "TSCutter.GUI.portable"
/// marker file sits next to the executable, every data path is redirected into a
/// "data" folder inside the install directory; otherwise the normal user profile
/// folders are used. Removing the marker restores profile-based storage.
/// </summary>
public static class AppDataPaths
{
    public const string MarkerFileName = "TSCutter.GUI.portable";
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

    private static bool _portableUnavailable;

    public static bool IsPortable =>
        !_portableUnavailable && File.Exists(Path.Combine(InstallRoot, MarkerFileName));

    public static string DataDirectory => Path.Combine(InstallRoot, DataDirectoryName);

    /// <summary>
    /// Returns an app-data path for the given profile root. In portable mode both
    /// application-data roots map to the same folder beside the executable.
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
    /// Seeds the portable data folder from the user profile on the first portable
    /// launch. Transient updater state (the lock and helper result files) is left
    /// behind deliberately: a stale lock from another install would block updates.
    /// </summary>
    public static void EnsurePortableData()
    {
        if (!File.Exists(Path.Combine(InstallRoot, MarkerFileName)) || _portableUnavailable)
            return;
        try
        {
            if (Directory.Exists(DataDirectory))
                return;
            Directory.CreateDirectory(DataDirectory);

            var configRoot = Path.Combine(ProfileRoot(Environment.SpecialFolder.ApplicationData), AppDirectoryName);
            CopyFileIfPresent(configRoot, "config.json");
            CopyFileIfPresent(configRoot, "remote-editing.json");
            CopyFileIfPresent(configRoot, "saved-frames.json");

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
            Console.Error.WriteLine(
                $"Portable data folder is unavailable: {ex.Message}. Using the user profile instead.");
        }
    }

    private static void CopyFileIfPresent(string sourceDirectory, string name)
    {
        var source = Path.Combine(sourceDirectory, name);
        if (File.Exists(source))
            File.Copy(source, Path.Combine(DataDirectory, name), overwrite: false);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }
}
