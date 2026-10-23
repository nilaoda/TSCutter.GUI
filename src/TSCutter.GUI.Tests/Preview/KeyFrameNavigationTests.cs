using Avalonia.Headless;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Tests.UI;
using Xunit;

namespace TSCutter.GUI.Tests.Preview;

[Collection("Avalonia UI")]
public sealed class KeyFrameNavigationTests
{
    [NativeMediaFact("keyframes")]
    public async Task BackwardNavigationUsesActualNeighborsAndKeepsForwardCursorAligned()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            var directory = TSCutter.GUI.Tests.FFmpeg.NativeTestEnvironment.FixtureDirectory("keyframes");
            foreach (var name in new[] { "fixed.mp4", "fractional.ts", "variable.mp4", "hevc-hlg.ts" })
            {
                var path = Path.Combine(directory, name);
                var expected = System.Text.Json.JsonSerializer.Deserialize<double[]>(File.ReadAllText(path + ".json"))!;
                // 直接扫描解码器取得原始 PTS，导航测试不依赖预览条服务。
                long anchorPts = 0;
                using (var reference = new VideoInstance(path))
                {
                    await reference.InitVideoAsync();
                    for (var index = 0; index <= 8; index++)
                    {
                        var frame = await reference.DecodeNextSearchFrameAsync(32, 18, CancellationToken.None);
                        Assert.NotNull(frame);
                        Assert.Equal(expected[index], frame.FrameTimestamp.TotalSeconds, 3);
                        anchorPts = frame.FramePts;
                        Dispose(frame);
                    }
                }
                foreach (var hardware in new[] { false, true })
                {
                    using var video = new VideoInstance(path, enableHardwareDecoding: hardware);
                    await video.InitVideoAsync();
                    // 普通跳转回片头后，首帧和下一关键帧都不能被 TS seek 或延迟输出跳过。
                    var firstPreview = await video.DecodeAtTimeAsync(TimeSpan.Zero);
                    Assert.Equal(expected[0], firstPreview.FrameTimestamp.TotalSeconds, 3);
                    Dispose(firstPreview);
                    var nextPreview = await video.DecodeNextFrameAsync(1);
                    Assert.Equal(expected[1], nextPreview.FrameTimestamp.TotalSeconds, 3);
                    Dispose(nextPreview);
                    Dispose(await video.DecodeKeyFrameAtPtsAsync(anchorPts));
                    foreach (var index in new[] { 7, 6, 5 })
                    {
                        var result = await video.DecodeNextFrameAsync(-1);
                        Assert.Equal(expected[index], result.FrameTimestamp.TotalSeconds, 3);
                        Assert.Equal(result.FramePts, video.CurrentPts);
                        Dispose(result);
                    }
                    var forward = await video.DecodeNextFrameAsync(1);
                    Assert.Equal(expected[6], forward.FrameTimestamp.TotalSeconds, 3);
                    Dispose(forward);
                    var first = await video.DecodeNextFrameAsync(-10);
                    Assert.Equal(expected[0], first.FrameTimestamp.TotalSeconds, 3);
                    Dispose(first);
                    var stillFirst = await video.DecodeNextFrameAsync(-1);
                    Assert.Equal(expected[0], stillFirst.FrameTimestamp.TotalSeconds, 3);
                    Dispose(stillFirst);
                }
            }
            return true;
        }, CancellationToken.None);
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
