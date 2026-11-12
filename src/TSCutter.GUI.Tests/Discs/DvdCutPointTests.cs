using System.Reflection;
using System.Security.Cryptography;
using FFmpeg.AutoGen.Abstractions;
using Splat;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Services.Discs;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Tests.Editing;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.ViewModels;
using Xunit;

namespace TSCutter.GUI.Tests.Discs;

/// <summary>
/// DVD（dvdvideo 解复用器）跳转后以目标时间重建时间戳，同一关键帧在不同跳转中的时间可能相差一个 VOBU。
/// 标记时记录关键帧压缩包的签名，导出按签名定位：从标记点开始的导出首帧就是预览画面；止于标记点之前的导出
/// 恰好结束在该关键帧之前，与整段导出相比只少该关键帧所在开放 GOP 的前导 B 帧。
/// </summary>
[Collection("Avalonia UI")]
public sealed class DvdCutPointTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [DiscMediaFact]
    public Task GeneratedDvdCutsAtTheFramesSeenInPreview() =>
        VerifyCutPoints(Path.Combine(NativeTestEnvironment.FixtureDirectory("iso"), "dvd.iso"), titleIndex: 1);

    [NativeRuntimeFact("TSCUTTER_DVD_TEST_SAMPLE")]
    public Task RealDvdCutsAtTheFramesSeenInPreview() =>
        VerifyCutPoints(Environment.GetEnvironmentVariable("TSCUTTER_DVD_TEST_SAMPLE")!, titleIndex: null);

    private static async Task VerifyCutPoints(string path, int? titleIndex)
    {
        var scan = await DiscScanService.ScanAsync(path, CancellationToken.None);
        var index = titleIndex ?? scan.RecommendedIndex;
        var input = scan.Inputs[index];
        var duration = scan.Titles[index].Duration.TotalSeconds;
        var directory = Directory.CreateTempSubdirectory("tscutter-dvd-cut-");
        try
        {
            await DiscIntegrationTests.RunUiAsync(async () =>
            {
                using var resolver = new ModernDependencyResolver();
                using var scope = resolver.WithResolver();
                Locator.CurrentMutable.RegisterConstant<ILocalizationService>(new LocalizationService());
                var vm = RemoveModeWorkflowTests.CreateViewModel(new RemoveModeWorkflowTests.MemoryConfiguration());
                try
                {
                    typeof(MainWindowViewModel).GetField("_discInput", Private)!.SetValue(vm, input);
                    typeof(MainWindowViewModel).GetField("_discTitleInfo", Private)!.SetValue(vm, scan.Titles[index]);
                    vm.VideoPath = input.SourcePath;
                    Assert.True(await (Task<bool>)typeof(MainWindowViewModel).GetMethod("LoadVideoAsync", Private)!
                        .Invoke(vm, [null])!);
                    // 跳到 VOBU 中间的时刻：预览显示该 VOBU 的关键帧，但时间戳是请求的时刻。
                    foreach (var fraction in new[] { 0.35, 0.65 })
                    {
                        await vm.SeekToTimeAndDrawFrameAsync(TimeSpan.FromSeconds(duration * fraction + 0.13));
                        await RemoveModeWorkflowTests.Settle();
                        await vm.AddClipCommand.ExecuteAsync(null);
                        await RemoveModeWorkflowTests.Settle();
                    }
                    // 删除模式：保留段止于删除起点，导出段带上该关键帧的签名。
                    var removed = vm.Clips.OrderBy(clip => clip.StartTime).ToArray();
                    var kept = typeof(MainWindowViewModel).GetMethod("CreateKeptRanges", Private)!.Invoke(vm, [removed])!;
                    var snapshot = typeof(MainWindowViewModel).GetMethod("CaptureRemoveExportSnapshot", Private)!
                        .Invoke(vm, [removed, kept, false])!;
                    var segment = Assert.Single((IReadOnlyList<RemuxExportSegment>)snapshot.GetType()
                        .GetProperty("RemuxSegments")!.GetValue(snapshot)!);
                    Assert.Equal(removed[0].StartSignature, segment.EndSignature);
                    var video = (VideoInstance)typeof(MainWindowViewModel).GetField("_videoInstance", Private)!.GetValue(vm)!;
                    var settings = video.CreateExportSettings();
                    var service = new RemuxExportService();
                    foreach (var clip in vm.Clips.ToArray())
                    {
                        Assert.NotNull(clip.StartSignature);
                        var mark = clip.StartTime;
                        var preview = VideoInstance.DecodeAtTimeSync(video, TimeSpan.FromSeconds(mark));
                        Assert.Equal(clip.StartSignature, preview.FrameEndSignature.ToTextOrNull());
                        var later = video.DecodeNextFrameSync(3);
                        var before = VideoInstance.DecodeAtTimeSync(video, TimeSpan.FromSeconds(Math.Max(0, mark - 4)));
                        var (laterTime, laterSignature) = (later.FrameTimestamp.TotalSeconds, later.FrameEndSignature.ToTextOrNull());
                        var (beforeTime, beforeSignature) = (before.FrameTimestamp.TotalSeconds, before.FrameEndSignature.ToTextOrNull());
                        var fromMark = Path.Combine(directory.FullName, $"from-{mark:F0}.{settings.Container}");
                        var toMark = Path.Combine(directory.FullName, $"to-{mark:F0}.{settings.Container}");
                        var whole = Path.Combine(directory.FullName, $"whole-{mark:F0}.{settings.Container}");
                        await service.ExportAsync(new RemuxExportRequest(fromMark, [new RemuxExportSegment(input.SourcePath,
                            mark, laterTime, settings.Snapshot(), StartSignature: clip.StartSignature, EndSignature: laterSignature)]));
                        await service.ExportAsync(new RemuxExportRequest(toMark, [new RemuxExportSegment(input.SourcePath,
                            beforeTime, mark, settings.Snapshot(), EndExclusive: true,
                            StartSignature: beforeSignature, EndSignature: clip.StartSignature)]));
                        await service.ExportAsync(new RemuxExportRequest(whole, [new RemuxExportSegment(input.SourcePath,
                            beforeTime, laterTime, settings.Snapshot(), StartSignature: beforeSignature, EndSignature: laterSignature)]));

                        // 从标记点开始的导出首帧与预览画面逐像素相同。
                        using (var exported = new VideoInstance(fromMark))
                        {
                            exported.InitVideo();
                            var first = VideoInstance.DecodeAtTimeSync(exported, TimeSpan.Zero);
                            Assert.Equal(Pixels(preview), Pixels(first));
                        }
                        // 整段 = 止于标记点之前的部分 + 标记关键帧 + 若干前导 B 帧（两侧都不输出）+ 从标记点开始部分的其余画面。
                        var (head, tail, all) = (VideoPackets(toMark), VideoPackets(fromMark), VideoPackets(whole));
                        Assert.Equal(head, all.Take(head.Count));
                        Assert.Equal(tail[0], all[head.Count]);
                        var leading = all.Count - head.Count - tail.Count;
                        Assert.InRange(leading, 0, 3);
                        Assert.Equal(tail.Skip(1), all.Skip(head.Count + 1 + leading));
                    }
                    // 片尾签名查找和 GOP 检查可能读到 EOF，后续仍应能定位并输出所选关键帧。
                    var endingFrame = VideoInstance.DecodeAtTimeSync(video, TimeSpan.FromSeconds(duration - 0.1));
                    try
                    {
                        var tailOutput = Path.Combine(directory.FullName, $"tail.{settings.Container}");
                        await service.ExportAsync(new RemuxExportRequest(tailOutput,
                            [new RemuxExportSegment(input.SourcePath, Math.Max(0, duration - 3),
                                endingFrame.FrameTimestamp.TotalSeconds, settings.Snapshot(),
                                EndSignature: endingFrame.FrameEndSignature.ToTextOrNull())]));
                        Assert.True(endingFrame.FrameEndSignature.MatchesHash(
                            Convert.FromHexString(VideoPackets(tailOutput, keysOnly: true)[^1])));
                    }
                    finally
                    {
                        endingFrame.BitmapLease?.Dispose();
                        if (endingFrame.BitmapLease is null) endingFrame.Bitmap?.Dispose();
                        endingFrame.GpuFrame?.Dispose();
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

    private static unsafe List<string> VideoPackets(string path, bool keysOnly = false)
    {
        using var input = FormatContext.OpenInputUrl(path);
        input.LoadStreamInfo();
        var video = input.Streams.First(stream => stream.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_VIDEO);
        var result = new List<string>();
        foreach (var packet in input.ReadPackets())
        {
            if (packet.StreamIndex != video.Index || keysOnly && (packet.Flags & 1) == 0) continue;
            AVPacket* raw = packet;
            result.Add(Convert.ToHexString(SHA256.HashData(new ReadOnlySpan<byte>(raw->data, raw->size))));
        }
        return result;
    }

    private static unsafe byte[] Pixels(DecodeResult frame)
    {
        var bitmap = frame.Bitmap!;
        var size = bitmap.PixelSize;
        var pixels = new byte[size.Width * size.Height * 4];
        fixed (byte* pointer = pixels)
            bitmap.CopyPixels(new Avalonia.PixelRect(size), (nint)pointer, pixels.Length, size.Width * 4);
        return pixels;
    }
}
