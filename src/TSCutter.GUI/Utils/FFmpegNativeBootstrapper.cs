using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using TSCutter.GUI.Models;

namespace TSCutter.GUI.Utils;

public static class FFmpegNativeBootstrapper
{
    private sealed record LibrarySpec(string ComponentName, int MajorVersion, bool Required = true)
    {
        public string CanonicalFileName => $"lib{ComponentName}.{MajorVersion}.dylib";
    }

    private sealed record ProbeResult(string RootPath, string Source, IReadOnlyDictionary<string, string> LibraryPaths);

    private static readonly LibrarySpec[] LibrarySpecs =
    [
        new("avutil", 59),
        new("swresample", 5),
        new("swscale", 8),
        new("avcodec", 61),
        new("avformat", 61),
        new("avfilter", 10),
        new("avdevice", 61),
        new("postproc", 58, Required: false),
    ];

    private static readonly object SyncRoot = new();
    private static bool _initialized;
    private static string _diagnosticSummary = "FFmpeg bootstrap has not run yet.";
    private static ProbeResult? _probeResult;

    public static void Initialize(AppConfig? config = null)
    {
        if (!OperatingSystem.IsMacOS())
        {
            _diagnosticSummary = "FFmpeg bootstrap skipped: current OS is not macOS.";
            return;
        }

        lock (SyncRoot)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            var probeNotes = new List<string>();
            foreach (var candidate in BuildCandidateDirectories(config))
            {
                if (TryProbeDirectory(candidate.Path, candidate.Source, out var probeResult, out var note))
                {
                    _probeResult = probeResult;
                    _diagnosticSummary = $"FFmpeg dylibs resolved from '{probeResult.RootPath}' ({probeResult.Source}).";
                    Console.WriteLine(_diagnosticSummary);
                    if (TryCreateAppLocalSymlinks(probeResult, out var shimPath))
                    {
                        _diagnosticSummary += $" App-local shims ready at '{shimPath}'.";
                        PreloadLibraries(probeResult);
                        return;
                    }

                    _diagnosticSummary += " App-local shims could not be created.";
                    Console.WriteLine(_diagnosticSummary);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(note))
                {
                    probeNotes.Add(note);
                }
            }

            _diagnosticSummary =
                "Unable to find a compatible FFmpeg 7 runtime on macOS. " +
                "Install 'ffmpeg@7' with Homebrew, or set 'FFmpegRootPath' in config.json to the FFmpeg 7 root/lib directory.";
            if (probeNotes.Count > 0)
            {
                _diagnosticSummary += Environment.NewLine + string.Join(Environment.NewLine, probeNotes);
            }
            Console.WriteLine(_diagnosticSummary);
        }
    }

    public static string BuildLoadFailureMessage(Exception exception)
    {
        var dllError = EnumerateExceptions(exception).OfType<DllNotFoundException>().FirstOrDefault();
        if (dllError is null)
        {
            return exception.Message;
        }

        return $"{dllError.Message}{Environment.NewLine}{Environment.NewLine}{_diagnosticSummary}";
    }

    public static string GetDiagnosticSummary() => _diagnosticSummary;

    private static IEnumerable<Exception> EnumerateExceptions(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            yield return current;
            if (current.InnerException is null)
            {
                yield break;
            }
        }
    }

    private static IEnumerable<(string Path, string Source)> BuildCandidateDirectories(AppConfig? config)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in new[]
                 {
                     (config?.FFmpegRootPath, "config"),
                     (Environment.GetEnvironmentVariable("TSCUTTER_FFMPEG_ROOT"), "env:TSCUTTER_FFMPEG_ROOT"),
                     (Environment.GetEnvironmentVariable("FFMPEG_ROOT"), "env:FFMPEG_ROOT"),
                     (TryGetBrewPrefix("ffmpeg@7"), "brew:ffmpeg@7"),
                     (TryGetBrewPrefix("ffmpeg"), "brew:ffmpeg"),
                     ("/opt/homebrew/opt/ffmpeg@7/lib", "standard:/opt/homebrew/opt/ffmpeg@7/lib"),
                     ("/usr/local/opt/ffmpeg@7/lib", "standard:/usr/local/opt/ffmpeg@7/lib"),
                     ("/opt/homebrew/opt/ffmpeg/lib", "standard:/opt/homebrew/opt/ffmpeg/lib"),
                     ("/usr/local/opt/ffmpeg/lib", "standard:/usr/local/opt/ffmpeg/lib"),
                     ("/opt/homebrew/lib", "standard:/opt/homebrew/lib"),
                     ("/usr/local/lib", "standard:/usr/local/lib"),
                 })
        {
            foreach (var normalized in NormalizeCandidateDirectory(candidate.Item1))
            {
                if (seen.Add(normalized))
                {
                    yield return (normalized, candidate.Item2);
                }
            }
        }
    }

    private static IEnumerable<string> NormalizeCandidateDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        if (!Path.IsPathRooted(expanded))
        {
            yield break;
        }

        if (Directory.Exists(expanded))
        {
            yield return expanded;
        }

        var libPath = Path.Combine(expanded, "lib");
        if (!string.Equals(expanded, libPath, StringComparison.Ordinal) && Directory.Exists(libPath))
        {
            yield return libPath;
        }
    }

    private static string? TryGetBrewPrefix(string formula)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "brew",
                ArgumentList = { "--prefix", formula },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(2000);
            return process.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryProbeDirectory(
        string directory,
        string source,
        out ProbeResult result,
        out string note)
    {
        result = default!;
        note = string.Empty;

        if (!Directory.Exists(directory))
        {
            return false;
        }

        var matched = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in LibrarySpecs)
        {
            var exactPath = Path.Combine(directory, spec.CanonicalFileName);
            if (File.Exists(exactPath))
            {
                matched[spec.CanonicalFileName] = exactPath;
                continue;
            }

            if (spec.Required)
            {
                note = $"Checked '{directory}' ({source}) but missing '{spec.CanonicalFileName}'. {DescribeDirectory(directory)}";
                return false;
            }
        }

        result = new ProbeResult(directory, source, matched);
        return true;
    }

    private static string DescribeDirectory(string directory)
    {
        try
        {
            var nearby = Directory.EnumerateFiles(directory, "*.dylib")
                .Select(Path.GetFileName)
                .Where(name => name is not null && (name.StartsWith("libav", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("libsw", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("libpostproc", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToArray();
            return nearby.Length == 0 ? "No FFmpeg dylibs found there." : $"Found: {string.Join(", ", nearby)}";
        }
        catch (Exception ex)
        {
            return $"Unable to inspect directory: {ex.Message}";
        }
    }

    private static void PreloadLibraries(ProbeResult probeResult)
    {
        foreach (var spec in LibrarySpecs)
        {
            if (!probeResult.LibraryPaths.TryGetValue(spec.CanonicalFileName, out var path))
            {
                continue;
            }

            if (NativeLibrary.TryLoad(path, out _))
            {
                Console.WriteLine($"Preloaded FFmpeg dylib: {path}");
            }
        }
    }

    private static bool TryCreateAppLocalSymlinks(ProbeResult probeResult, out string shimDirectory)
    {
        shimDirectory = Path.Combine(AppContext.BaseDirectory, "runtimes", "osx", "native");
        try
        {
            Directory.CreateDirectory(shimDirectory);
            foreach (var pair in probeResult.LibraryPaths)
            {
                var targetPath = Path.Combine(shimDirectory, pair.Key);
                if (Path.Exists(targetPath))
                {
                    FileSystemInfo? linkTarget = null;
                    try
                    {
                        linkTarget = File.ResolveLinkTarget(targetPath, returnFinalTarget: false);
                    }
                    catch
                    {
                    }

                    if (linkTarget is not null)
                    {
                        var rawTarget = linkTarget.FullName;
                        var existingTarget = Path.GetFullPath(
                            Path.IsPathRooted(rawTarget) ? rawTarget : Path.Combine(shimDirectory, rawTarget));
                        var desiredTarget = Path.GetFullPath(pair.Value);
                        if (string.Equals(existingTarget, desiredTarget, StringComparison.Ordinal))
                        {
                            continue;
                        }
                    }

                    File.Delete(targetPath);
                }

                File.CreateSymbolicLink(targetPath, pair.Value);
                Console.WriteLine($"Created FFmpeg shim: {targetPath} -> {pair.Value}");
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to create FFmpeg shims in '{shimDirectory}': {ex.Message}");
            return false;
        }
    }
}
