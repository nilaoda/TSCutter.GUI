using System.Reflection;
using Avalonia.Headless;
using Avalonia.Threading;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.Avalonia;
using Splat;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Tests.Editing;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Tests.UI;
using TSCutter.GUI.Utils.TransportStream.Packets;
using TSCutter.GUI.ViewModels;
using TSCutter.GUI.ViewModels.Preview;
using Xunit;

namespace TSCutter.GUI.Tests.Preview;

/// <summary>
/// 时钟向前跳变的 TS：按时间往前退会落在跳变的空白里，逐帧、关键帧导航和导航条仍要取到跳变之前的画面。
/// 素材由 12 秒的合成 TS（约每秒一个关键帧，帧率 29.97）在第 6 秒的关键帧处改写时间戳得到。
/// </summary>
[Collection("Avalonia UI")]
public sealed class ClockJumpNavigationTests
{
    private const double JumpSeconds = 20000;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [NativeMediaFact("frameaccurate")]
    public async Task SwitchingCutModesOnTheFirstFrameAfterAJumpKeepsTheFramesBeforeTheJump()
    {
        using var sample = ClockJumpSample.Create(JumpSeconds);
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(new LocalizationService());
            var config = new MemoryConfiguration();
            config.CurrentConfig.ClipCutMode = ClipCutMode.KeyFrame;
            var vm = new MainWindowViewModel(
                new DialogService(DispatchProxy.Create<IDialogManager, ExportDialogManager>()), config) { VideoPath = sample.Path };
            try
            {
                Assert.True(await (Task<bool>)typeof(MainWindowViewModel).GetMethod("LoadVideoAsync", Private)!
                    .Invoke(vm, [null])!);
                // 关键帧模式停在跳变后的第一个关键帧，再切换到智能剪辑：导航条改为逐帧，以这一帧为中心。
                await vm.SeekToTimeAndDrawFrameAsync(TimeSpan.FromSeconds(JumpSeconds + 7.5));
                await Settle();
                while (vm.CurrentTime > JumpSeconds + 6.01)
                {
                    var previous = vm.CurrentTime;
                    await vm.DrawNextFrameAsync(-1);
                    await Settle();
                    Assert.True(vm.CurrentTime < previous, $"{vm.CurrentTime}");
                }
                Assert.True(vm.CurrentTime is >= JumpSeconds + 6 and <= JumpSeconds + 6.01, $"{vm.CurrentTime}");
                vm.IsFrameAccurateMode = true;
                await Settle();
                Assert.True(vm.UsesFrameAccurateCut);

                var items = await FilledStripAsync(vm);
                var current = Assert.Single(items, item => item.IsCurrent);
                Assert.Equal(CurrentPts(vm), current.Pts);
                // 当前帧左侧是跳变之前的最后几帧，每帧之间相差一帧的时长。
                var before = items.TakeWhile(item => !item.IsCurrent).Select(item => item.Timestamp.TotalSeconds).ToArray();
                Assert.Equal((items.Count - 1) / 2, before.Length);
                Assert.All(before, seconds => Assert.InRange(seconds, 5.8, 6));
                Assert.All(before.Zip(before.Skip(1)), pair => Assert.InRange(pair.Second - pair.First, 0.03, 0.04));

                // 波形随导航条出现：当前帧下方的一段已读到声音。
                var waveform = vm.KeyFrameStripWaveform;
                Assert.True(waveform.IsAvailable);
                var span = StripWaveformViewModel.GetSpan(items, items.ToList().IndexOf(current))!.Value;
                for (var attempt = 0; attempt < 100 && !waveform.TryGetPeak(span.Start, span.End, out _); attempt++)
                {
                    await waveform.IdleTask;
                    await Settle();
                }
                Assert.True(waveform.TryGetPeak(span.Start, span.End, out var peak));
                Assert.InRange(peak, 0.08, 0.1);

                // 再切回关键帧剪辑：导航条左侧是跳变之前的关键帧（约每秒一个），不是占位。
                vm.IsKeyFrameMode = true;
                await Settle();
                Assert.False(vm.UsesFrameAccurateCut);
                items = await FilledStripAsync(vm);
                current = Assert.Single(items, item => item.IsCurrent);
                Assert.InRange(current.Timestamp.TotalSeconds, JumpSeconds + 6, JumpSeconds + 6.01);
                before = items.TakeWhile(item => !item.IsCurrent).Select(item => item.Timestamp.TotalSeconds).ToArray();
                Assert.Equal((items.Count - 1) / 2, before.Length);
                Assert.InRange(before[^1], 5, 5.01);
                Assert.All(before.Zip(before.Skip(1)), pair => Assert.InRange(pair.Second - pair.First, 1, 1.01));

                // 上一个关键帧跨过跳变，落在跳变之前的最后一个关键帧。
                await vm.DrawNextFrameAsync(-1);
                await Settle();
                Assert.True(vm.CurrentTime is >= 5 and <= 5.01, $"{vm.CurrentTime}");
            }
            finally
            {
                vm.Close();
                await vm.KeyFrameStrip.IdleTask;
                await vm.KeyFrameStripWaveform.IdleTask;
                Dispatcher.UIThread.RunJobs();
            }
            return true;
        }, CancellationToken.None);
    }

    [NativeMediaFact("frameaccurate")]
    public async Task PreviousFrameStepsThroughAJumpOneFrameAtATime()
    {
        using var sample = ClockJumpSample.Create(JumpSeconds);
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            using var video = new VideoInstance(sample.Path);
            await video.InitVideoAsync();
            // 从跳变后约一秒处逐帧后退：以前在跳变后一秒之内就找不到上一帧，被当作片头。
            var frame = await video.DecodeAtTimeAsync(TimeSpan.FromSeconds(JumpSeconds + 7.5));
            var times = new List<double> { frame.FrameTimestamp.TotalSeconds };
            while (frame.FrameTimestamp.TotalSeconds > 5.5)
            {
                var previous = await video.DecodeAdjacentFrameAsync(frame.FramePts, -1);
                Assert.True(previous is not null, $"no previous frame at {frame.FrameTimestamp.TotalSeconds:F3}");
                frame = previous;
                times.Add(frame.FrameTimestamp.TotalSeconds);
            }
            var steps = times.Zip(times.Skip(1), (later, earlier) => later - earlier).ToArray();
            // 只有一步跨过跳变（从跳变后的第一帧回到跳变前的最后一帧），其余每步都是一帧。
            Assert.Single(steps, step => step > 1);
            Assert.All(steps.Where(step => step <= 1), step => Assert.InRange(step, 0.03, 0.04));
            var crossing = Array.FindIndex(steps, step => step > 1);
            Assert.InRange(times[crossing], JumpSeconds + 6, JumpSeconds + 6.01);
            Assert.InRange(times[crossing + 1], 5.9, 6);
            return true;
        }, CancellationToken.None);
    }

    /// <summary>
    /// 一次跳多个关键帧按估算间隔定位：从跳变后的第一帧向前跳，目标时间落在跳变的空白里，
    /// 应停在跳变之前的关键帧上，而不是反复落回跳变之后直到报解码失败；向后跳同样能跨过跳变。
    /// </summary>
    [NativeMediaTheory("frameaccurate")]
    [InlineData(10)]
    [InlineData(20)]
    public async Task JumpingSeveralKeyframesCrossesAJump(int count)
    {
        using var sample = ClockJumpSample.Create(JumpSeconds);
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            using var video = new VideoInstance(sample.Path);
            await video.InitVideoAsync();
            var frame = await video.DecodeAtTimeAsync(TimeSpan.FromSeconds(JumpSeconds + 7.5));
            while (frame.FrameTimestamp.TotalSeconds > JumpSeconds + 6.01)
            {
                Release(frame);
                frame = await video.DecodeNextFrameAsync(-1);
            }
            Assert.InRange(frame.FrameTimestamp.TotalSeconds, JumpSeconds + 6, JumpSeconds + 6.01);
            Release(frame);

            frame = await video.DecodeNextFrameAsync(-count);
            Assert.InRange(frame.FrameTimestamp.TotalSeconds, 0, 5.01);
            Release(frame);

            frame = await video.DecodeNextFrameAsync(count);
            Assert.True(frame.FrameTimestamp.TotalSeconds > JumpSeconds, $"{frame.FrameTimestamp.TotalSeconds}");
            Release(frame);
            return true;
        }, CancellationToken.None);
    }

    /// <summary>
    /// 正常素材不受影响：在所有合成的 TS/M2TS 素材上逐处检测，跳变检测都不成立，各处定位仍按原来的方式进行；
    /// 带跳变的素材上，跳变空白里的位置都指向跳变之前的最后一个关键包。
    /// </summary>
    [NativeMediaFact("frameaccurate")]
    public async Task GapDetectionOnlyTriggersInsideATimestampJump()
    {
        var find = typeof(VideoInstance).GetMethod("FindKeyPacketBeforeGap", Private)!;
        (long Pts, long Position)? Find(VideoInstance video, double seconds) =>
            ((long Pts, long Position)?)find.Invoke(video, [video.SecondsToPts(seconds), CancellationToken.None]);

        var sources = new[] { "keyframes", "frameaccurate", "remux", "subtitles" }
            .Select(NativeTestEnvironment.FixtureDirectory)
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory))
            .Where(path => Path.GetExtension(path) is ".ts" or ".m2ts")
            .ToArray();
        var checkedFiles = 0;
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            foreach (var source in sources)
            {
                using var video = new VideoInstance(source);
                try
                {
                    await video.InitVideoAsync();
                    Release(await video.DecodeAtTimeAsync(TimeSpan.Zero));
                }
                catch (Exception exception) when (exception is not Xunit.Sdk.XunitException)
                {
                    // 用于验证拒绝路径的素材打不开，不在检查范围内。
                    continue;
                }
                for (var seconds = 0.05; seconds < video.GetVideoDurationInSeconds(); seconds += 0.1)
                    Assert.True(Find(video, seconds) is null, $"{Path.GetFileName(source)} @ {seconds:F2}");
                checkedFiles++;
            }

            using var sample = ClockJumpSample.Create(JumpSeconds);
            using var jumped = new VideoInstance(sample.Path);
            await jumped.InitVideoAsync();
            Release(await jumped.DecodeAtTimeAsync(TimeSpan.Zero));
            // 跳变之前、之后的正常位置不检测为跳变；空白里的位置都指向跳变之前最后一个关键帧（5.005 秒）。
            foreach (var seconds in new[] { 1.0, 5.5, 5.95, JumpSeconds + 6.5, JumpSeconds + 10 })
                Assert.True(Find(jumped, seconds) is null, $"{seconds}");
            foreach (var seconds in new[] { 6.5, 100, JumpSeconds / 2, JumpSeconds + 5.9 })
            {
                var key = Find(jumped, seconds);
                Assert.True(key is not null, $"{seconds}");
                Assert.InRange(jumped.PtsToTimeSpan(key!.Value.Pts).TotalSeconds, 5, 5.01);
            }
            return true;
        }, CancellationToken.None);
        Assert.True(checkedFiles >= 10, $"only {checkedFiles} TS fixtures checked");
    }

    private static void Release(DecodeResult result)
    {
        result.BitmapLease?.Dispose();
        if (result.BitmapLease is null)
            result.Bitmap?.Dispose();
        result.GpuFrame?.Dispose();
    }

    /// <summary>等导航条填满（没有占位）后返回各格画面。</summary>
    private static async Task<IReadOnlyList<KeyFrameStripItem>> FilledStripAsync(MainWindowViewModel vm)
    {
        var strip = vm.KeyFrameStrip;
        for (var attempt = 0; attempt < 200 && (strip.Items.Count == 0 || strip.Items.Any(item => item.IsPlaceholder)); attempt++)
        {
            await strip.IdleTask;
            await Settle();
        }
        Assert.NotEmpty(strip.Items);
        Assert.DoesNotContain(strip.Items, item => item.IsPlaceholder);
        return strip.Items;
    }

    private static long CurrentPts(MainWindowViewModel vm) =>
        (long)typeof(MainWindowViewModel).GetProperty("CurrentPts", Private)!.GetValue(vm)!;

    private static async Task Settle()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    private sealed class MemoryConfiguration : IConfigurationService
    {
        public AppConfig CurrentConfig { get; } = new();
        public void Load() { }
        public void Save() { }
        public void ApplyTheme(string theme) { }
        public void ApplyLanguage(string language) { }
    }
}

/// <summary>
/// 在临时目录生成带时钟跳变的 TS：复制帧精确素材中的 12 秒 H.264 TS（440 Hz 正弦波，振幅 1/8），
/// 从视频 PID 上第一个不早于第 6 秒的随机访问点起，把之后所有包的 PCR、PTS、DTS 加上跳变量，
/// 不设置不连续标志，模拟拼接留下的时钟跳变。释放时删除临时目录。
/// </summary>
internal sealed class ClockJumpSample : IDisposable
{
    private readonly DirectoryInfo directory;

    private ClockJumpSample(DirectoryInfo directory, string path)
    {
        this.directory = directory;
        Path = path;
    }

    public string Path { get; }

    public static ClockJumpSample Create(double jumpSeconds, double afterSeconds = 6)
    {
        var directory = Directory.CreateTempSubdirectory("tscutter-clock-jump-");
        try
        {
            var path = System.IO.Path.Combine(directory.FullName, "jump.ts");
            Write(System.IO.Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), "h264.ts"), path,
                afterSeconds, jumpSeconds);
            return new ClockJumpSample(directory, path);
        }
        catch
        {
            directory.Delete(recursive: true);
            throw;
        }
    }

    public void Dispose() => directory.Delete(recursive: true);

    private static void Write(string sourcePath, string outputPath, double afterSeconds, double jumpSeconds)
    {
        const int packetSize = 188;
        var data = File.ReadAllBytes(sourcePath);
        Assert.Equal(0, data.Length % packetSize);
        var correction = (long)Math.Round(jumpSeconds * 90000);
        int? pcrPid = null;
        long? firstPts = null;
        var shifting = false;
        for (var offset = 0; offset < data.Length; offset += packetSize)
        {
            var packet = data.AsSpan(offset, packetSize);
            Assert.Equal(0x47, packet[0]);
            var pid = ((packet[1] & 0x1F) << 8) | packet[2];
            if (TsTimestampFieldCodec.TryReadPcr(packet, out _, out _, out _))
                pcrPid ??= pid;
            if (!shifting && pid == pcrPid &&
                TsTimestampFieldCodec.TryLocatePesTimestamps(packet, out var ptsOffset, out _))
            {
                var pts = TsTimestampFieldCodec.ReadPesTimestamp(packet.Slice(ptsOffset, 5));
                firstPts ??= pts;
                var randomAccess = (packet[3] & 0x20) != 0 && packet[4] > 0 && (packet[5] & 0x40) != 0;
                shifting = randomAccess && pts - firstPts >= afterSeconds * 90000;
            }
            if (!shifting)
                continue;
            TsTimestampFieldCodec.RewritePcr(packet, correction);
            TsTimestampFieldCodec.RewritePesTimestamps(packet, correction);
        }
        Assert.True(shifting, "源文件中没有找到跳变位置。");
        File.WriteAllBytes(outputPath, data);
    }
}
