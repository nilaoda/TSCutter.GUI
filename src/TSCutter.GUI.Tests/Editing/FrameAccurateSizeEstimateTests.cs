using Avalonia.Headless;
using Splat;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Tests.UI;
using TSCutter.GUI.Utils;
using TSCutter.GUI.Utils.Application;
using Xunit;

namespace TSCutter.GUI.Tests.Editing;

/// <summary>帧精确片段的大小显示：单节目 TS 导出前显示带“~”的预估值，无法预估时导出后确定，导出后显示实际大小。</summary>
[Collection("Avalonia UI")]
public sealed class FrameAccurateSizeEstimateTests
{
    [Fact]
    public async Task ClipAndQueueShowApproximateSizeOnlyWhenEstimable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tscutter-frame-size-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source.ts");
            var output = Path.Combine(directory, "output.ts");
            File.WriteAllBytes(source, new byte[4000]);
            using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
            await session.Dispatch<bool>(() =>
            {
                using var resolver = new ModernDependencyResolver();
                using var scope = resolver.WithResolver();
                var locale = new LocalizationService();
                locale.SwitchLanguage("zh-CN");
                Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
                var strings = LocalizationManager.Instance;
                var range = CommonUtil.FormatFileSize(1500);

                PickedClip Clip(bool frameAccurate, bool estimable) => new()
                {
                    InFileInfo = new FileInfo(source), StartPosition = 500, EndPosition = 2000,
                    UsesFrameAccurateExport = frameAccurate, CanEstimateFrameAccurateSize = estimable
                };

                // 关键帧模式按字节范围显示，不带“~”。
                Assert.Equal("大小：" + range, Clip(false, true).EstimatedSizeStr);
                // 单节目 TS 的帧精确片段显示预估值。
                var clip = Clip(true, true);
                Assert.Equal("大小：~" + range, clip.EstimatedSizeStr);
                // 多节目 TS 或需要重新封装的来源无法预估。
                Assert.Equal("大小：" + strings.String_PreviewTrack_SizeAfterExport, Clip(true, false).EstimatedSizeStr);

                // 导出后显示实际大小；合并导出显示“已合并导出”。
                File.WriteAllBytes(output, new byte[1234]);
                clip.OutputFileInfo = new FileInfo(output);
                Assert.Equal("大小：" + CommonUtil.FormatFileSize(1234), clip.EstimatedSizeStr);
                clip.MergedOutput = new MergedClipOutput("group", 2);
                Assert.Equal(strings.String_Clips_Merged, clip.EstimatedSizeStr);
                // 修改边界后回到预估值。
                clip.StartPosition = 1000;
                Assert.Equal("大小：~" + CommonUtil.FormatFileSize(1000), clip.EstimatedSizeStr);

                var settings = new FrameAccurateExportSettings { Container = "ts", VideoIndex = 0, StreamIndices = [0] };
                Assert.Equal("大小：~" + range, new ExportQueueItem
                {
                    FrameAccurate = settings, EstimatedBytes = 1500, SizeIsApproximate = true
                }.EstimatedSizeStr);
                Assert.Equal("大小：" + strings.String_PreviewTrack_SizeAfterExport, new ExportQueueItem
                {
                    FrameAccurate = settings
                }.EstimatedSizeStr);

                // 切换到英文后同样使用预估格式。
                locale.SwitchLanguage("en-US");
                Assert.Equal("Size: ~" + CommonUtil.FormatFileSize(1000), clip.EstimatedSizeStr);
                return Task.FromResult(true);
            }, CancellationToken.None);
        }
        finally { Directory.Delete(directory, true); }
    }
}
