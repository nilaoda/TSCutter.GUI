using Avalonia.Headless;
using FFmpeg.AutoGen.Abstractions;
using Splat;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.Media;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Tests.UI;
using TSCutter.GUI.Utils.Media;
using TSCutter.GUI.ViewModels.Editing;
using Xunit;

namespace TSCutter.GUI.Tests.Editing;

[Collection("Avalonia UI")]
public sealed class DolbyEExportWorkflowTests
{
    /// <summary>
    /// 带转码计划时，关键帧剪切与智能剪辑的输出都必须经过重组输出流（否则导出报错）；
    /// 计划中的轨道不在节目表里时原样写出，且输出目录中不留下其他文件。
    /// </summary>
    [NativeMediaFact("frameaccurate")]
    public async Task ExportsWithPlanGoThroughRemuxStreamForEveryTsPath()
    {
        var source = Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), "h264.ts");
        var directory = Path.Combine(Path.GetTempPath(), $"tscutter-dolbye-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            int videoIndex;
            List<int> indices;
            long startPts, endPts;
            using (var input = InterruptibleInputFormatContext.Open(source, static () => false))
            {
                input.LoadStreamInfo();
                videoIndex = input.GetVideoStream().Index;
                indices = input.Streams.Where(stream => stream.Codecpar?.CodecType is AVMediaType.AVMEDIA_TYPE_VIDEO
                    or AVMediaType.AVMEDIA_TYPE_AUDIO).Select(stream => stream.Index).ToList();
                var pts = input.ReadPackets().Where(packet => packet.StreamIndex == videoIndex)
                    .Select(packet => packet.Pts).ToList();
                pts.Sort();
                (startPts, endPts) = (pts[pts.Count / 4 + 3], pts[pts.Count * 3 / 4]);
            }
            var plan = new AudioRebuildPlan
            {
                Tracks =
                [
                    new DolbyETrackSettings
                    {
                        Pid = 0x1FFE, ProgramConfig = 0, Transcode = true,
                        Outputs = [new DolbyEOutput { Program = 1, Codec = AudioOutputCodec.Ac3, BitrateKbps = 448 }]
                    }
                ],
                // 多轨合并同样经过重组流；来源不在节目表里时原样写出。
                Merges =
                [
                    new ChannelMergeSettings
                    {
                        ProgramId = 1, Merge = true,
                        Sources = [new ChannelMergeSource { Pid = 0x1FF0 }, new ChannelMergeSource { Pid = 0x1FF1 }, new ChannelMergeSource { Pid = 0x1FF2 }],
                        Outputs = [new AudioOutput { Codec = AudioOutputCodec.Ac3, BitrateKbps = 448 }]
                    }
                ]
            };
            var length = new FileInfo(source).Length / 188 * 188;
            var copy = Path.Combine(directory, "copy.ts");
            var smart = Path.Combine(directory, "smart.ts");
            await RunAsync(async () =>
            {
                var output = new OutputWindowViewModel();
                output.SetBatchTasks(
                [
                    new(source, copy, 0, length, AudioRebuild: plan),
                    new(source, smart, 0, -1, FrameAccurate: new FrameAccurateExportSettings
                    {
                        Container = "ts", VideoIndex = videoIndex, StreamIndices = indices
                    }, StartPts: startPts, EndPts: endPts, AudioRebuild: plan)
                ]);
                output.ConfirmProgramTableWarningAsync = (_, _) => Task.FromResult(TsProgramTableExportChoice.Original);
                await output.OutputCommand.ExecuteAsync(null);
                Assert.Null(output.Exception);
                Assert.Equal([0, 1], output.CompletedTaskIndices);
                // 轨道不在节目表里，没有实际转码，导出结果不标记杜比 E 转码。
                Assert.All(output.Outcomes, outcome => Assert.Equal(ExportModifiers.None, outcome.Modifiers));
            });
            Assert.Equal(File.ReadAllBytes(source).AsSpan(0, (int)length).ToArray(), File.ReadAllBytes(copy));
            Assert.True(new FileInfo(smart).Length > 0);
            Assert.Equal(["copy.ts", "smart.ts"], Directory.GetFiles(directory).Select(Path.GetFileName).Order());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>批量复制任务带多轨合并计划：输出加入合并的 5.1，导出结果标记“合并 5.1”。</summary>
    [NativeMediaFact("remux")]
    public async Task CopyTaskMergesTracksAndMarksTheOutcome()
    {
        var source = Path.Combine(NativeTestEnvironment.FixtureDirectory("remux"), "channel-merge.ts");
        var directory = Path.Combine(Path.GetTempPath(), $"tscutter-merge-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var plan = new AudioRebuildPlan
            {
                Merges =
                [
                    new ChannelMergeSettings
                    {
                        ProgramId = 1, Merge = true,
                        Sources = [new ChannelMergeSource { Pid = 0x101 }, new ChannelMergeSource { Pid = 0x102 }, new ChannelMergeSource { Pid = 0x103 }],
                        Outputs = [new AudioOutput { Codec = AudioOutputCodec.Ac3, BitrateKbps = 448 }]
                    }
                ]
            };
            var length = new FileInfo(source).Length / 188 * 188;
            var copy = Path.Combine(directory, "copy.ts");
            await RunAsync(async () =>
            {
                var output = new OutputWindowViewModel();
                output.SetBatchTasks([new(source, copy, 0, length, AudioRebuild: plan)]);
                output.ConfirmProgramTableWarningAsync = (_, _) => Task.FromResult(TsProgramTableExportChoice.Original);
                await output.OutputCommand.ExecuteAsync(null);
                Assert.Null(output.Exception);
                Assert.Equal(ExportModifiers.ChannelsMerged, Assert.Single(output.Outcomes).Modifiers);
            });
            using var reopened = InterruptibleInputFormatContext.Open(copy, static () => false);
            reopened.LoadStreamInfo();
            var audio = Assert.Single(reopened.Streams, stream => stream.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_AUDIO);
            Assert.Equal(0x101, audio.Id);
            Assert.Equal(AVCodecID.AV_CODEC_ID_AC3, audio.Codecpar!.CodecId);
            Assert.Equal(6, audio.Codecpar.ChLayout.nb_channels);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
