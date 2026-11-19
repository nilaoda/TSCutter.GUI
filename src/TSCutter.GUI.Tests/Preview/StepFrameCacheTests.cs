using System.Security.Cryptography;
using Avalonia.Headless;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Tests.UI;
using Xunit;

namespace TSCutter.GUI.Tests.Preview;

/// <summary>
/// 主预览的逐帧缓存与顺序续解：前进、后退（跨过关键帧）、关键帧导航穿插其中时，
/// 每一步的 PTS 与像素都与不缓存的实例完全一致；连续后退和前进确实命中缓存。
/// </summary>
[Collection("Avalonia UI")]
public sealed class StepFrameCacheTests
{
    [NativeMediaTheory("frameaccurate")]
    [InlineData("h264.ts", 2.3)]
    [InlineData("hevc-cra.ts", 4.95)]
    [InlineData("open-gop.ts", 3.0)]
    [InlineData("mpeg2.ts", 3.1)]
    [InlineData("hevc-hlg.ts", 2.0)]
    [InlineData("h264.mp4", 2.3)]
    [InlineData("vp9.mkv", 2.0)]
    public async Task CachedSteppingMatchesUncachedDecoding(string name, double start)
    {
        var source = Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), name);
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await headless.Dispatch<bool>(async () =>
        {
            var runs = new List<List<string>>();
            foreach (var cached in new[] { false, true })
            {
                using var video = new VideoInstance(source, retainPreviewFrame: true) { IsMainPreview = cached };
                await video.InitVideoAsync();
                var steps = new List<string>();
                var current = await video.DecodeAtTimeAsync(TimeSpan.FromSeconds(start));
                var pts = Record(steps, current);
                var hits = 0;
                // 前进、后退越过起点所在关键帧、再前进；中间插入一次关键帧导航，结束续解状态。
                foreach (var (direction, count) in new[] { (1, 6), (-1, 14), (1, 3) })
                {
                    for (var index = 0; index < count; index++)
                    {
                        var step = await video.DecodeAdjacentFrameAsync(pts, direction);
                        Assert.NotNull(step);
                        pts = Record(steps, step);
                        if (cached && video.StepCacheCount > 0)
                            hits++;
                    }
                    if (direction < 0)
                    {
                        var key = await video.DecodeNextFrameAsync();
                        Record(steps, key);
                        var resumed = await video.DecodeAdjacentFrameAsync(pts, 1);
                        Assert.NotNull(resumed);
                        pts = Record(steps, resumed);
                    }
                }
                if (cached)
                    Assert.True(hits > 0, "Stepping never filled the frame cache.");
                runs.Add(steps);
            }
            Assert.Equal(runs[0], runs[1]);
            return true;
        }, CancellationToken.None);
    }

    [NativeMediaFact("frameaccurate")]
    public async Task ContinuationDoesNotLeakIntoKeyFrameNavigation()
    {
        var source = Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), "h264.ts");
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await headless.Dispatch<bool>(async () =>
        {
            var runs = new List<List<string>>();
            foreach (var cached in new[] { false, true })
            {
                using var video = new VideoInstance(source, retainPreviewFrame: true) { IsMainPreview = cached };
                await video.InitVideoAsync();
                var steps = new List<string>();
                var pts = Record(steps, await video.DecodeAtTimeAsync(TimeSpan.FromSeconds(1.5)));
                for (var round = 0; round < 3; round++)
                {
                    // 前进到缓存末尾之后需要续解；随后的关键帧导航必须仍只输出关键帧。
                    for (var index = 0; index < 4; index++)
                        pts = Record(steps, await video.DecodeAdjacentFrameAsync(pts, 1));
                    // 命中缓存的后退需要恢复读取位置。恢复时未缓存的解码结果不能建立续解状态。
                    pts = Record(steps, await video.DecodeAdjacentFrameAsync(pts, -1));
                    var key = await video.DecodeNextFrameAsync();
                    Assert.True(key.FramePts > pts);
                    pts = Record(steps, key);
                }
                runs.Add(steps);
            }
            Assert.Equal(runs[0], runs[1]);
            return true;
        }, CancellationToken.None);
    }

    private static long Record(List<string> steps, DecodeResult? result)
    {
        Assert.NotNull(result);
        Assert.NotNull(result.Bitmap);
        using var png = new MemoryStream();
        result.Bitmap.Save(png);
        steps.Add($"{result.FramePts}/{Convert.ToHexString(SHA256.HashData(png.ToArray()))}");
        result.BitmapLease?.Dispose();
        if (result.BitmapLease is null)
            result.Bitmap.Dispose();
        return result.FramePts;
    }
}
