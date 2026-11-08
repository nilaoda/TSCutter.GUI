using System.Reflection;
using System.Security.Cryptography;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Services.Discs;
using TSCutter.GUI.Tests.Discs;
using Avalonia.Headless;
using Avalonia.Threading;
using HanumanInstitute.MvvmDialogs;
using Splat;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.TransportStream.Processing;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Tests.Player;
using TSCutter.GUI.Tests.UI;
using TSCutter.GUI.ViewModels;
using Xunit;

namespace TSCutter.GUI.Tests.Editing;

/// <summary>
/// 删除模式的完整流程：在主界面标记删除范围 [起点, 终点)（终点是恢复播放的画面），开启删除模式后导出，
/// 输出为删除范围之外的全部画面。覆盖 TS 关键帧剪辑（按字节合并）、MP4 关键帧剪辑（重新封装）与智能剪辑（任意画面），
/// 以及删除模式下其他输出命令不可用、工程按版本 6 保存删除模式与终点画面位置。
/// </summary>
[Collection("Avalonia UI")]
public sealed class RemoveModeWorkflowTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal sealed class MemoryConfiguration : IConfigurationService
    {
        public AppConfig CurrentConfig { get; } = new() { PreferHardwareDecoding = false };
        public void Load() { }
        public void Save() { }
        public void ApplyTheme(string theme) { }
        public void ApplyLanguage(string language) { }
    }

    [NativeMediaTheory("frameaccurate")]
    // 关键帧剪辑：删除第 60–119 帧与第 210 帧到结尾（两个删除范围，后者到文件结尾）。
    [InlineData("h264.ts", false, new[] { 60, 120, 210, -1 })]
    [InlineData("h264.mp4", false, new[] { 60, 120, 210, -1 })]
    // 智能剪辑：删除范围落在任意画面上；第一个范围从文件开头开始。
    [InlineData("h264.ts", true, new[] { 0, 17, 45, 101, 250, 263 })]
    public async Task RemovesTheMarkedRanges(string name, bool smartCut, int[] marks)
    {
        var source = Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), name);
        var frames = FrameAccurateExportTests.DecodeFrames(source);
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await headless.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(new LocalizationService());
            var configuration = new MemoryConfiguration();
            var vm = CreateViewModel(configuration);
            var output = Path.Combine(Path.GetTempPath(), $"tscutter-remove-ui-{Guid.NewGuid():N}{Path.GetExtension(name)}");
            try
            {
                vm.IsFrameAccurateMode = smartCut;
                vm.VideoPath = source;
                Assert.True(await (Task<bool>)typeof(MainWindowViewModel).GetMethod("LoadVideoAsync", Private)!
                    .Invoke(vm, [null])!);
                // 按画面标记每个删除范围：先新建片段并定位到起点，再定位到终点（-1 表示不标记终点，删除到文件结尾）。
                for (var i = 0; i < marks.Length; i += 2)
                {
                    await vm.SeekToKeyFrameAndDrawFrameAsync(frames[marks[i]]);
                    await Settle();
                    await vm.AddClipCommand.ExecuteAsync(null);
                    await vm.MarkClipStartShortcutCommand.ExecuteAsync(null);
                    await Settle();
                    if (marks[i + 1] >= 0)
                    {
                        await vm.SeekToKeyFrameAndDrawFrameAsync(frames[marks[i + 1]]);
                        await Settle();
                        await vm.MarkClipEndShortcutCommand.ExecuteAsync(null);
                        await Settle();
                    }
                }
                Assert.Equal(marks.Length / 2, vm.Clips.Count);
                foreach (var clip in vm.Clips)
                    clip.IsSelected = true;

                vm.IsRemoveMode = true;
                await Settle();
                Assert.Same(vm.ExportRemovingSelectedRangesCommand, vm.ClipOutputCommand);
                Assert.True(vm.ExportRemovingSelectedRangesCommand.CanExecute(null));
                Assert.False(vm.SaveVideoClickCommand.CanExecute(null));
                Assert.False(vm.AddToQueueCommand.CanExecute(null));
                Assert.False(vm.ExportAllCommand.CanExecute(null));
                Assert.False(vm.MergeSelectedClipsCommand.CanExecute(null));
                Assert.All(vm.SelectedClipRanges, range => Assert.True(range.Removed));
                Assert.All(vm.KeyFrameStripRanges, range => Assert.True(range.Removed));
                Assert.All(vm.Clips, clip => Assert.True(clip.IsRemoval));
                // 开关状态写入设置，下次启动沿用。
                Assert.Equal(ClipListMode.Remove, configuration.CurrentConfig.ClipListMode);

                // 工程：删除模式使用版本 6，并保存终点画面的位置。
                var project = (CutterProject)typeof(MainWindowViewModel).GetMethod("CaptureProject", Private)!
                    .Invoke(vm, [false])!;
                Assert.Equal(6, project.Version);
                Assert.Equal(ClipListMode.Remove, project.ClipMode);
                Assert.All(project.Clips.Where(clip => clip.EndPosition >= 0), clip => Assert.True(clip.EndFramePosition >= 0));

                // 按界面导出时的同一份快照输出（保存对话框之外的步骤相同）。
                var removed = vm.Clips.Where(clip => clip.IsSelected).OrderBy(clip => clip.StartTime).ToArray();
                var kept = typeof(MainWindowViewModel).GetMethod("CreateKeptRanges", Private)!.Invoke(vm, [removed])!;
                var snapshot = typeof(MainWindowViewModel).GetMethod("CaptureRemoveExportSnapshot", Private)!
                    .Invoke(vm, [removed, kept, smartCut])!;
                var type = snapshot.GetType();
                if (type.GetProperty("FrameAccurateSegments")!.GetValue(snapshot) is IReadOnlyList<FrameAccurateExportSegment> segments)
                    await new FrameAccurateExportService().ExportAsync(new FrameAccurateExportRequest(output, segments));
                else if (type.GetProperty("RemuxSegments")!.GetValue(snapshot) is IReadOnlyList<RemuxExportSegment> remux)
                    await new RemuxExportService().ExportAsync(new RemuxExportRequest(output, remux));
                else
                {
                    var request = (TsClipMergeRequest)type.GetProperty("TsRequest")!.GetValue(snapshot)!;
                    await new TsClipMergeService().MergeAsync(new TsClipMergeRequest
                    {
                        SourcePath = request.SourcePath, OutputPath = output, ProgramId = request.ProgramId,
                        VideoPid = request.VideoPid, AudioPids = request.AudioPids, Ranges = request.Ranges
                    });
                }
                var expected = Enumerable.Range(0, frames.Count).ToList();
                for (var i = 0; i < marks.Length; i += 2)
                    expected.RemoveAll(frame => frame >= marks[i] && (marks[i + 1] < 0 || frame < marks[i + 1]));
                Assert.Equal(expected, FrameAccurateExportTests.DecodeNumbers(output));
            }
            finally
            {
                File.Delete(output);
                vm.Close();
            }
            return true;
        }, CancellationToken.None);
    }

    /// <summary>
    /// 光盘来源（蓝光正片：三个播放项，分界在 90s 与 170s，关键帧间隔 2s）：在主界面按关键帧标记删除范围后导出。
    /// 每个保留段单独导出时止于删除起点之前（帧数恰为两个关键帧之间的画面数），合并输出与各段逐包一致；
    /// 覆盖删除起点在开头、删除到结尾、删除范围跨播放项衔接处、保留段止于或始于衔接处。
    /// </summary>
    [DiscMediaFact]
    public async Task RemovesRangesFromBlurayTitle()
    {
        var path = Path.Combine(NativeTestEnvironment.FixtureDirectory("iso"), "bluray", "BDMV");
        var scan = await DiscScanService.ScanAsync(path, CancellationToken.None);
        // 播放列表的枚举顺序因平台而异，按编号选择合成素材中的三播放项标题。
        var titleIndex = Enumerable.Range(0, scan.Inputs.Length).Single(index => scan.Inputs[index].TitleNumber == 2);
        foreach (var marks in new[]
                 {
                     new[] { 20.0, 30.0, 84.0, 96.0, 190.0, -1 },
                     [0.0, 10.0, 80.0, 90.0, 166.0, 176.0],
                     [90.0, 100.0]
                 })
        {
            var directory = Directory.CreateTempSubdirectory("tscutter-disc-remove-");
            try
            {
                await DiscIntegrationTests.RunUiAsync(async () =>
                {
                    using var resolver = new ModernDependencyResolver();
                    using var scope = resolver.WithResolver();
                    Locator.CurrentMutable.RegisterConstant<ILocalizationService>(new LocalizationService());
                    var vm = CreateViewModel(new MemoryConfiguration());
                    try
                    {
                        typeof(MainWindowViewModel).GetField("_discInput", Private)!.SetValue(vm, scan.Inputs[titleIndex]);
                        typeof(MainWindowViewModel).GetField("_discTitleInfo", Private)!.SetValue(vm, scan.Titles[titleIndex]);
                        vm.VideoPath = scan.Inputs[titleIndex].SourcePath;
                        Assert.True(await (Task<bool>)typeof(MainWindowViewModel).GetMethod("LoadVideoAsync", Private)!
                            .Invoke(vm, [null])!);
                        for (var i = 0; i < marks.Length; i += 2)
                        {
                            await vm.SeekToTimeAndDrawFrameAsync(TimeSpan.FromSeconds(marks[i] + 0.01));
                            await Settle();
                            await vm.AddClipCommand.ExecuteAsync(null);
                            await vm.MarkClipStartShortcutCommand.ExecuteAsync(null);
                            await Settle();
                            if (marks[i + 1] >= 0)
                            {
                                await vm.SeekToTimeAndDrawFrameAsync(TimeSpan.FromSeconds(marks[i + 1] + 0.01));
                                await Settle();
                                await vm.MarkClipEndShortcutCommand.ExecuteAsync(null);
                                await Settle();
                            }
                        }
                        Assert.Equal(marks.Length / 2, vm.Clips.Count);
                        foreach (var clip in vm.Clips)
                            clip.IsSelected = true;
                        vm.IsRemoveMode = true;
                        await Settle();
                        Assert.True(vm.ExportRemovingSelectedRangesCommand.CanExecute(null));

                        var removed = vm.Clips.OrderBy(clip => clip.StartTime).ToArray();
                        // 标记落在所选时刻的关键帧上（标题时间轴与整秒有几毫秒的固定偏移，按半帧比较）。
                        for (var i = 0; i < removed.Length; i++)
                        {
                            Assert.InRange(removed[i].StartTime - marks[i * 2], -1 / 48.0, 1 / 48.0);
                            if (marks[i * 2 + 1] >= 0)
                                Assert.InRange(removed[i].EndTime - marks[i * 2 + 1], -1 / 48.0, 1 / 48.0);
                        }
                        var kept = typeof(MainWindowViewModel).GetMethod("CreateKeptRanges", Private)!.Invoke(vm, [removed])!;
                        var snapshot = typeof(MainWindowViewModel).GetMethod("CaptureRemoveExportSnapshot", Private)!
                            .Invoke(vm, [removed, kept, false])!;
                        var segments = (IReadOnlyList<RemuxExportSegment>)snapshot.GetType().GetProperty("RemuxSegments")!
                            .GetValue(snapshot)!;
                        var service = new RemuxExportService();
                        var parts = new List<DiscMedia>();
                        for (var i = 0; i < segments.Count; i++)
                        {
                            var output = Path.Combine(directory.FullName, $"part{i}.m2ts");
                            try
                            {
                                await service.ExportAsync(new RemuxExportRequest(output, [segments[i]]));
                            }
                            catch (RemuxExportException exception)
                            {
                                throw new Xunit.Sdk.XunitException($"marks {string.Join(",", marks)}, segment " +
                                    $"{segments[i].StartSeconds}-{segments[i].EndSeconds}: {exception.Code} {exception.Message}");
                            }
                            var media = InspectDisc(output);
                            // 止于删除起点之前：帧数恰为保留段起止两个关键帧之间的画面数（24 fps）。
                            if (segments[i].EndSeconds is { } end)
                                Assert.Equal((int)Math.Round((end - segments[i].StartSeconds) * 24), media.Frames);
                            parts.Add(media);
                        }
                        var merged = Path.Combine(directory.FullName, "merged.m2ts");
                        await service.ExportAsync(new RemuxExportRequest(merged, segments));
                        var whole = InspectDisc(merged);
                        Assert.Equal(parts.Sum(part => part.Frames), whole.Frames);
                        Assert.Equal(parts.SelectMany(part => part.Video), whole.Video);
                        Assert.Equal(parts.SelectMany(part => part.Audio), whole.Audio);
                        // 保留模式只标记终点的片段从时间轴原点开始：光盘首帧略晚于原点，从第一个关键帧开始导出，
                        // 包含终点关键帧。
                        if (marks[0] > 0)
                        {
                            var fromOrigin = Path.Combine(directory.FullName, "origin.m2ts");
                            await service.ExportAsync(new RemuxExportRequest(fromOrigin,
                                [new RemuxExportSegment(segments[0].SourcePath, 0, removed[0].StartTime, segments[0].Settings)]));
                            Assert.Equal((int)Math.Round(removed[0].StartTime * 24) + 1, InspectDisc(fromOrigin).Frames);
                        }
                    }
                    finally
                    {
                        vm.Close();
                    }
                    return true;
                });
            }
            finally
            {
                directory.Delete(recursive: true);
            }
        }
    }

    private sealed record DiscMedia(List<string> Video, List<string> Audio, int Frames);

    /// <summary>输出文件的视频、音频包内容摘要与解码出的画面数。</summary>
    private static unsafe DiscMedia InspectDisc(string path)
    {
        using var input = FormatContext.OpenInputUrl(path);
        input.LoadStreamInfo();
        var video = input.Streams.First(stream => stream.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_VIDEO);
        var audio = input.Streams.First(stream => stream.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_AUDIO);
        using var decoder = new CodecContext(Codec.FindDecoderById(video.Codecpar!.CodecId));
        decoder.FillParameters(video.Codecpar);
        decoder.PacketTimeBase = video.TimeBase;
        decoder.Open();
        using var destination = new Frame();
        var videoPackets = new List<string>();
        var audioPackets = new List<string>();
        var frames = 0;
        foreach (var packet in input.ReadPackets())
        {
            if (packet.StreamIndex != video.Index && packet.StreamIndex != audio.Index)
                continue;
            AVPacket* raw = packet;
            var hash = Convert.ToHexString(SHA256.HashData(new ReadOnlySpan<byte>(raw->data, raw->size)));
            if (packet.StreamIndex == video.Index)
            {
                videoPackets.Add(hash);
                foreach (var _ in decoder.DecodePacket(packet, destination))
                    frames++;
            }
            else
                audioPackets.Add(hash);
        }
        foreach (var _ in decoder.DecodePacket(null, destination))
            frames++;
        return new DiscMedia(videoPackets, audioPackets, frames);
    }

    /// <summary>删除模式按设置恢复；按工程恢复的模式不改动设置中的偏好。</summary>
    [Fact]
    public async Task RemoveModeIsRememberedAcrossLaunches()
    {
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await headless.Dispatch<bool>(() =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(new LocalizationService());
            var configuration = new MemoryConfiguration();
            configuration.CurrentConfig.ClipListMode = ClipListMode.Remove;
            var vm = CreateViewModel(configuration);
            try
            {
                Assert.True(vm.IsRemoveMode);
                vm.ClipMode = ClipListMode.Keep;
                Assert.Equal(ClipListMode.Remove, configuration.CurrentConfig.ClipListMode);
                vm.IsRemoveMode = true;
                vm.IsRemoveMode = false;
                Assert.Equal(ClipListMode.Keep, configuration.CurrentConfig.ClipListMode);
                var next = CreateViewModel(configuration);
                Assert.False(next.IsRemoveMode);
                next.Close();
            }
            finally
            {
                vm.Close();
            }
            return Task.FromResult(true);
        }, CancellationToken.None);
    }

    internal static MainWindowViewModel CreateViewModel(IConfigurationService configuration) =>
        new(new HanumanInstitute.MvvmDialogs.Avalonia.DialogService(
            System.Reflection.DispatchProxy.Create<IDialogManager, MainWindowPlaybackTests.UnusedDialogs>()), configuration);

    internal static async Task Settle()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }
}
