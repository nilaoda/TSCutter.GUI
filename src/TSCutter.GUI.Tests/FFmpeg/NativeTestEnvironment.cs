using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Utils.Media;
using Xunit;

namespace TSCutter.GUI.Tests.FFmpeg;

/// <summary>集中解析原生测试的运行库、生成素材和可选真实素材。</summary>
internal static class NativeTestEnvironment
{
    private static readonly Lazy<string?> Runtime = new(FindRuntime);
    internal static string? RuntimeDirectory => Runtime.Value;

    internal static string FixtureDirectory(string group)
    {
        var legacyVariable = LegacyVariable(group);
        var legacy = legacyVariable is null ? null : Environment.GetEnvironmentVariable(legacyVariable);
        if (!string.IsNullOrWhiteSpace(legacy))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(legacy));
        var root = Environment.GetEnvironmentVariable("TSCUTTER_TEST_MEDIA_ROOT");
        return Path.Combine(string.IsNullOrWhiteSpace(root) ? Path.Combine(AppContext.BaseDirectory, "TestAssets")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(root)), group);
    }

    internal static string? MissingRequirement(string? group, string? sampleVariable)
    {
        if (sampleVariable is not null)
        {
            var sample = Environment.GetEnvironmentVariable(sampleVariable);
            if (string.IsNullOrWhiteSpace(sample) || (!File.Exists(sample) && !Directory.Exists(sample)))
                return $"可选真实素材未配置或不存在：{sampleVariable}。参见 src/TSCutter.GUI.Tests/README.md。";
        }
        if (group is not null)
        {
            var directory = FixtureDirectory(group);
            // 兼容以前由独立脚本生成、通过环境变量指定的目录。
            var legacy = LegacyVariable(group);
            var overridden = legacy is not null && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(legacy));
            if (!Directory.Exists(directory) || (!overridden && !File.Exists(Path.Combine(directory, "manifest.json"))))
                return $"缺少 {group} 合成素材。先运行 python tools/prepare-test-media.py --group {group}，再运行 dotnet test；"
                    + "无需配置素材环境变量。参见 src/TSCutter.GUI.Tests/README.md。";
        }
        if (RuntimeDirectory is null)
            return "未找到 FFmpeg 9 共享库。将当前系统和架构的完整运行库放到应用或测试输出目录，"
                + "也可设置 TSCUTTER_FFMPEG_ROOT。参见 src/TSCutter.GUI.Tests/README.md。";
        return null;
    }

    private static string? LegacyVariable(string group) => group switch
    {
        "remux" => "TSCUTTER_REMUX_TEST_SAMPLES",
        "subtitles" => "TSCUTTER_SUBTITLE_TEST_SAMPLES",
        _ => null
    };

    private static string? FindRuntime()
    {
        var fileName = FFmpegNativeBootstrapper.LibraryFileName("avcodec", 63);
        foreach (var directory in RuntimeDirectories())
        foreach (var candidate in new[] { directory, Path.Combine(directory, "lib") })
        {
            if (!File.Exists(Path.Combine(candidate, fileName)))
                continue;
            // 文件存在但损坏、ABI 或架构不匹配时由实际加载报错，不静默跳过。
            FFmpegNativeBootstrapper.Configure(new AppConfig { FFmpegRootPath = candidate });
            return candidate;
        }
        return null;
    }

    private static IEnumerable<string> RuntimeDirectories()
    {
        yield return AppContext.BaseDirectory;
        foreach (var name in new[] { "TSCUTTER_FFMPEG_ROOT", "FFMPEG_ROOT" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
                yield return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));
        }
        var assembly = typeof(NativeTestEnvironment).Assembly;
        var configuration = assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
        var framework = new FrameworkName(assembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName);
        var target = "net" + framework.Version.ToString(2);
        var foundProject = false;
        for (var parent = new DirectoryInfo(AppContext.BaseDirectory); parent is not null && !foundProject; parent = parent.Parent)
        {
            foreach (var project in new[] { Path.Combine(parent.FullName, "TSCutter.GUI"),
                         Path.Combine(parent.FullName, "src", "TSCutter.GUI") })
            {
                if (!File.Exists(Path.Combine(project, "TSCutter.GUI.csproj")))
                    continue;
                var output = Path.Combine(project, "bin", configuration, target);
                yield return output;
                yield return Path.Combine(output, RuntimeInformation.RuntimeIdentifier);
                foundProject = true;
                break;
            }
        }
        if (OperatingSystem.IsMacOS())
        {
            foreach (var directory in new[] { "/opt/homebrew/opt/ffmpeg@9/lib", "/usr/local/opt/ffmpeg@9/lib",
                         "/opt/homebrew/opt/ffmpeg/lib", "/usr/local/opt/ffmpeg/lib", "/opt/homebrew/lib", "/usr/local/lib" })
                yield return directory;
        }
        else if (OperatingSystem.IsLinux())
        {
            foreach (var directory in new[] { "/usr/local/lib", "/usr/lib/x86_64-linux-gnu", "/usr/lib" })
                yield return directory;
        }
    }
}

public class NativeMediaFactAttribute : FactAttribute
{
    public NativeMediaFactAttribute(string? group = null, string? sampleVariable = null)
        => Skip = NativeTestEnvironment.MissingRequirement(group, sampleVariable);
}

public sealed class NativeRuntimeFactAttribute : NativeMediaFactAttribute
{
    public NativeRuntimeFactAttribute(string? requiredVariable = null) : base(sampleVariable: requiredVariable) { }
}
