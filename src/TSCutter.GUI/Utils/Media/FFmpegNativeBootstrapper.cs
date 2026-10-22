using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using TSCutter.GUI.Models.Application;

namespace TSCutter.GUI.Utils.Media;

public static class FFmpegNativeBootstrapper
{
    private static readonly (string Name, int Major)[] Libraries =
    [
        ("avutil", 61), ("swresample", 7), ("swscale", 10),
        ("avcodec", 63), ("avformat", 63), ("avfilter", 12), ("avdevice", 63)
    ];
    private static readonly string[] SubtitleExports =
    [
        "ass_library_init", "ass_library_done", "ass_renderer_init", "ass_renderer_done",
        "ass_set_frame_size", "ass_set_storage_size", "ass_set_pixel_aspect", "ass_set_fonts", "ass_add_font",
        "ass_new_track", "ass_free_track", "ass_process_codec_private", "ass_process_chunk",
        "ass_render_frame", "ass_flush_events", "ass_track_set_feature"
    ];
    private static string? runtimeDirectory;
    private static string? configuredRootPath;
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, IntPtr> Handles = new(StringComparer.Ordinal);
    private static bool initialized;
    private static bool subtitlesValidated;
    private static string diagnosticSummary = "FFmpeg bootstrap has not run yet.";

    // 启动时仅保存搜索路径，首次调用原生接口时再加载并校验运行库，避免阻塞首屏。
    public static void Configure(AppConfig config)
    {
        lock (SyncRoot)
            configuredRootPath = config.FFmpegRootPath;
    }

    public static void Initialize(AppConfig? config = null)
    {
        lock (SyncRoot)
        {
            if (initialized) return;
            var notes = new List<string>();
            foreach (var directory in CandidateDirectories(config?.FFmpegRootPath ?? configuredRootPath))
            {
                if (!Directory.Exists(directory)) continue;
                try
                {
                    var loaded = LoadDirectory(directory);
                    foreach (var library in loaded) Handles.Add(library.Key, library.Value);
                    runtimeDirectory = directory;
                    initialized = true;
                    diagnosticSummary = $"FFmpeg 9 runtime resolved from '{directory}'.";
                    Console.WriteLine(diagnosticSummary);
                    return;
                }
                catch (Exception exception)
                {
                    notes.Add($"Checked '{directory}': {exception.Message}");
                }
            }
            diagnosticSummary = "Unable to find a compatible FFmpeg 9 runtime. "
                + "Restore the complete matching FFmpegSharedLibraries runtime beside the application, "
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
            if (initialized && component == "ass")
            {
                var filter = Handles["avfilter"];
                if (!subtitlesValidated)
                {
                    ValidateSubtitleSupport(filter, Path.Combine(runtimeDirectory!, LibraryFileName("avfilter", 12)));
                    subtitlesValidated = true;
                }
                return filter;
            }
            if (Handles.TryGetValue(component, out var handle)) return handle;
            throw new DllNotFoundException(diagnosticSummary);
        }
    }

    internal static string LibraryFileName(string component, int major) => OperatingSystem.IsWindows()
        ? $"{component}-{major}.dll" : OperatingSystem.IsMacOS()
        ? $"lib{component}.{major}.dylib" : $"lib{component}.so.{major}";

    internal static Dictionary<string, IntPtr> LoadDirectory(string directory)
    {
        var missing = Libraries.Where(l => !File.Exists(Path.Combine(directory, LibraryFileName(l.Name, l.Major))))
            .Select(l => LibraryFileName(l.Name, l.Major)).ToArray();
        if (missing.Length != 0)
            throw new DllNotFoundException($"Missing runtime libraries: {string.Join(", ", missing)}. Restore the complete matching runtime package.");

        var loaded = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
        var currentFile = "";
        try
        {
            foreach (var library in Libraries)
            {
                currentFile = Path.Combine(directory, LibraryFileName(library.Name, library.Major));
                var handle = NativeLibrary.Load(currentFile);
                loaded.Add(library.Name, handle);
                ValidateVersion(handle, library.Name, library.Major);
            }
            return loaded;
        }
        catch (Exception exception)
        {
            foreach (var handle in loaded.Values.Reverse()) NativeLibrary.Free(handle);
            var detail = exception is BadImageFormatException
                ? "The file is damaged or targets a different operating system or CPU architecture. " + exception.Message
                : exception.Message;
            throw new DllNotFoundException($"Unable to load '{currentFile}': {detail} "
                + "Restore the complete matching runtime package; avoid mixing libraries from different builds.", exception);
        }
    }

    // 字幕接口静态合入 avfilter。按需验证，普通预览无需依赖字幕接口是否存在。
    internal static void ValidateSubtitleSupport(IntPtr handle, string libraryPath)
    {
        try
        {
            ValidateVersion(handle, "ass", 0);
            foreach (var symbol in SubtitleExports) RequireExport(handle, symbol);
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or InvalidOperationException)
        {
            throw new DllNotFoundException(
                $"Subtitle rendering is unavailable in '{libraryPath}': {exception.Message} "
                + "This avfilter must export the embedded libass API (0.17.4 or later). "
                + "Restore the complete matching subtitle-enabled FFmpegSharedLibraries runtime package.", exception);
        }
    }

    private static IntPtr RequireExport(IntPtr handle, string symbol) =>
        NativeLibrary.TryGetExport(handle, symbol, out var address) ? address
            : throw new EntryPointNotFoundException($"Missing required entry point '{symbol}'. The runtime is incompatible or incomplete.");

    private static unsafe void ValidateVersion(IntPtr handle, string component, int expectedMajor)
    {
        // 文件名不足以保证 ABI 一致：在读取任何原生结构之前验证真正的版本。
        var symbol = component == "ass" ? "ass_library_version" : component + "_version";
        var version = (delegate* unmanaged[Cdecl]<uint>)RequireExport(handle, symbol);
        var actual = version();
        if (component == "ass")
        {
            if (actual < 0x01704000 || actual >= 0x10000000)
                throw new InvalidOperationException($"libass: expected API 0.17.4 or later within major version 0, got 0x{actual:X8}.");
        }
        else if (actual >> 16 != expectedMajor)
            throw new InvalidOperationException($"{component}: expected ABI {expectedMajor}, got {actual >> 16}.");
    }

    private static IEnumerable<string> CandidateDirectories(string? rootPath)
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var candidates = new List<string?>
        {
            AppContext.BaseDirectory,
            rootPath,
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
            if (current is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                return current.Message.Contains(diagnosticSummary, StringComparison.Ordinal) ? current.Message
                    : $"{current.Message}{Environment.NewLine}{Environment.NewLine}{diagnosticSummary}";
        return exception.Message;
    }
    public static string GetDiagnosticSummary() => diagnosticSummary;
}
