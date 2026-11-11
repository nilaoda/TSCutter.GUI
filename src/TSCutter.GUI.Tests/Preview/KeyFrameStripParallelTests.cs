using System.Reflection;
using Avalonia.Headless;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Services.Preview;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Tests.UI;
using Xunit;

namespace TSCutter.GUI.Tests.Preview;

/// <summary>
/// 关键帧导航条的并行扫描：多个解码实例分段读取后拼出的窗口必须与单实例顺序扫描完全一致（包括边缘和补读），
/// 中途发布的部分窗口也只能是真实相邻的关键帧；打开中途取消不得遗留打开的源文件。
/// </summary>
[Collection("Avalonia UI")]
public sealed class KeyFrameStripParallelTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [NativeMediaFact("keyframes")]
    public async Task ParallelScanMatchesSequentialScan()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            var directory = NativeTestEnvironment.FixtureDirectory("keyframes");
            foreach (var path in Directory.EnumerateFiles(directory).Where(path => path.EndsWith(".mp4") || path.EndsWith(".ts")))
            {
                var expected = System.Text.Json.JsonSerializer.Deserialize<double[]>(File.ReadAllText(path + ".json"))!;
                await using var parallel = new KeyFrameStripService { ScanInstanceCount = 3 };
                await using var sequential = new KeyFrameStripService { ScanInstanceCount = 1 };
                await parallel.OpenAsync(new KeyFrameStripSource(path, null, false), CancellationToken.None);
                await sequential.OpenAsync(new KeyFrameStripSource(path, null, false), CancellationToken.None);
                Assert.Equal(2, ((System.Collections.ICollection)typeof(KeyFrameStripService)
                    .GetField("helpers", Private)!.GetValue(parallel)!).Count);
                var targets = new[] { 0, 1.5, expected[Math.Min(4, expected.Length - 1)], expected[expected.Length / 2],
                    expected[^1] / 2, expected[^1], expected[^1] + 1 };
                foreach (var count in new[] { 9, 1, 18, 48 })
                foreach (var target in targets.Distinct())
                {
                    var progress = new List<KeyFrameStripRange>();
                    var actual = await ReadAsync(parallel, target, count, progress);
                    var reference = await ReadAsync(sequential, target, count, null);
                    var label = $"{Path.GetFileName(path)} @{target} x{count}";
                    var detail = $"{Describe(label, reference)}\n{Describe(label, actual)}";
                    Assert.True(reference.Frames.Select(frame => frame.Pts).SequenceEqual(actual.Frames.Select(frame => frame.Pts)), detail);
                    // 并行扫描可能多读到文件开头或结尾，比顺序扫描知道得多；标记为真时必须确实显示到第一张或最后一张。
                    Assert.True(actual.AtStart || !reference.AtStart, detail);
                    Assert.True(actual.AtEnd || !reference.AtEnd, detail);
                    Assert.True(!actual.AtStart || Math.Abs(actual.Frames[0].Timestamp.TotalSeconds - expected[0]) < 0.001, detail);
                    Assert.True(!actual.AtEnd || Math.Abs(actual.Frames[^1].Timestamp.TotalSeconds - expected[^1]) < 0.001, detail);
                    // 部分窗口可以较短，但必须是真实相邻的一段关键帧。
                    foreach (var partial in progress)
                    {
                        var indices = partial.Frames.Select(frame => Array.FindIndex(expected,
                            time => Math.Abs(time - frame.Timestamp.TotalSeconds) < 0.001)).ToArray();
                        Assert.DoesNotContain(-1, indices);
                        Assert.Equal(Enumerable.Range(indices[0], indices.Length), indices);
                    }
                    // 并行读取后，相邻关键帧导航从主实例的游标继续，结果与顺序扫描相同。
                    foreach (var direction in new[] { 1, -1 })
                    {
                        var anchor = direction > 0 ? actual.Frames[^1] : actual.Frames[0];
                        var next = await parallel.ReadNeighborAsync(anchor, direction, _ => false, CancellationToken.None);
                        var expectedNext = await sequential.ReadNeighborAsync(anchor, direction, _ => false, CancellationToken.None);
                        try { Assert.Equal(expectedNext?.Pts, next?.Pts); }
                        finally
                        {
                            next?.Bitmap?.Dispose();
                            expectedNext?.Bitmap?.Dispose();
                        }
                    }
                }
            }
            return true;
        }, CancellationToken.None);
    }

    /// <summary>
    /// 关键包过滤：跳过的关键包不解码，停止后返回 null；停止前已送入的关键帧可从解码器缓存中取出。
    /// </summary>
    [NativeMediaFact("keyframes")]
    public async Task KeyPacketFilterSkipsStopsAndDrains()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            foreach (var name in new[] { "fixed.mp4", "hevc-hlg.ts" })
            {
                var path = Path.Combine(NativeTestEnvironment.FixtureDirectory("keyframes"), name);
                using var video = new VideoInstance(path) { ThumbnailDecoding = true };
                await video.InitVideoAsync();
                await video.SeekBeforeTimeAsync(TimeSpan.Zero, CancellationToken.None);
                var all = new List<long>();
                while (await video.DecodeNextSearchFrameAsync(64, 36, CancellationToken.None) is { } result)
                {
                    all.Add(result.FramePts);
                    Dispose(result);
                }
                all.AddRange(await DrainAsync(video));
                Assert.True(all.Count >= 10, name);

                var stopAt = all[8];
                var decided = new List<long>();
                await video.SeekBeforeTimeAsync(TimeSpan.Zero, CancellationToken.None);
                var filtered = new List<long>();
                KeyPacketDecision Filter(long pts)
                {
                    decided.Add(pts);
                    if (pts >= stopAt)
                        return KeyPacketDecision.Stop;
                    return all.IndexOf(pts) % 2 == 0 ? KeyPacketDecision.Decode : KeyPacketDecision.Skip;
                }
                while (await video.DecodeNextSearchFrameAsync(64, 36, CancellationToken.None, Filter) is { } result)
                {
                    filtered.Add(result.FramePts);
                    Dispose(result);
                }
                filtered.AddRange(await DrainAsync(video));
                Assert.Equal(all.Take(8).Where((_, index) => index % 2 == 0), filtered.Order());
                Assert.Equal(stopAt, decided[^1]);
            }
            return true;
        }, CancellationToken.None);
    }

    /// <summary>打开中途在任意时刻取消后释放服务，所有解码实例（含辅助实例）都已关闭源文件。</summary>
    [NativeMediaFact("keyframes")]
    public async Task CancelledOpenReleasesEveryDecoder()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            var source = Path.Combine(NativeTestEnvironment.FixtureDirectory("keyframes"), "hevc-hlg.ts");
            var directory = Directory.CreateTempSubdirectory("tscutter-strip-");
            try
            {
                var copy = Path.Combine(directory.FullName, "source.ts");
                var moved = Path.Combine(directory.FullName, "moved.ts");
                File.Copy(source, copy);
                foreach (var delay in new[] { 0, 1, 3, 8, 15, 30, 60, 120 })
                {
                    var service = new KeyFrameStripService { ScanInstanceCount = 3 };
                    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(delay));
                    try { await service.OpenAsync(new KeyFrameStripSource(copy, null, false), cancellation.Token); }
                    catch (OperationCanceledException) { }
                    await service.DisposeAsync();
                    Assert.Empty((System.Collections.ICollection)typeof(KeyFrameStripService)
                        .GetField("helpers", Private)!.GetValue(service)!);
                    // Windows 上源文件仍被打开时无法移动。
                    File.Move(copy, moved);
                    File.Move(moved, copy);
                }
            }
            finally { directory.Delete(true); }
            return true;
        }, CancellationToken.None);
    }

    /// <summary>FFmpeg 自带的 AV1 解码器只能配合硬件加速；软解候选中它排在 libdav1d/libaom-av1 之后。</summary>
    [NativeMediaFact]
    public void HardwareOnlyAv1DecoderIsTriedLastForSoftwareDecoding()
    {
        var decoders = Codec.FindDecoders(AVCodecID.AV_CODEC_ID_AV1).Where(decoder => !decoder.Name.Contains('_')).ToList();
        Assert.All(decoders, decoder => Assert.Equal(decoder.Name == "av1", decoder.RequiresHardwareAcceleration));
        Assert.All(Codec.FindDecoders(AVCodecID.AV_CODEC_ID_H264), decoder => Assert.False(decoder.RequiresHardwareAcceleration));
        // 不打开文件，只检查候选顺序：原有的倒序中 FFmpeg 自带的 av1 排在最前，修正后必须排到最后。
        var video = new VideoInstance("unused.mp4");
        typeof(VideoInstance).GetField("softwareDecoders", Private)!.SetValue(video, decoders);
        var candidates = ((IEnumerable<Codec>)typeof(VideoInstance).GetProperty("SoftwareDecoderCandidates", Private)!
            .GetValue(video)!).Select(decoder => decoder.Name).ToList();
        Assert.Equal(decoders.Count, candidates.Count);
        if (decoders.Any(decoder => decoder.Name == "av1"))
            Assert.Equal("av1", candidates[^1]);
        if (decoders.Count > 1)
            Assert.NotEqual("av1", candidates[0]);
    }

    private static async Task<KeyFrameStripRange> ReadAsync(KeyFrameStripService service, double target, int count,
        List<KeyFrameStripRange>? progress)
    {
        var range = await service.ReadAsync(target, _ => false, frame => frame.Bitmap?.Dispose(), CancellationToken.None,
            progress is null ? null : progress.Add, frameCount: count);
        Assert.NotEmpty(range.Frames);
        return range;
    }

    private static string Describe(string label, KeyFrameStripRange range) =>
        $"{label} start={range.AtStart} end={range.AtEnd} [{string.Join(",", range.Frames.Select(frame => frame.Timestamp.TotalSeconds.ToString("F3")))}]";

    private static async Task<List<long>> DrainAsync(VideoInstance video)
    {
        var drained = new List<long>();
        foreach (var result in await video.DrainSearchFramesAsync(64, 36, CancellationToken.None))
        {
            drained.Add(result.FramePts);
            Dispose(result);
        }
        return drained;
    }

    private static void Dispose(DecodeResult result)
    {
        result.BitmapLease?.Dispose();
        if (result.BitmapLease is null)
            result.Bitmap?.Dispose();
        result.GpuFrame?.Dispose();
    }
}
