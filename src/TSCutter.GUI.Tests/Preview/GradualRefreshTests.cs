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
                Assert.Equal(FrameAccurateSupport.GradualRefresh, scan.FrameAccurateSupport);
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
