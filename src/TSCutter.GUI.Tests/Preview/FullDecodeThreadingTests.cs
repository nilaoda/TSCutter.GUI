using System.Reflection;
using System.Security.Cryptography;
using Avalonia.Headless;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Services.Preview;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Tests.UI;
using Xunit;

namespace TSCutter.GUI.Tests.Preview;

/// <summary>
/// 完整解码（智能剪辑导航条的逐帧窗口）改用限定线程数的多线程软解码器：与单线程解码器得到的画面完全相同
/// （PTS、画面类型、像素逐字节一致），关键帧解码器仍是单线程。覆盖 H.264 B 帧、HEVC 开放 GOP（CRA + RASL）与 MPEG-2。
/// </summary>
[Collection("Avalonia UI")]
public sealed class FullDecodeThreadingTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [NativeMediaTheory("frameaccurate")]
    [InlineData("h264.ts", new[] { 2.3, 5.6, 11.8 })]
    [InlineData("hevc-cra.ts", new[] { 1.9, 4.95, 7.6 })]
    [InlineData("mpeg2.ts", new[] { 3.1, 6.5 })]
    public async Task ThreadedFullDecodingMatchesSingleThread(string name, double[] targets)
    {
        var source = Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), name);
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await headless.Dispatch<bool>(async () =>
        {
            var results = new List<List<string>>();
            foreach (var threaded in new[] { false, true })
            {
                await using var service = new KeyFrameStripService();
                await service.OpenAsync(new KeyFrameStripSource(source, null, false, EveryFrame: true), CancellationToken.None);
                var video = (VideoInstance)typeof(KeyFrameStripService).GetField("decoder", Private)!.GetValue(service)!;
                if (!threaded)
                    typeof(VideoInstance).GetField("fullDecoderUnavailable", Private)!.SetValue(video, true);
                var frames = new List<string>();
                foreach (var target in targets)
                    await service.ReadAsync(target, _ => false, frame =>
                    {
                        using var png = new MemoryStream();
                        frame.Bitmap!.Save(png);
                        frames.Add($"{frame.Pts}/{frame.KeyFrameType}/{Convert.ToHexString(SHA256.HashData(png.ToArray()))}");
                        frame.Bitmap.Dispose();
                    }, CancellationToken.None, frameCount: 9);
                var fullDecoder = typeof(VideoInstance).GetField("fullDecoder", Private)!.GetValue(video);
                Assert.Equal(threaded, fullDecoder is not null);
                results.Add(frames);
            }
            Assert.Equal(targets.Length * 9, results[0].Count);
            Assert.Equal(results[0], results[1]);
            return true;
        }, CancellationToken.None);
    }
}
