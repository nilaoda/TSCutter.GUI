using System.Reflection;
using System.Text.Json;
using Avalonia.Headless;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Tests.UI;
using Xunit;

namespace TSCutter.GUI.Tests.Preview;

/// <summary>
/// IDR 与场景切换处的 CRA 混用、重排序深度较大的 HEVC：逐个跳到下一关键帧时，每个关键包都须独立解码。
/// 硬解若不在关键帧之间重置，会积压多帧才输出、CRA 引用没有送入的参考帧，跳到错误的画面或改为完整解码（之后每次跳转都很慢）。
/// 没有硬件解码时只检查软解。
/// </summary>
[Collection("Avalonia UI")]
public sealed class HardwareKeyFrameNavigationTests
{
    [NativeMediaFact("keyframes")]
    public async Task KeyFrameStepsVisitEveryIdrAndCraWithAndWithoutHardware()
    {
        var path = Path.Combine(NativeTestEnvironment.FixtureDirectory("keyframes"), "hevc-mixed-irap.ts");
        Assert.True(File.Exists(path), "缺少 hevc-mixed-irap.ts；请重新运行 python tools/prepare-test-media.py --group keyframes。");
        var expected = JsonSerializer.Deserialize<double[]>(File.ReadAllText(path + ".json"))!;
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            foreach (var hardware in new[] { false, true })
            {
                using var video = new VideoInstance(path, hardware);
                await video.InitVideoAsync();
                if (hardware && !video.IsHardwareDecoding)
                    break;
                var first = await video.DecodeAtTimeAsync(TimeSpan.Zero);
                var shown = new List<double> { first.FrameTimestamp.TotalSeconds };
                Dispose(first);
                while (shown.Count < expected.Length)
                {
                    var result = await video.DecodeNextFrameAsync();
                    shown.Add(result.FrameTimestamp.TotalSeconds);
                    Dispose(result);
                }
                Assert.Equal(expected, shown.Select(time => Math.Round(time, 3)));
                // 硬解全程只解关键包，没有因为输出积压而改为完整解码。
                Assert.False((bool)typeof(VideoInstance).GetField("useFullPacketDecoding",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(video)!);
                Assert.Equal(hardware, video.IsHardwareDecoding);
            }
            return true;
        }, CancellationToken.None);
    }

    private static void Dispose(DecodeResult result)
    {
        result.BitmapLease?.Dispose();
        if (result.BitmapLease is null)
            result.Bitmap?.Dispose();
        result.GpuFrame?.Dispose();
    }
}
