using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using TSCutter.GUI.Models;

namespace TSCutter.GUI.Utils;

public static class FFmpegNativeBootstrapper
{
    private static readonly (string Name, int Major)[] Libraries =
    [
        ("avutil", 61), ("swresample", 7), ("swscale", 10),
        ("avcodec", 63), ("avformat", 63), ("avfilter", 12), ("avdevice", 63)
    ];
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, IntPtr> Handles = new(StringComparer.Ordinal);
    private static bool initialized;
    private static string diagnosticSummary = "FFmpeg bootstrap has not run yet.";

    public static void Initialize(AppConfig? config = null)
    {
        lock (SyncRoot)
        {
            if (initialized) return;
            var notes = new List<string>();
            foreach (var directory in CandidateDirectories(config))
            {
                if (!Directory.Exists(directory)) continue;
                var missing = Libraries.Where(l => !File.Exists(Path.Combine(directory, LibraryFileName(l.Name, l.Major))))
                    .Select(l => LibraryFileName(l.Name, l.Major)).ToArray();
                if (missing.Length != 0)
                {
                    notes.Add($"Checked '{directory}': missing {string.Join(", ", missing)}.");
                    continue;
                }
                var loaded = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
                try
                {
                    foreach (var library in Libraries)
                    {
                        var handle = NativeLibrary.Load(Path.Combine(directory, LibraryFileName(library.Name, library.Major)));
                        loaded.Add(library.Name, handle);
                        ValidateVersion(handle, library.Name, library.Major);
                    }
                    foreach (var library in loaded) Handles.Add(library.Key, library.Value);
                    initialized = true;
                    diagnosticSummary = $"FFmpeg 9 runtime resolved from '{directory}'.";
                    Console.WriteLine(diagnosticSummary);
                    return;
                }
                catch (Exception exception)
                {
                    // 某个依赖加载失败时，按相反顺序释放本次加载的句柄，允许继续探测。
                    foreach (var handle in loaded.Values.Reverse()) NativeLibrary.Free(handle);
                    notes.Add($"Checked '{directory}': {exception.Message}");
                }
            }
            diagnosticSummary = "Unable to find a compatible FFmpeg 9 runtime. "
                + "Download the runtime from FFmpegSharedLibraries release 20261004 and place it beside the application, "
                + "or set FFmpegRootPath / TSCUTTER_FFMPEG_ROOT to its directory."
                + Environment.NewLine + string.Join(Environment.NewLine, notes);
            Console.WriteLine(diagnosticSummary);
        }
    }

    internal static IntPtr ResolveLibrary(string component)
    {
        lock (SyncRoot)
        {
            if (!initialized) Initialize();
            return Handles.TryGetValue(component, out var handle) ? handle
                : throw new DllNotFoundException(diagnosticSummary);
        }
    }

    internal static string LibraryFileName(string component, int major) => OperatingSystem.IsWindows()
        ? $"{component}-{major}.dll" : OperatingSystem.IsMacOS()
        ? $"lib{component}.{major}.dylib" : $"lib{component}.so.{major}";

    private static unsafe void ValidateVersion(IntPtr handle, string component, int expectedMajor)
    {
        // 文件名不足以保证 ABI 一致：在读取任何原生结构之前验证真正的主版本。
        var version = (delegate* unmanaged[Cdecl]<uint>)NativeLibrary.GetExport(handle, component + "_version");
        var actualMajor = version() >> 16;
        if (actualMajor != expectedMajor)
            throw new InvalidOperationException($"{component}: expected ABI {expectedMajor}, got {actualMajor}.");
    }

    private static IEnumerable<string> CandidateDirectories(AppConfig? config)
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var candidates = new List<string?>
        {
            AppContext.BaseDirectory,
            config?.FFmpegRootPath,
            Environment.GetEnvironmentVariable("TSCUTTER_FFMPEG_ROOT"),
            Environment.GetEnvironmentVariable("FFMPEG_ROOT")
        };
        if (OperatingSystem.IsMacOS())
            candidates.AddRange(["/opt/homebrew/opt/ffmpeg@9/lib", "/usr/local/opt/ffmpeg@9/lib",
                "/opt/homebrew/opt/ffmpeg/lib", "/usr/local/opt/ffmpeg/lib", "/opt/homebrew/lib", "/usr/local/lib"]);
        else if (OperatingSystem.IsLinux())
            candidates.AddRange(["/usr/local/lib", "/usr/lib/x86_64-linux-gnu", "/usr/lib"]);
        foreach (var value in candidates)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var path = Environment.ExpandEnvironmentVariables(value.Trim());
            if (!Path.IsPathRooted(path)) continue;
            path = Path.GetFullPath(path);
            if (seen.Add(path)) yield return path;
            var lib = Path.Combine(path, "lib");
            if (seen.Add(lib)) yield return lib;
        }
    }

    public static string BuildLoadFailureMessage(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is DllNotFoundException or EntryPointNotFoundException)
                return $"{current.Message}{Environment.NewLine}{Environment.NewLine}{diagnosticSummary}";
        return exception.Message;
    }
    public static string GetDiagnosticSummary() => diagnosticSummary;
}
