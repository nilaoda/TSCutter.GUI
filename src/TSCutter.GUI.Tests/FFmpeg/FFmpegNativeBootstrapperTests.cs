using System.Runtime.InteropServices;
using Xunit;
using TSCutter.GUI.Utils.Media;

namespace TSCutter.GUI.Tests.FFmpeg;

public class FFmpegNativeBootstrapperTests
{
    [Fact]
    public void MissingRuntime_ReportsExactFilesAndRecovery()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tscutter-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var exception = Assert.Throws<DllNotFoundException>(() => FFmpegNativeBootstrapper.LoadDirectory(directory));
            Assert.Contains(FFmpegNativeBootstrapper.LibraryFileName("avcodec", 63), exception.Message);
            Assert.Contains("complete matching runtime package", exception.Message);
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [NativeRuntimeFact]
    public void UnsupportedSubtitleLibraryReportsItsPathAndMissingEntryPoint()
    {
        var root = NativeTestEnvironment.RuntimeDirectory!;
        var libraries = FFmpegNativeBootstrapper.LoadDirectory(root);
        try
        {
            var path = Path.Combine(root, FFmpegNativeBootstrapper.LibraryFileName("avfilter", 12));
            // 使用没有字幕接口的原生库，验证缺失接口不会被误报为缺少单独的 DLL。
            var error = Assert.Throws<DllNotFoundException>(() =>
                FFmpegNativeBootstrapper.ValidateSubtitleSupport(libraries["avutil"], path));
            Assert.Contains(path, error.Message);
            Assert.Contains("ass_library_version", error.Message);
            Assert.Contains("complete matching subtitle-enabled", error.Message);
        }
        finally
        {
            foreach (var handle in libraries.Values.Reverse()) NativeLibrary.Free(handle);
        }
    }

    [Fact]
    public void LoadFailure_UnwrapsArchitectureError()
    {
        var exception = new TypeInitializationException("NativeMethods",
            new BadImageFormatException("Wrong CPU architecture: avcodec"));
        Assert.Contains("Wrong CPU architecture: avcodec", FFmpegNativeBootstrapper.BuildLoadFailureMessage(exception));
    }

    [Fact]
    public void LoadFailure_DoesNotDuplicateDiagnosticSummary()
    {
        var summary = FFmpegNativeBootstrapper.GetDiagnosticSummary();
        Assert.Equal(summary, FFmpegNativeBootstrapper.BuildLoadFailureMessage(new DllNotFoundException(summary)));
    }
}
