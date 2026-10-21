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
