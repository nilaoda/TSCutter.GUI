using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Utils.Files;
using Xunit;

namespace TSCutter.GUI.Tests.Files;

public sealed class MediaFileUtilTests
{
    [Fact]
    public void RootLevelOutputDoesNotResolveTheVolumeRootAsALink()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var path = Path.Combine(root, "tscutter-" + Guid.NewGuid().ToString("N") + ".mp4");
        Assert.Equal(Path.GetFullPath(path), MediaFileUtil.CanonicalPath(path));
        Assert.Equal(Path.GetFullPath(root), MediaFileUtil.CanonicalPath(root));
        Assert.True(MediaFileUtil.IsWithinDirectory(path, root));
    }

    [Fact]
    public void ExportValidationAcceptsDifferentRootLevelPathsAndStillProtectsTheSource()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var source = Path.Combine(root, "tscutter-source-" + Guid.NewGuid().ToString("N") + ".mp4");
        var output = Path.Combine(root, "tscutter-output-" + Guid.NewGuid().ToString("N") + ".mp4");
        var settings = new RemuxExportSettings { Container = "mp4", ReferenceVideoIndex = 0, StreamIndices = [0] };
        var segment = new RemuxExportSegment(source, 0, 1, settings);
        RemuxExportService.Validate(new RemuxExportRequest(output, [segment]));
        Assert.Equal(RemuxExportError.SameFile, Assert.Throws<RemuxExportException>(() =>
            RemuxExportService.Validate(new RemuxExportRequest(source, [segment]))).Code);
    }
}
