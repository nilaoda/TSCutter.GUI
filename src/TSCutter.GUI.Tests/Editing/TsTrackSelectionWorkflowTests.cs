using Avalonia.Headless;
using FFmpeg.AutoGen.Abstractions;
using Splat;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Tests.UI;
using TSCutter.GUI.Utils.Media;
using TSCutter.GUI.ViewModels.Editing;
using Xunit;

namespace TSCutter.GUI.Tests.Editing;

[Collection("Avalonia UI")]
public sealed class TsTrackSelectionWorkflowTests
{
    /// <summary>
    /// 导出窗口按轨道选择删去音轨：关键帧剪切与智能剪辑的输出都没有这条音轨；
    /// 导出记录的类型与附加标签如实反映改动：没有改动时为字节复制，删去轨道或改写音频时间戳时不再称为字节复制。
    /// </summary>
    [NativeMediaFact("frameaccurate")]
    public async Task ExcludedTrackIsRemovedAndLabelled()
    {
        var source = Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), "h264.ts");
        var directory = Path.Combine(Path.GetTempPath(), $"tscutter-ts-tracks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            int videoIndex, videoPid, audioPid;
            List<int> indices;
            long startPts, endPts;
            using (var input = InterruptibleInputFormatContext.Open(source, static () => false))
            {
                input.LoadStreamInfo();
                var video = input.GetVideoStream();
                (videoIndex, videoPid) = (video.Index, video.Id);
                var audio = input.Streams.First(stream => stream.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_AUDIO);
                audioPid = audio.Id;
                // 智能剪辑的轨道已按选择去掉音轨，与主窗口建立设置时一致。
                indices = [videoIndex];
                var pts = input.ReadPackets().Where(packet => packet.StreamIndex == videoIndex)
                    .Select(packet => packet.Pts).ToList();
                pts.Sort();
                (startPts, endPts) = (pts[pts.Count / 4 + 3], pts[pts.Count * 3 / 4]);
            }
            var length = new FileInfo(source).Length / 188 * 188;
            var plain = Path.Combine(directory, "plain.ts");
            var removed = Path.Combine(directory, "removed.ts");
            var delayed = Path.Combine(directory, "delayed.ts");
            var smart = Path.Combine(directory, "smart.ts");
            List<ExportTaskOutcome> outcomes = [];
            await RunAsync(async () =>
            {
                var output = new OutputWindowViewModel();
                output.SetBatchTasks(
                [
                    new(source, plain, 0, length),
                    new(source, removed, 0, length, ExcludedPids: [audioPid]),
                    new(source, delayed, 0, length, AudioDelayMs: 120),
                    new(source, smart, 0, -1, FrameAccurate: new FrameAccurateExportSettings
                    {
                        Container = "ts", VideoIndex = videoIndex, StreamIndices = indices
                    }, StartPts: startPts, EndPts: endPts, ExcludedPids: [audioPid])
                ]);
                output.ConfirmProgramTableWarningAsync = (_, _) => Task.FromResult(TsProgramTableExportChoice.Original);
                await output.OutputCommand.ExecuteAsync(null);
                Assert.Null(output.Exception);
                Assert.Equal([0, 1, 2, 3], output.CompletedTaskIndices);
                outcomes = output.Outcomes;
            });

            Assert.Equal((ExportKind.TsCopy, ExportModifiers.None), Describe(outcomes, plain));
            Assert.Equal((ExportKind.TsPacket, ExportModifiers.TracksSelected), Describe(outcomes, removed));
            Assert.Equal((ExportKind.TsPacket, ExportModifiers.AudioDelay), Describe(outcomes, delayed));
            Assert.Equal(120, outcomes.Single(outcome => outcome.OutputPath == delayed).AudioDelayMs);
            Assert.Equal((ExportKind.FrameAccurate, ExportModifiers.TracksSelected), Describe(outcomes, smart));

            // 删包与其他包不变由重组层的单元测试覆盖，这里只确认两条导出路径的输出都没有这条音轨。
            foreach (var path in new[] { removed, smart })
            {
                using var result = InterruptibleInputFormatContext.Open(path, static () => false);
                result.LoadStreamInfo();
                Assert.DoesNotContain(result.Streams, stream => stream.Id == audioPid);
                Assert.Contains(result.Streams, stream => stream.Id == videoPid);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static (ExportKind?, ExportModifiers) Describe(IEnumerable<ExportTaskOutcome> outcomes, string path)
    {
        var outcome = outcomes.Single(item => item.OutputPath == path);
        Assert.True(outcome.Success);
        return (outcome.Kind, outcome.Modifiers);
    }

    private static async Task RunAsync(Func<Task> action)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var locale = new LocalizationService();
            locale.SwitchLanguage("zh-CN");
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
            await action();
            return true;
        }, CancellationToken.None);
    }
}
