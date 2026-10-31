using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Classic.Avalonia.Theme;
using TSCutter.GUI.Controls;
using TSCutter.GUI.Services.Preview;
using TSCutter.GUI.Themes;
using TSCutter.GUI.ViewModels;
using TSCutter.GUI.ViewModels.Preview;
using Avalonia.VisualTree;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.Avalonia;
using Splat;
using System.Reflection;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.ViewModels.Application;
using TSCutter.GUI.Views;
using Xunit;

namespace TSCutter.GUI.Tests.UI;

[Collection("Avalonia UI")]
public sealed class KeyFrameStripTests
{
    [Fact]
    public async Task RetentionIndicatorsRespectExactBoundariesOverlapsAndUnknownFrames()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var strip = new KeyFrameStripControl { BorderBrush = Brushes.Gray, ClipBrush = Brushes.Green };
            var range = new KeyFrameRetentionRange(1, 3, 180_000, 360_000, false, "blue");
            // 用非零 PTS 起点和略有舍入偏差的时间，确认判断依据是原始 PTS。
            var first = new KeyFrameStripItem(180_000, TimeSpan.FromSeconds(0.9999999), null, false);
            var last = new KeyFrameStripItem(360_000, TimeSpan.FromSeconds(2.9999999), null, false);
            strip.Items = [first with { Pts = 179_999 }, first, last, last with { Pts = 360_001 }];
            strip.Measure(new Size(448, 80));
            strip.Arrange(new Rect(0, 0, 448, 80));
            var tiles = strip.GetTiles();
            strip.Ranges = [range];
            Assert.Same(ClipLabels.Brush("blue"), strip.GetRetentionBrush(first));
            Assert.Same(ClipLabels.Brush("blue"), strip.GetRetentionBrush(last));
            Assert.Same(Brushes.Gray, strip.GetRetentionBrush(first with { Pts = 179_999 }));
            Assert.Same(Brushes.Gray, strip.GetRetentionBrush(last with { Pts = 360_001 }));
            var markers = strip.GetBoundaryMarkers(tiles);
            Assert.Equal(2, markers.Count);
            Assert.Equal(tiles[1].Bounds.Left + 4, Assert.Single(markers, marker => marker.IsStart).Bounds.Left);
            Assert.Equal(tiles[2].Bounds.Right - 4, Assert.Single(markers, marker => !marker.IsStart).Bounds.Right);
            strip.Ranges = [range, range with { IsActive = true, Color = "red" }, range with { Color = "purple" }];
            Assert.Same(ClipLabels.Brush("red"), strip.GetRetentionBrush(first));
            strip.Ranges = [range with { End = double.PositiveInfinity, EndPts = null, Color = "" }];
            Assert.Same(Brushes.Green, strip.GetRetentionBrush(last with { Pts = 900_000, Timestamp = TimeSpan.FromSeconds(9) }));
            Assert.True(Assert.Single(strip.GetBoundaryMarkers(tiles)).IsStart);
            strip.Ranges = [range with { StartPts = 1, EndPts = 900_000 }];
            Assert.Empty(strip.GetBoundaryMarkers(tiles)); // 可见区边缘不是剪辑边界。
            // 旧项目没有 PTS 时仍按时间判断；未知帧不提前声称被保留或丢弃。
            strip.Ranges = [range with { StartPts = null, EndPts = null }];
            Assert.Same(Brushes.Gray, strip.GetRetentionBrush(first));
            Assert.Same(ClipLabels.Brush("blue"), strip.GetRetentionBrush(first with { Timestamp = TimeSpan.FromSeconds(1) }));
            Assert.Same(ClipLabels.Brush("blue"), strip.GetRetentionBrush(last with { Timestamp = TimeSpan.FromSeconds(3) }));
            Assert.Null(strip.GetRetentionBrush(first with { Pts = long.MinValue }));
            strip.Ranges = [];
            Assert.Same(Brushes.Gray, strip.GetRetentionBrush(first));
            strip.Ranges = [range];
            strip.Items = [last, last with { Pts = 360_001 }];
            var edgeTiles = strip.GetTiles();
            var edgeMarker = Assert.Single(strip.GetBoundaryMarkers(edgeTiles));
            Assert.False(edgeMarker.IsStart);
            Assert.True(edgeMarker.Bounds.Left >= edgeTiles[0].Bounds.Left);
            Assert.True(edgeMarker.Bounds.Right <= edgeTiles[0].Bounds.Right);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ContinuousMovementDebouncesAndReleaseRunsImmediately()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            var fake = new FakeService();
            var vm = new KeyFrameStripViewModel(() => fake);
            vm.SetSource(new("one.mp4", null, true));
            try
            {
                for (var i = 0; i < 5; i++)
                {
                    vm.RequestAt(i * 10);
                    await Task.Delay(35);
                }
                Assert.Empty(fake.Targets);
                vm.RequestAt(80, immediate: true);
                await vm.IdleTask;
                Assert.Equal(new double[] { 80 }, fake.Targets);
                Assert.Equal(80, Assert.Single(vm.Items, item => item.IsCurrent).Timestamp.TotalSeconds);
                Assert.All(vm.Items, item => Assert.Equal("CRA", item.KeyFrameType));
                var displayed = vm.Items;
                vm.RequestAt(81);
                Assert.Same(displayed, vm.Items); // 拖动命中已有缓存时也必须等待停住。
                vm.Cancel();
                await vm.IdleTask;
                vm.RequestAt(90);
                Assert.All(vm.Items, item => Assert.True(item.IsPlaceholder));
                Assert.Single(fake.Targets); // 拖动到未缓存位置先清空，停住后才取帧。
                await vm.IdleTask;
                Assert.Equal(new double[] { 80, 90 }, fake.Targets);
            }
            finally { vm.SetSource(null); await vm.IdleTask; }
            Assert.True(fake.Disposed);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ObsoleteResultsNeverPublishAndCacheRemainsBounded()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            var fake = new FakeService();
            var vm = new KeyFrameStripViewModel(() => fake);
            vm.SetSource(new("one.mp4", null, true));
            try
            {
                vm.RequestAt(10, true);
                await vm.IdleTask;
                vm.RequestAt(10.5, true);
                await vm.IdleTask;
                Assert.Single(fake.Targets); // 同一 GOP 内复用已完成的邻域。
                fake.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                vm.RequestAt(100, true);
                vm.RequestAt(200, true);
                Assert.All(vm.Items, item => Assert.True(item.IsPlaceholder));
                var published = new List<double>();
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(vm.Items) && vm.Items.Any(item => item.IsCurrent))
                        published.Add(vm.Items.Single(item => item.IsCurrent).Timestamp.TotalSeconds);
                };
                fake.Gate.SetResult();
                await vm.IdleTask;
                Assert.Equal(new double[] { 200 }, published.Distinct());
                for (var i = 3; i < 15; i++)
                {
                    vm.RequestAt(i * 100, true);
                    await vm.IdleTask;
                }
                Assert.InRange(vm.CachedCount, 9, 64);
                Assert.Equal(1, fake.MaximumConcurrency);
            }
            finally { vm.SetSource(null); await vm.IdleTask; }
            Assert.Empty(vm.Items);
            Assert.Equal(0, vm.CachedCount);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ChangingSourceDuringReadDisposesOldDecoderAndRejectsItsPictures()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            var old = new FakeService { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            var next = new FakeService();
            var services = new Queue<FakeService>([old, next]);
            var vm = new KeyFrameStripViewModel(() => services.Dequeue());
            vm.SetSource(new("old.mp4", null, true));
            vm.RequestAt(10, true);
            vm.SetSource(new("new.mp4", null, false));
            vm.RequestAt(50, true);
            old.Gate.SetResult();
            await vm.IdleTask;
            Assert.True(old.Disposed);
            Assert.False(next.Disposed);
            Assert.Equal(50, vm.Items.Single(item => item.IsCurrent).Timestamp.TotalSeconds);
            Assert.Equal(9, vm.CachedCount);
            vm.SetSource(null);
            await vm.IdleTask;
            Assert.True(next.Disposed);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task StripAdaptsToWindowWidthAndSeeksToTheClickedActualTimestamp()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            var vm = new KeyFrameStripViewModel(() => new FakeService());
            vm.SetSource(new("one.mp4", null, true));
            vm.RequestAt(10, true);
            await vm.IdleTask;
            var strip = new KeyFrameStripControl { Items = vm.Items };
            strip.SizeChanged += (_, _) => vm.SetVisibleCount(strip.VisibleCapacity);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(vm.Items))
                    strip.Items = vm.Items;
            };
            var window = new Window { Content = strip, Width = 1200, Height = 120 };
            KeyFrameStripItem? requested = null;
            strip.SeekRequested += (_, frame) => requested = frame;
            try
            {
                window.Show();
                foreach (var theme in new[] { ClassicTheme.Standard, CustomTheme.Windows10,
                             CustomTheme.Windows11, CustomTheme.Windows11Dark, CustomTheme.MacOS, CustomTheme.MacOSDark })
                {
                    Avalonia.Application.Current!.RequestedThemeVariant = theme;
                    Dispatcher.UIThread.RunJobs();
                    foreach (var width in new[] { 1200, 2200, 660, 3840, 300 })
                    {
                        window.Width = width;
                        Dispatcher.UIThread.RunJobs();
                        window.UpdateLayout();
                        await vm.IdleTask;
                        var tiles = strip.GetTiles();
                        Assert.Equal(strip.VisibleCapacity, tiles.Count);
                        Assert.Contains(tiles, tile => tile.Item.IsCurrent);
                        Assert.All(tiles, tile => Assert.True(tile.Bounds.Left >= 0 && tile.Bounds.Right <= strip.Bounds.Width));
                        var tile = tiles[0];
                        window.MouseDown(strip.TranslatePoint(tile.Bounds.Center, window)!.Value, Avalonia.Input.MouseButton.Left);
                        window.MouseUp(strip.TranslatePoint(tile.Bounds.Center, window)!.Value, Avalonia.Input.MouseButton.Left);
                        Assert.Same(tile.Item, requested);
                    }
                }
            }
            finally { window.Close(); vm.SetSource(null); await vm.IdleTask; }
            return true;
        }, CancellationToken.None);
    }

    [TSCutter.GUI.Tests.FFmpeg.NativeMediaFact("keyframes")]
    public async Task NativeStripUsesActualAdjacentKeyframesForMp4TsAndVariableGops()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            var directory = TSCutter.GUI.Tests.FFmpeg.NativeTestEnvironment.FixtureDirectory("keyframes");
            foreach (var path in Directory.EnumerateFiles(directory).Where(path => path.EndsWith(".mp4") || path.EndsWith(".ts")))
            {
                var expected = System.Text.Json.JsonSerializer.Deserialize<double[]>(File.ReadAllText(path + ".json"))!;
                await using var service = new KeyFrameStripService();
                await service.OpenAsync(new(path, null, true), CancellationToken.None);
                using var preview = new TSCutter.GUI.Models.Preview.VideoInstance(path, enableHardwareDecoding: true);
                await preview.InitVideoAsync();
                foreach (var target in new[] { 0, 1.5, expected[^1] / 2, expected[^1] + 1 })
                {
                    var pictures = new List<KeyFrameStripFrame>();
                    try
                    {
                        var progressCounts = new List<int>();
                        var range = await service.ReadAsync(target, _ => false, pictures.Add, CancellationToken.None,
                            partial => progressCounts.Add(partial.Frames.Count));
                        var times = range.Frames.Select(frame => frame.Timestamp.TotalSeconds).ToArray();
                        Assert.Equal(Math.Min(9, expected.Length), times.Length);
                        if (target == 0)
                        {
                            Assert.Equal(0, times[0]);
                            Assert.NotEmpty(progressCounts);
                            Assert.Equal(1, progressCounts[0]);
                            Assert.Equal(times.Length, progressCounts[^1]);
                        }
                        Assert.Equal(times.Order().Distinct(), times);
                        Assert.All(times, time => Assert.Contains(expected, item => Math.Abs(item - time) < 0.001));
                        var current = times.LastOrDefault(time => time <= target);
                        Assert.Equal(expected.LastOrDefault(time => time <= target), current, 3);
                        // 连续扫描结果必须对应真实相邻帧，不能跳过中间的关键帧。
                        var indices = times.Select(time => Array.FindIndex(expected, item => Math.Abs(item - time) < 0.001)).ToArray();
                        Assert.Equal(Enumerable.Range(indices[0], indices.Length), indices);
                        Assert.All(pictures, frame => Assert.NotNull(frame.Bitmap));
                        Assert.All(pictures, frame => Assert.False(string.IsNullOrEmpty(frame.KeyFrameType)));
                        foreach (var direction in new[] { 1, -1 })
                        {
                            var anchorFrame = direction > 0 ? range.Frames[^1] : range.Frames[0];
                            var anchorIndex = Array.FindIndex(expected,
                                time => Math.Abs(time - anchorFrame.Timestamp.TotalSeconds) < 0.001);
                            var neighbor = await service.ReadNeighborAsync(anchorFrame, direction, _ => false, CancellationToken.None);
                            try
                            {
                                var index = anchorIndex + direction;
                                if (index < 0 || index >= expected.Length)
                                    Assert.Null(neighbor);
                                else
                                {
                                    Assert.NotNull(neighbor);
                                    Assert.Equal(expected[index], neighbor.Timestamp.TotalSeconds, 3);
                                    Assert.NotNull(neighbor.Bitmap);
                                }
                            }
                            finally { neighbor?.Bitmap?.Dispose(); }
                        }

                        foreach (var frame in new[] { range.Frames[^1], range.Frames[0], range.Frames[range.Frames.Count / 2] })
                        {
                            var decoded = await preview.DecodeKeyFrameAtPtsAsync(frame.Pts);
                            Assert.NotNull(decoded);
                            try { Assert.Equal(frame.Pts, decoded.FramePts); }
                            finally
                            {
                                decoded.BitmapLease?.Dispose();
                                if (decoded.BitmapLease is null)
                                    decoded.Bitmap?.Dispose();
                                decoded.GpuFrame?.Dispose();
                            }
                        }
                    }
                    finally { foreach (var picture in pictures) picture.Bitmap?.Dispose(); }
                }
            }
            return true;
        }, CancellationToken.None);
    }

    [TSCutter.GUI.Tests.FFmpeg.NativeMediaFact("keyframes")]
    public async Task MainWindowKeepsStripFrozenDuringDraggingAndSupportsThemeAndVisibilityChanges()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var locale = new LocalizationService();
            locale.SwitchLanguage("zh-CN");
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
            var config = new MemoryConfiguration();
            var vm = new MainWindowViewModel(
                new DialogService(DispatchProxy.Create<IDialogManager, UnusedDialogs>()), config)
            {
                VideoPath = Path.Combine(TSCutter.GUI.Tests.FFmpeg.NativeTestEnvironment.FixtureDirectory("keyframes"), "fractional.ts")
            };
            var window = new MainWindow { DataContext = vm };
            try
            {
                var load = typeof(MainWindowViewModel).GetMethod("LoadVideoAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                Assert.True(await (Task<bool>)load.Invoke(vm, new object?[] { null })!);
                window.Show();
                await vm.KeyFrameStrip.IdleTask;
                var strip = window.GetVisualDescendants().OfType<KeyFrameStripControl>().Single();
                var timeline = window.FindControl<TimelineControl>("MainTimeline")!;
                Assert.Equal(strip.VisibleCapacity, strip.Items!.Count);
                Assert.Equal(0, strip.Items[0].Timestamp.TotalSeconds);
                Assert.Contains(strip.GetTiles(), tile => tile.Item.IsCurrent);
                if (Environment.GetEnvironmentVariable("TSCUTTER_KEYFRAME_STRIP_CAPTURE") is { Length: > 0 } initialOutput)
                {
                    Directory.CreateDirectory(initialOutput);
                    using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width * 2,
                        (int)window.Bounds.Height * 2), new Vector(192, 192));
                    bitmap.Render(window);
                    bitmap.Save(Path.Combine(initialOutput, "Initial.png"));
                }
                // 分数帧率、非零起始 PTS 的 TS：点击前后跳转均须命中同一个原始 PTS。
                foreach (var frame in new[] { strip.Items[0], strip.Items[5], strip.Items[1], strip.Items[3], strip.Items[3] })
                {
                    var tile = strip.GetTiles().Single(tile => tile.Item.Pts == frame.Pts);
                    var click = strip.TranslatePoint(tile.Bounds.Center, window)!.Value;
                    window.MouseDown(click, Avalonia.Input.MouseButton.Left);
                    window.MouseUp(click, Avalonia.Input.MouseButton.Left);
                    var seekDeadline = DateTime.UtcNow.AddSeconds(10);
                    while ((!timeline.IsEffectivelyEnabled || vm.IsDecoding) && DateTime.UtcNow < seekDeadline)
                        await Task.Delay(10);
                    await vm.KeyFrameStrip.IdleTask;
                    var actualPts = (long)typeof(MainWindowViewModel).GetField("_displayedFramePts",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
                    Assert.Equal(frame.Pts, actualPts);
                }
                var startFrame = strip.Items[1];
                var endFrame = strip.Items[4];
                var clip = new TSCutter.GUI.Models.Editing.PickedClip
                {
                    InFileInfo = new FileInfo(vm.VideoPath),
                    StartTime = startFrame.Timestamp.TotalSeconds, StartPts = startFrame.Pts, StartPosition = 188,
                    EndTime = endFrame.Timestamp.TotalSeconds, EndPts = endFrame.Pts, EndPosition = 376
                };
                vm.Clips.Add(clip);
                Assert.False(clip.IsSelected);
                Assert.Single(vm.KeyFrameStripRanges);
                vm.SelectClip(clip);
                Assert.True(Assert.Single(strip.Ranges!).Contains(endFrame.Pts, endFrame.Timestamp.TotalSeconds));
                vm.UpdateClipDetails(clip, "", "", "purple");
                Assert.Same(ClipLabels.Brush("purple"), strip.GetRetentionBrush(startFrame));
                Assert.Same(ClipLabels.Brush("purple"), strip.GetRetentionBrush(endFrame));
                vm.UndoCommand.Execute(null);
                Assert.Equal("", Assert.Single(strip.Ranges!).Color);
                var boundaries = (clip.StartTime, clip.StartPts, clip.StartPosition,
                    clip.EndTime, clip.EndPts, clip.EndPosition);
                try
                {
                    // 右侧图片和时间按钮共用这些命令，起点、终点都必须命中对应帧。
                    foreach (var isStart in new[] { false, true, false, true })
                    {
                        await (isStart ? vm.JumpToStartTimeCommand : vm.JumpToEndTimeCommand).ExecuteAsync(clip);
                        var actualPts = (long)typeof(MainWindowViewModel).GetField("_displayedFramePts",
                            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
                        Assert.Equal(isStart ? clip.StartPts : clip.EndPts, actualPts);
                        Assert.Same(clip, vm.SelectedClip);
                        Assert.Equal(boundaries, (clip.StartTime, clip.StartPts, clip.StartPosition,
                            clip.EndTime, clip.EndPts, clip.EndPosition));
                    }
                    // 未显式标记的文件尾不使用 EndPts 跳转。
                    clip.EndPosition = -1;
                    await vm.JumpToEndTimeCommand.ExecuteAsync(clip);
                    var unchangedPts = (long)typeof(MainWindowViewModel).GetField("_displayedFramePts",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
                    Assert.Equal(clip.StartPts, unchangedPts);
                }
                finally
                {
                    vm.Clips.Remove(clip);
                    vm.SelectedClip = null;
                    clip.Dispose();
                    await vm.KeyFrameStrip.IdleTask;
                }
                await vm.SeekToKeyFrameAndDrawFrameAsync(strip.Items[5].Pts);
                await vm.KeyFrameStrip.IdleTask;
                var beforeStep = strip.Items.ToArray();
                var expectedNext = beforeStep[5];
                var navigation = vm.NextGopClickCommand.ExecuteAsync(null);
                // 主预览尚未完成时，预览条已左移，右侧留出待填充的位置。
                Assert.Equal(expectedNext.Pts, strip.Items.Single(item => item.IsCurrent).Pts);
                Assert.Same(beforeStep[1].Bitmap, strip.Items[0].Bitmap);
                Assert.True(strip.Items[^1].IsPlaceholder);
                await navigation;
                await vm.KeyFrameStrip.IdleTask;
                var navigatedPts = (long)typeof(MainWindowViewModel).GetField("_displayedFramePts",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
                Assert.Equal(expectedNext.Pts, navigatedPts);
                Assert.DoesNotContain(strip.Items, item => item.IsPlaceholder);
                var original = strip.Items;
                var point = timeline.TranslatePoint(new Point(timeline.Bounds.Width * 0.2, 12), window)!.Value;
                window.MouseDown(point, Avalonia.Input.MouseButton.Left);
                for (var i = 0; i < 8; i++)
                {
                    window.MouseMove(timeline.TranslatePoint(new Point(timeline.Bounds.Width * (0.3 + i * 0.05), 12), window)!.Value);
                    await Task.Delay(30);
                    // 未缓存的位置允许清空；持续拖动期间不应发布新缩略图。
                    Assert.True(ReferenceEquals(original, strip.Items) || strip.Items!.All(item => item.IsPlaceholder));
                }
                window.MouseUp(timeline.TranslatePoint(new Point(timeline.Bounds.Width * 0.65, 12), window)!.Value,
                    Avalonia.Input.MouseButton.Left);
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while ((!timeline.IsEffectivelyEnabled || vm.IsDecoding) && DateTime.UtcNow < deadline)
                    await Task.Delay(10);
                await vm.KeyFrameStrip.IdleTask;
                Assert.True(timeline.IsEffectivelyEnabled);
                Assert.NotSame(original, strip.Items);
                Assert.InRange(Math.Abs(vm.KeyFrameStrip.Items.Single(item => item.IsCurrent).Timestamp.TotalSeconds - vm.CurrentTime), 0, 2.01);
                foreach (var theme in new[] { ClassicTheme.Standard, CustomTheme.Windows10, CustomTheme.Windows11,
                             CustomTheme.Windows11Dark, CustomTheme.MacOS, CustomTheme.MacOSDark })
                {
                    Avalonia.Application.Current!.RequestedThemeVariant = theme;
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Assert.True(strip.IsEffectivelyVisible);
                    Assert.Equal(80, strip.Bounds.Height);
                    Assert.True(timeline.Bounds.Height > 0);
                    Assert.True(window.FindControl<ImageViewer>("ImageViewer")!.Bounds.Height >= 300);
                    var stripBottom = strip.TranslatePoint(new Point(0, strip.Bounds.Height), window)!.Value.Y;
                    Assert.True(stripBottom <= timeline.TranslatePoint(default, window)!.Value.Y + 1);
                    if (Environment.GetEnvironmentVariable("TSCUTTER_KEYFRAME_STRIP_CAPTURE") is { Length: > 0 } output)
                    {
                        Directory.CreateDirectory(output);
                        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width * 2,
                            (int)window.Bounds.Height * 2), new Vector(192, 192));
                        bitmap.Render(window);
                        bitmap.Save(Path.Combine(output, theme.Key + ".png"));
                    }
                }
                var settings = new SettingsWindowViewModel(config, locale) { ShowKeyFrameStrip = false };
                settings.ApplyCommand.Execute(null);
                Assert.False(vm.ShowKeyFrameStrip);
                await vm.KeyFrameStrip.IdleTask;
                Dispatcher.UIThread.RunJobs();
                Assert.False(strip.IsVisible);
                Assert.Empty(vm.KeyFrameStrip.Items);
                Assert.False(config.CurrentConfig.ShowKeyFrameStrip);
                vm.RequestKeyFrameStrip(0, immediate: true);
                await vm.KeyFrameStrip.IdleTask;
                Assert.Empty(vm.KeyFrameStrip.Items);
                vm.ShowKeyFrameStrip = true;
                await vm.KeyFrameStrip.IdleTask;
                Assert.NotEmpty(vm.KeyFrameStrip.Items);
                Assert.Equal(2, config.Saves);
            }
            finally { window.Close(); vm.Close(); await vm.KeyFrameStrip.IdleTask; }
            return true;
        }, CancellationToken.None);
    }

    private sealed class MemoryConfiguration : IConfigurationService
    {
        public AppConfig CurrentConfig { get; } = new() { PreferHardwareDecoding = false };
        public int Saves { get; private set; }
        public void Load() { }
        public void Save() => Saves++;
        public void ApplyTheme(string theme) { }
        public void ApplyLanguage(string language) { }
    }

    public class UnusedDialogs : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => null;
    }

    [Fact]
    public async Task StepMovesCachedPicturesImmediatelyAndOnlyFillsTheMissingEdge()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            var fake = new FakeService();
            var vm = new KeyFrameStripViewModel(() => fake);
            vm.SetSource(new("one.mp4", null, true));
            vm.RequestAt(10, true);
            await vm.IdleTask;
            var original = vm.Items.ToArray();
            fake.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                Assert.True(vm.PreviewStep(1));
                Assert.Equal(11, vm.Items.Single(item => item.IsCurrent).Timestamp.TotalSeconds);
                Assert.True(vm.Items[^1].IsPlaceholder);
                for (var index = 0; index < 8; index++)
                    Assert.Same(original[index + 1].Bitmap, vm.Items[index].Bitmap);
                await Task.Delay(250);
                Assert.Single(fake.NeighborRequests);
                Assert.Single(fake.Targets);
                fake.Gate.SetResult();
                await vm.IdleTask;
                Assert.DoesNotContain(vm.Items, item => item.IsPlaceholder);
                Assert.Equal(15, vm.Items[^1].Pts);
                Assert.True(vm.PreviewStep(-1));
                await vm.IdleTask;
                Assert.Equal(10, vm.Items.Single(item => item.IsCurrent).Timestamp.TotalSeconds);
                Assert.Single(fake.NeighborRequests); // 向回走完全复用缓存，不再次取帧。
                Assert.Single(fake.Targets);
                vm.RequestAt(14.5, immediate: true);
                Assert.Equal(14, vm.Items.Single(item => item.IsCurrent).Pts);
                await vm.IdleTask;
                Assert.Single(fake.Targets); // 复用增量加载后确认的相邻帧区间，避免重新扫整组。
            }
            finally { fake.Gate.TrySetResult(); vm.SetSource(null); await vm.IdleTask; }
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task RepeatedStepsAndCanceledEdgeReadsKeepTheLatestWindow()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            var fake = new FakeService();
            var vm = new KeyFrameStripViewModel(() => fake);
            vm.SetSource(new("one.mp4", null, true));
            vm.RequestAt(10, true);
            await vm.IdleTask;
            fake.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                Assert.True(vm.PreviewStep(1));
                await Task.Delay(250);
                Assert.True(vm.PreviewStep(1));
                Assert.Equal(12, vm.Items.Single(item => item.IsCurrent).Timestamp.TotalSeconds);
                Assert.Equal(2, vm.Items.Count(item => item.IsPlaceholder));
                fake.Gate.SetResult();
                await vm.IdleTask;
                Assert.Equal(12, vm.Items.Single(item => item.IsCurrent).Timestamp.TotalSeconds);
                Assert.Equal(Enumerable.Range(8, 9).Select(pts => (long)pts), vm.Items.Select(item => item.Pts));
                Assert.Single(fake.Targets);
                Assert.Equal(2, fake.NeighborRequests.Count);
                Assert.Equal(1, fake.MaximumConcurrency);
                fake.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                for (var index = 0; index < 5; index++)
                    Assert.True(vm.PreviewStep(1));
                Assert.True(vm.Items.Single(item => item.IsCurrent).IsPlaceholder);
                Assert.Equal(9, vm.Items.Count);
                vm.RequestAt(17, immediate: true, currentPts: 17);
                fake.Gate.SetResult();
                await vm.IdleTask;
                Assert.Equal(17, vm.Items.Single(item => item.IsCurrent).Pts);
                Assert.DoesNotContain(vm.Items, item => item.IsPlaceholder);
            }
            finally { fake.Gate.TrySetResult(); vm.SetSource(null); await vm.IdleTask; }
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task InitialFramesFillProgressivelyAndUncachedJumpsClearImmediately()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            var fake = new FakeService { PauseAfterFirst = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            var vm = new KeyFrameStripViewModel(() => fake);
            vm.SetSource(new("one.mp4", null, true));
            try
            {
                vm.RequestAt(0, immediate: true);
                await fake.FirstFrameReady.Task;
                Assert.Equal(0, vm.Items[0].Pts);
                Assert.NotNull(vm.Items[0].Bitmap);
                Assert.Equal("CRA", vm.Items[0].KeyFrameType);
                Assert.All(vm.Items.Skip(1), item => Assert.True(item.IsPlaceholder));
                var partiallyLoaded = vm.Items;
                vm.RequestAt(0, immediate: true, currentPts: 0);
                Assert.Same(partiallyLoaded, vm.Items);
                Assert.Single(fake.Targets); // 主预览确认同一位置不能重新清空或解码。

                var published = new List<long>();
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(vm.Items))
                        published.AddRange(vm.Items.Where(item => !item.IsPlaceholder).Select(item => item.Pts));
                };
                vm.RequestAt(100, immediate: true);
                Assert.Equal(9, vm.Items.Count);
                Assert.All(vm.Items, item => Assert.True(item.IsPlaceholder));
                fake.PauseAfterFirst.SetResult();
                await vm.IdleTask;
                Assert.DoesNotContain(published, pts => pts < 96);
                Assert.Equal(100, vm.Items.Single(item => item.IsCurrent).Pts);
                Assert.DoesNotContain(vm.Items, item => item.IsPlaceholder);

                vm.RequestAt(0, immediate: true);
                Assert.Equal(0, vm.Items[0].Pts);
                await vm.IdleTask;
                Assert.Equal(new double[] { 0, 100 }, fake.Targets); // 返回已缓存位置时仍直接复用。
                // 非常接近的秒数仍可能对应不同的目标帧，不能把精确确认合并到旧目标。
                fake.PauseAfterFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
                vm.RequestAt(9.99999995, immediate: true);
                vm.RequestAt(10, immediate: true, currentPts: 10);
                fake.PauseAfterFirst.SetResult();
                await vm.IdleTask;
                Assert.Equal(10, vm.Items.Single(item => item.IsCurrent).Pts);
            }
            finally
            {
                fake.PauseAfterFirst.TrySetResult();
                vm.SetSource(null);
                await vm.IdleTask;
            }
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PlaybackSuspensionShowsOnlyPlaceholdersUntilResumed()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            var fake = new FakeService { PauseAfterFirst = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            var vm = new KeyFrameStripViewModel(() => fake);
            vm.SetSource(new("one.mp4", null, true));
            try
            {
                vm.RequestAt(0, immediate: true);
                await fake.FirstFrameReady.Task;
                var firstBitmap = vm.Items[0].Bitmap;
                vm.SetSuspended(true);
                fake.PauseAfterFirst.SetResult();
                await vm.IdleTask;
                Assert.Equal(vm.VisibleCount, vm.Items.Count);
                Assert.All(vm.Items, item => Assert.True(item.IsPlaceholder));
                vm.SetVisibleCount(12);
                vm.RequestAt(100, immediate: true);
                await vm.IdleTask;
                Assert.Equal(12, vm.Items.Count);
                Assert.All(vm.Items, item => Assert.True(item.IsPlaceholder));
                Assert.Single(fake.Targets);
                vm.SetSuspended(false);
                vm.RequestAt(0, immediate: true);
                await vm.IdleTask;
                Assert.DoesNotContain(vm.Items, item => item.IsPlaceholder);
                Assert.Same(firstBitmap, vm.Items[0].Bitmap);
            }
            finally { fake.PauseAfterFirst.TrySetResult(); vm.SetSource(null); await vm.IdleTask; }
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ResizingReusesPicturesAndDebouncesAdditionalDecoding()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            var fake = new FakeService();
            var vm = new KeyFrameStripViewModel(() => fake);
            vm.SetSource(new("one.mp4", null, true));
            vm.RequestAt(100, true);
            await vm.IdleTask;
            var original = vm.Items.ToDictionary(item => item.Pts, item => item.Bitmap);
            try
            {
                vm.SetVisibleCount(18);
                Assert.Equal(18, vm.Items.Count);
                Assert.Equal(9, vm.Items.Count(item => item.IsPlaceholder));
                foreach (var item in vm.Items.Where(item => !item.IsPlaceholder))
                    Assert.Same(original[item.Pts], item.Bitmap);
                await vm.IdleTask;
                Assert.Equal(18, vm.Items.Count);
                Assert.DoesNotContain(vm.Items, item => item.IsPlaceholder);
                Assert.Equal(9, fake.NeighborRequests.Count);
                Assert.Single(fake.Targets);
                vm.SetVisibleCount(6);
                await vm.IdleTask;
                Assert.Equal(6, vm.Items.Count);
                Assert.Equal(9, fake.NeighborRequests.Count);
                foreach (var count in new[] { 19, 22, 24 })
                {
                    vm.SetVisibleCount(count);
                    await Task.Delay(35);
                }
                Assert.Equal(9, fake.NeighborRequests.Count);
                await vm.IdleTask;
                Assert.Equal(24, vm.Items.Count);
                Assert.DoesNotContain(vm.Items, item => item.IsPlaceholder);
                Assert.Equal(100, vm.Items.Single(item => item.IsCurrent).Pts);
                Assert.Equal(15, fake.NeighborRequests.Count);
                Assert.Single(fake.Targets);
                Assert.Equal(1, fake.MaximumConcurrency);
                fake.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                vm.SetVisibleCount(48);
                await Task.Delay(250);
                vm.SetVisibleCount(12);
                fake.Gate.SetResult();
                await vm.IdleTask;
                Assert.Equal(12, vm.Items.Count);
                Assert.DoesNotContain(vm.Items, item => item.IsPlaceholder);
                Assert.Equal(100, vm.Items.Single(item => item.IsCurrent).Pts);
                vm.SetVisibleCount(1000);
                await vm.IdleTask;
                Assert.Equal(KeyFrameStripViewModel.MaximumVisibleCount, vm.Items.Count);
                Assert.InRange(vm.CachedCount, vm.Items.Count, 64);
            }
            finally { fake.Gate?.TrySetResult(); vm.SetSource(null); await vm.IdleTask; }
            return true;
        }, CancellationToken.None);
    }

    [TSCutter.GUI.Tests.FFmpeg.NativeMediaFact("keyframes")]
    public async Task StripFillsRequestedCountsAtStartMiddleAndEnd()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            var directory = TSCutter.GUI.Tests.FFmpeg.NativeTestEnvironment.FixtureDirectory("keyframes");
            foreach (var name in new[] { "variable.mp4", "hevc-hlg.ts", "single.mp4" })
            {
                var path = Path.Combine(directory, name);
                var expected = System.Text.Json.JsonSerializer.Deserialize<double[]>(File.ReadAllText(path + ".json"))!;
                await using var service = new KeyFrameStripService();
                await service.OpenAsync(new(path, null, true), CancellationToken.None);
                foreach (var count in new[] { 1, 18, 48 })
                foreach (var target in new[] { 0, expected[^1] / 2, expected[^1] + 1 })
                {
                    var pictures = new List<KeyFrameStripFrame>();
                    try
                    {
                        var range = await service.ReadAsync(target, _ => false, pictures.Add,
                            CancellationToken.None, frameCount: count);
                        Assert.Equal(Math.Min(count, expected.Length), range.Frames.Count);
                        var times = range.Frames.Select(frame => frame.Timestamp.TotalSeconds).ToArray();
                        Assert.Equal(expected.Last(time => time <= target), times.Last(time => time <= target), 3);
                        var first = Array.FindIndex(expected, time => Math.Abs(time - times[0]) < 0.001);
                        Assert.Equal(expected.Skip(first).Take(times.Length).Select(time => Math.Round(time, 3)),
                            times.Select(time => Math.Round(time, 3)));
                    }
                    finally { foreach (var frame in pictures) frame.Bitmap?.Dispose(); }
                }
            }
            return true;
        }, CancellationToken.None);
    }

    private sealed class FakeService : IKeyFrameStripService
    {
        public List<double> Targets { get; } = [];
        public List<(long Pts, int Direction)> NeighborRequests { get; } = [];
        public TaskCompletionSource? Gate { get; set; }
        public TaskCompletionSource? PauseAfterFirst { get; set; }
        public TaskCompletionSource FirstFrameReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public int MaximumConcurrency { get; private set; }
        private int concurrency;
        public Task OpenAsync(KeyFrameStripSource source, CancellationToken token) => Task.CompletedTask;
        public async Task<KeyFrameStripRange> ReadAsync(double target, Func<long, bool> isCached,
            Action<KeyFrameStripFrame> received, CancellationToken token, Action<KeyFrameStripRange>? progress = null, int frameCount = 9)
        {
            Targets.Add(target);
            MaximumConcurrency = Math.Max(MaximumConcurrency, ++concurrency);
            try
            {
                // 故意忽略取消，以验证迟到的解码结果也无法覆盖新目标或新文件。
                if (Gate is not null)
                    await Gate.Task;
                var start = Math.Max(0, (int)target - (frameCount - 1) / 2);
                var frames = Enumerable.Range(start, frameCount).Select(time =>
                    new KeyFrameStripFrame(time, TimeSpan.FromSeconds(time), isCached(time) ? null :
                        new WriteableBitmap(new PixelSize(32, 18), new Vector(96, 96),
                            PixelFormat.Bgra8888, AlphaFormat.Opaque), "CRA")).ToArray();
                var partial = new List<KeyFrameStripFrame>();
                foreach (var frame in frames)
                {
                    received(frame);
                    partial.Add(frame with { Bitmap = null });
                    progress?.Invoke(new(partial.ToArray(), start == 0, false));
                    if (partial.Count == 1 && PauseAfterFirst is { } pause)
                    {
                        FirstFrameReady.TrySetResult();
                        await pause.Task;
                    }
                }
                return new(frames.Select(frame => frame with { Bitmap = null }).ToArray(), start == 0, false);
            }
            finally { concurrency--; }
        }
        public async Task<KeyFrameStripFrame?> ReadNeighborAsync(KeyFrameStripFrame anchor, int direction,
            Func<long, bool> isCached, CancellationToken token)
        {
            NeighborRequests.Add((anchor.Pts, direction));
            MaximumConcurrency = Math.Max(MaximumConcurrency, ++concurrency);
            try
            {
                if (Gate is not null)
                    await Gate.Task;
                var pts = anchor.Pts + direction;
                if (pts < 0)
                    return null;
                return new(pts, TimeSpan.FromSeconds(pts), isCached(pts) ? null :
                    new WriteableBitmap(new PixelSize(32, 18), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque), "CRA");
            }
            finally { concurrency--; }
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
