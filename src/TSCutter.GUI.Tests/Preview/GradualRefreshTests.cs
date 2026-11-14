using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Player;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Tests.UI;
using TSCutter.GUI.Utils.Media;
using Xunit;

namespace TSCutter.GUI.Tests.Preview;

/// <summary>
/// 逐行刷新（周期帧内刷新）H.264：没有 IDR 和 I 帧，关键帧只是 P 条带的恢复点，且恢复点 SEI 声称立即恢复。
/// 单独从恢复点解码画面不完整，跳转、导航、播放和截图都须从更早处连续解码，结果与从片头连续解码一致。
/// </summary>
[Collection("Avalonia UI")]
public sealed class GradualRefreshTests
{
    [NativeMediaFact("frameaccurate")]
    public async Task RecoveryPointsAreShownAfterTheRefreshCompletes()
    {
        var path = Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), "gradual-refresh.ts");
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await headless.Dispatch<bool>(async () =>
        {
            // 参考：从片头连续扫描，刷新完成后的恢复点画面即正确画面；类型标为恢复点而不是 P。
            var reference = new List<(long Pts, TimeSpan Time, string Hash)>();
            using (var scan = new VideoInstance(path))
            {
                await scan.InitVideoAsync();
                Assert.True(scan.UsesGradualRefresh);
                Assert.Equal(VideoInstance.GradualRefreshPrerollSeconds, scan.RecoveryPrerollSeconds);
                // 智能剪辑以恢复点衔接（复制区开头几秒与原片略有差别）。
                Assert.Equal(FrameAccurateSupport.Supported, scan.FrameAccurateSupport);
                while (await scan.DecodeNextSearchFrameAsync(0, 0, CancellationToken.None) is { } frame)
                {
                    Assert.Equal(KeyFrameTypeReader.RecoveryPoint, frame.KeyFrameType);
                    reference.Add((frame.FramePts, frame.FrameTimestamp, Hash(frame)));
                    Dispose(frame);
                }
            }
            Assert.InRange(reference.Count, 20, 30);

            using var video = new VideoInstance(path);
            await video.InitVideoAsync();
            // 按 PTS 定位缩略图、按时间跳转、向前一个关键帧：画面都与连续解码一致。
            foreach (var index in new[] { reference.Count - 3, reference.Count / 2 })
            {
                var frame = await video.DecodeKeyFrameAtPtsAsync(reference[index].Pts);
                Assert.NotNull(frame);
                Assert.Equal(reference[index].Pts, frame.FramePts);
                Assert.Equal(reference[index].Hash, Hash(frame));
                Dispose(frame);
            }
            var target = reference[^5];
            var seeked = await video.DecodeAtTimeAsync(target.Time);
            Assert.Equal(target.Pts, seeked.FramePts);
            Assert.Equal(target.Hash, Hash(seeked));
            Dispose(seeked);
            var previous = await video.DecodeNextFrameAsync(-1);
            Assert.Equal(reference[^6].Pts, previous.FramePts);
            Assert.Equal(reference[^6].Hash, Hash(previous));
            Dispose(previous);

            // 播放从恢复点开始：首个呈现画面已刷新完整。
            var first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (var session = await PlaybackSession.StartAsync(new PlaybackOptions
                         {
                             FilePath = path, StartSeconds = target.Time.TotalSeconds,
                             EndSeconds = target.Time.TotalSeconds + 1,
                             TimelineOriginSeconds = video.TimelineOriginSeconds,
                             EstimatedKeyFrameIntervalSeconds = video.EstimatedKeyFrameIntervalSeconds,
                             MinimumPrerollSeconds = video.RecoveryPrerollSeconds,
                         }, uiPost: action => action(), initialize: owner => owner.FramePresented += result =>
                         {
                             if (result.FramePts == target.Pts)
                                 first.TrySetResult(Hash(result));
                             Dispose(result);
                         }))
                Assert.Equal(target.Hash, await first.Task.WaitAsync(TimeSpan.FromSeconds(15)));
            return true;
        }, CancellationToken.None);
    }

    /// <summary>
    /// MPEG-2 帧内刷新（杜比 E 回传源的结构）：全程没有 I 帧，解复用器一个关键包都不标，只在部分 P 帧前重复序列头。
    /// 以前只把关键包送入解码器，打开即“解码失败”，视频不可预览。现在带序列头的 P 帧作为恢复点，
    /// 按逐行刷新预滚：从入口单独解码画面不完整，跳转、导航和播放须先连续解码，结果才与从片头连续解码一致。
    /// </summary>
    [NativeMediaFact("frameaccurate")]
    public async Task Mpeg2IntraRefreshUsesSequenceHeadersAsRecoveryPoints()
    {
        var path = Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), "mpeg2-refresh.ts");
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await headless.Dispatch<bool>(async () =>
        {
            var reference = new List<(long Pts, TimeSpan Time, string Hash)>();
            using (var scan = new VideoInstance(path))
            {
                await scan.InitVideoAsync();
                Assert.True(scan.UsesMpeg2RefreshPoints);
                Assert.True(scan.UsesGradualRefresh);
                Assert.True(scan.CanBinaryClip);
                Assert.Equal(1, scan.EstimatedKeyFrameIntervalSeconds, 3);
                // 4:2:2 档次可以重新编码，以带序列头的 P 帧衔接，提供智能剪辑。
                Assert.Equal(FrameAccurateSupport.Supported, scan.FrameAccurateSupport);
                while (await scan.DecodeNextSearchFrameAsync(0, 0, CancellationToken.None) is { } frame)
                {
                    Assert.Equal(KeyFrameTypeReader.RecoveryPoint, frame.KeyFrameType);
                    reference.Add((frame.FramePts, frame.FrameTimestamp, Hash(frame)));
                    Dispose(frame);
                }
            }
            // 499 帧，每 25 帧一个入口。
            Assert.Equal(20, reference.Count);
            Assert.Equal(reference.Count, reference.Select(item => item.Hash).Distinct().Count());

            using var video = new VideoInstance(path);
            await video.InitVideoAsync();
            // 打开后的首帧即第一个入口；片头的入口缺少参考帧，只核对位置。
            var first = await video.DecodeNextFrameAsync(1);
            Assert.Equal(reference[0].Pts, first.FramePts);
            Dispose(first);
            // 以下目标都在片头 12 秒之后，预滚从片中的入口开始。
            foreach (var index in new[] { reference.Count - 2, 14 })
            {
                var frame = await video.DecodeKeyFrameAtPtsAsync(reference[index].Pts);
                Assert.NotNull(frame);
                Assert.Equal(reference[index].Pts, frame.FramePts);
                Assert.Equal(reference[index].Hash, Hash(frame));
                Dispose(frame);
            }
            var target = reference[^3];
            // 按时间向后定位：与普通 TS 一样，正好落在关键帧时间时可能取到下一个关键帧。
            var seeked = await video.DecodeAtTimeAsync(target.Time);
            var landed = reference.FindIndex(item => item.Pts == seeked.FramePts);
            Assert.InRange(landed, reference.Count - 3, reference.Count - 2);
            Assert.Equal(reference[landed].Hash, Hash(seeked));
            Dispose(seeked);
            var previous = await video.DecodeNextFrameAsync(-1);
            Assert.Equal(reference[landed - 1].Pts, previous.FramePts);
            Assert.Equal(reference[landed - 1].Hash, Hash(previous));
            Dispose(previous);

            // 播放同样按入口包等待关键帧：须由预览的检测开启补标，否则一直等不到画面。
            var presented = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (var session = await PlaybackSession.StartAsync(new PlaybackOptions
                         {
                             FilePath = path, StartSeconds = target.Time.TotalSeconds,
                             EndSeconds = target.Time.TotalSeconds + 1,
                             TimelineOriginSeconds = video.TimelineOriginSeconds,
                             EstimatedKeyFrameIntervalSeconds = video.EstimatedKeyFrameIntervalSeconds,
                             MinimumPrerollSeconds = video.RecoveryPrerollSeconds,
                             Mpeg2RefreshPoints = video.UsesMpeg2RefreshPoints,
                             TsSyncOffset = video.TsSyncOffset,
                         }, uiPost: action => action(), initialize: owner => owner.FramePresented += result =>
                         {
                             if (result.FramePts == target.Pts)
                                 presented.TrySetResult(Hash(result));
                             Dispose(result);
                         }))
                Assert.Equal(target.Hash, await presented.Task.WaitAsync(TimeSpan.FromSeconds(15)));
            return true;
        }, CancellationToken.None);
    }

    private static string Hash(DecodeResult result)
    {
        var bitmap = result.BitmapLease?.Bitmap ?? result.Bitmap;
        Assert.NotNull(bitmap);
        var size = bitmap.PixelSize;
        var stride = size.Width * 4;
        var pixels = new byte[stride * size.Height];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), pixels.Length, stride); }
        finally { handle.Free(); }
        return Convert.ToHexString(SHA256.HashData(pixels));
    }

    private static void Dispose(DecodeResult? result)
    {
        Assert.NotNull(result);
        result.BitmapLease?.Dispose();
        if (result.BitmapLease is null)
            result.Bitmap?.Dispose();
        result.GpuFrame?.Dispose();
    }
}
