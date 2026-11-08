using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Classic.Avalonia.Theme;
using HanumanInstitute.MvvmDialogs;
using Splat;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Tests.Player;
using TSCutter.GUI.Tests.UI;
using TSCutter.GUI.Themes;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.ViewModels;
using TSCutter.GUI.Views;
using Xunit;

namespace TSCutter.GUI.Tests.Editing;

/// <summary>主界面帧精确流程：选择模式 → 逐帧定位 → 标记非关键帧起止点 → 按任务快照导出。</summary>
[Collection("Avalonia UI")]
public sealed class FrameAccurateWorkflowTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private sealed class MemoryConfiguration : IConfigurationService
    {
        public AppConfig CurrentConfig { get; } = new() { PreferHardwareDecoding = false };
        public void Load() { }
        public void Save() { }
        public void ApplyTheme(string theme) { }
        public void ApplyLanguage(string language) { }
    }

    /// <summary>
    /// 工具栏的帧精确开关（真实点击）：按钮的按下状态始终与实际模式一致——
    /// 开、关各点一次随之切换；素材不支持时按钮禁用（悬停仍显示原因），点击无效且不改动偏好；
    /// 偏好为开启时旁边显示警告图标，说明原因并提示改用关键帧剪辑，偏好关闭时不显示。
    /// </summary>
    [NativeMediaFact("frameaccurate")]
    public async Task FrameAccurateToggleFollowsTheActualMode()
    {
        var source = Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), "h264-444.mp4");
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await headless.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var localization = new LocalizationService();
            localization.SwitchLanguage("zh-CN");
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(localization);
            var configuration = new MemoryConfiguration();
            var vm = new MainWindowViewModel(new HanumanInstitute.MvvmDialogs.Avalonia.DialogService(
                System.Reflection.DispatchProxy.Create<IDialogManager, MainWindowPlaybackTests.UnusedDialogs>()), configuration);
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                var toggle = FrameAccurateToggle(window);
                await Settle();
                Assert.False(toggle.IsChecked);
                // 关闭时只说明关键帧剪辑与开启后的效果，不附开启状态的规则。
                Assert.Equal(LocalizationManager.Instance.String_CutMode_FrameAccurateOff, vm.FrameAccurateToolTip);
                await Click(window, toggle);
                Assert.True(vm.IsFrameAccurateMode);
                Assert.True(toggle.IsChecked);
                Assert.Equal(LocalizationManager.Instance.String_CutMode_FrameAccurateOn + Environment.NewLine
                    + LocalizationManager.Instance.String_CutMode_FrameAccurateTip, vm.FrameAccurateToolTip);
                await Click(window, toggle);
                Assert.False(vm.IsFrameAccurateMode);
                Assert.False(toggle.IsChecked);
                var warning = window.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "FrameAccurateWarning");
                Assert.False(warning.IsVisible);
                await Click(window, toggle);
                Assert.True(vm.IsFrameAccurateMode);

                // 不支持的素材：按钮禁用并弹起，点击无效，偏好仍为开启；警告图标说明原因与改用的方式。
                vm.VideoPath = source;
                Assert.True(await (Task<bool>)typeof(MainWindowViewModel).GetMethod("LoadVideoAsync", Private)!
                    .Invoke(vm, [null])!);
                await Settle();
                Assert.False(toggle.IsEffectivelyEnabled);
                Assert.True(ToolTip.GetShowOnDisabled(toggle));
                Assert.False(toggle.IsChecked);
                Assert.Contains(LocalizationManager.Instance.String_CutMode_Reason_Codec, vm.FrameAccurateToolTip);
                Assert.True(warning.IsEffectivelyVisible);
                var tip = Assert.IsType<string>(ToolTip.GetTip(warning));
                Assert.Contains(LocalizationManager.Instance.String_CutMode_Reason_Codec, tip);
                Assert.Contains(LocalizationManager.Instance.String_CutMode_FallbackKeyFrame, tip);
                var origin = warning.TranslatePoint(default, window)!.Value;
                var toggleOrigin = toggle.TranslatePoint(default, window)!.Value;
                Assert.InRange(toggleOrigin.X - (origin.X + warning.Bounds.Width), -0.5, 8);
                await Click(window, toggle);
                Assert.False(vm.IsFrameAccurateMode);
                Assert.False(toggle.IsChecked);
                Assert.Equal(ClipCutMode.FrameAccurate, configuration.CurrentConfig.ClipCutMode);

                // 偏好改为关闭（如从设置或工程恢复）：按钮仍禁用，但不再显示警告图标。
                await (Task)typeof(MainWindowViewModel).GetMethod("ChangeCutModeAsync", Private)!
                    .Invoke(vm, [ClipCutMode.KeyFrame])!;
                await Settle();
                Assert.False(warning.IsVisible);
                Assert.False(toggle.IsEffectivelyEnabled);
            }
            finally
            {
                window.Close();
                typeof(MainWindowViewModel).GetMethod("Close")?.Invoke(vm, null);
            }
            return true;
        }, CancellationToken.None);
    }

    /// <summary>
    /// 在导航条上滚动滚轮（真实窗口事件）：两种剪辑模式均按关键帧移动，向下滚前进、向上滚后退；
    /// 与图片预览相同，每个滚轮事件移动一格而不按增量大小换算（macOS 普通滚轮每格的增量常小于 1 且随加速变化）；
    /// 定位期间继续滚动的格数累计后一次移动。
    /// </summary>
    [NativeMediaFact("frameaccurate")]
    public async Task WheelOnFrameStripMovesByKeyFramesInBothModes()
    {
        // 29.97 帧/秒，每 30 帧一个关键帧，验证按真实关键帧跳转而非固定秒数定位。
        var source = Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), "h264.ts");
        var frames = FrameAccurateExportTests.DecodeFrames(source);
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await headless.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var localization = new LocalizationService();
            localization.SwitchLanguage("zh-CN");
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(localization);
            var vm = new MainWindowViewModel(new HanumanInstitute.MvvmDialogs.Avalonia.DialogService(
                System.Reflection.DispatchProxy.Create<IDialogManager, MainWindowPlaybackTests.UnusedDialogs>()),
                new MemoryConfiguration());
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                vm.VideoPath = source;
                Assert.True(await (Task<bool>)typeof(MainWindowViewModel).GetMethod("LoadVideoAsync", Private)!
                    .Invoke(vm, [null])!);
                vm.ShowKeyFrameStrip = true;
                await Settle();
                var strip = window.GetVisualDescendants().OfType<TSCutter.GUI.Controls.KeyFrameStripControl>().Single();
                var center = strip.TranslatePoint(new Point(strip.Bounds.Width / 2, strip.Bounds.Height / 2), window)!.Value;
                // 逐格滚动，相邻两格间隔超过忽略连续事件流的时长。
                async Task Wheel(params double[] deltas)
                {
                    foreach (var delta in deltas)
                    {
                        window.MouseWheel(center, new Vector(0, delta));
                        await Task.Delay((int)TSCutter.GUI.Controls.KeyFrameStripControl.WheelIntervalMs * 2);
                    }
                }
                Assert.True(vm.IsKeyFrameMode);
                Assert.Equal(frames[0], CurrentPts(vm));

                // 关键帧剪辑：向下一格到下一个关键帧，再滚两格前进两个关键帧，向上一格回到上一个关键帧。
                await Wheel(-1);
                await WaitForFrame(vm, frames[30]);
                await Wheel(-1, -1);
                await WaitForFrame(vm, frames[90]);
                await Wheel(1);
                await WaitForFrame(vm, frames[60]);

                // 智能剪辑也按关键帧跳转；起点可以是逐帧选中的非关键帧。
                vm.IsFrameAccurateMode = true;
                await Settle();
                Assert.True(vm.UsesFrameAccurateCut);
                await StepToAsync(vm, frames, 1.0, 40);
                await Wheel(-0.2, -0.4, -0.2);
                await WaitForFrame(vm, frames[120]);
                await Wheel(0.2);
                await WaitForFrame(vm, frames[90]);
                await StepToAsync(vm, frames, 1.0, 40);
                await Wheel(0.2);
                await WaitForFrame(vm, frames[30]);
                // 片头之前与片尾之后分别停在第一帧和最后一个关键帧。
                await Wheel(Enumerable.Repeat(0.2, 6).ToArray());
                await WaitForFrame(vm, frames[0]);
                await Wheel(Enumerable.Repeat(-0.2, 15).ToArray());
                await WaitForFrame(vm, frames[(frames.Count - 1) / 30 * 30]);
            }
            finally
            {
                window.Close();
                typeof(MainWindowViewModel).GetMethod("Close")?.Invoke(vm, null);
            }
            return true;
        }, CancellationToken.None);
    }

    /// <summary>
    /// 从文件开头开始的片段（只标记终点）：起点记为时间轴原点画面的 PTS，而不是 PTS 0——源时间戳常常不从 0 开始
    /// （TS 为很大的时间戳，带 B 帧的 MP4 首帧有延迟）。旧版本工程中起点存为 0 的片段，导出时按原点画面处理，
    /// 单段与合并都能导出，不再报“找不到所选的画面”。
    /// </summary>
    [NativeMediaTheory("frameaccurate")]
    [InlineData("h264.ts")]
    [InlineData("open-gop-flat.mp4")]
    public async Task ClipsFromTheFileStartUseTheFirstFrame(string name)
    {
        var source = Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), name);
        var frames = FrameAccurateExportTests.DecodeFrames(source);
        Assert.NotEqual(0, frames[0]);
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await headless.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var localization = new LocalizationService();
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(localization);
            var vm = new MainWindowViewModel(new HanumanInstitute.MvvmDialogs.Avalonia.DialogService(
                System.Reflection.DispatchProxy.Create<IDialogManager, MainWindowPlaybackTests.UnusedDialogs>()),
                new MemoryConfiguration());
            try
            {
                vm.IsFrameAccurateMode = true;
                vm.VideoPath = source;
                Assert.True(await (Task<bool>)typeof(MainWindowViewModel).GetMethod("LoadVideoAsync", Private)!
                    .Invoke(vm, [null])!);
                await StepToAsync(vm, frames, 2.0, 60);
                await vm.MarkClipEndShortcutCommand.ExecuteAsync(null);
                await Settle();
                var clip = Assert.Single(vm.Clips);
                Assert.Equal(frames[0], clip.StartPts);

                // 旧工程：起点存为 0。
                clip.StartPts = 0;
                var task = ((FrameAccurateExportSettings Settings, long StartPts, long? EndPts))
                    typeof(MainWindowViewModel).GetMethod("CreateFrameAccurateTask", Private)!.Invoke(vm, [clip])!;
                Assert.Equal(frames[0], task.StartPts);
                var segments = (IReadOnlyList<FrameAccurateExportSegment>)typeof(MainWindowViewModel)
                    .GetMethod("CreateFrameAccurateSegments", Private)!.Invoke(vm, [new[] { clip }])!;
                Assert.Equal(frames[0], Assert.Single(segments).StartPts);
                var output = Path.Combine(Path.GetTempPath(), $"tscutter-file-start-{Guid.NewGuid():N}{Path.GetExtension(name)}");
                try
                {
                    await new FrameAccurateExportService().ExportAsync(new FrameAccurateExportRequest(output, segments));
                    Assert.Equal(Enumerable.Range(0, 61), FrameAccurateExportTests.DecodeNumbers(output));
                }
                finally { File.Delete(output); }
            }
            finally
            {
                typeof(MainWindowViewModel).GetMethod("Close")?.Invoke(vm, null);
            }
            return true;
        }, CancellationToken.None);
    }

    /// <summary>等待滚轮触发的定位完成并核对当前画面。</summary>
    private static async Task WaitForFrame(MainWindowViewModel vm, long expected)
    {
        for (var i = 0; i < 300 && (CurrentPts(vm) != expected || vm.IsDecoding); i++)
            await Settle();
        Assert.Equal(expected, CurrentPts(vm));
    }

    private static Classic.CommonControls.ToolBarButton FrameAccurateToggle(Window window) =>
        window.GetVisualDescendants().OfType<Classic.CommonControls.ToolBarButton>()
            .Single(button => button.Name == "CutFrameAccurateButton");

    /// <summary>在按钮中心按下并松开鼠标，再处理界面任务。</summary>
    private static async Task Click(Window window, Control control)
    {
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, Avalonia.Input.MouseButton.Left);
        window.MouseUp(center, Avalonia.Input.MouseButton.Left);
        await Settle();
    }

    [NativeMediaFact("frameaccurate")]
    public async Task MarksNonKeyFramesAndExportsTheSelectedRange()
    {
        var source = Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), "h264.ts");
        var frames = FrameAccurateExportTests.DecodeFrames(source);
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await headless.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var localization = new LocalizationService();
            localization.SwitchLanguage("en-US");
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(localization);
            var configuration = new MemoryConfiguration();
            var vm = new MainWindowViewModel(new HanumanInstitute.MvvmDialogs.Avalonia.DialogService(
                System.Reflection.DispatchProxy.Create<IDialogManager, MainWindowPlaybackTests.UnusedDialogs>()), configuration);
            try
            {
                Assert.True(vm.IsKeyFrameMode);
                vm.IsFrameAccurateMode = true;
                await Settle();
                Assert.Equal(ClipCutMode.FrameAccurate, configuration.CurrentConfig.ClipCutMode);

                vm.VideoPath = source;
                Assert.True(await (Task<bool>)typeof(MainWindowViewModel).GetMethod("LoadVideoAsync", Private)!
                    .Invoke(vm, [null])!);
                Assert.True(vm.UsesFrameAccurateCut);
                Assert.True(vm.CanSelectFrameAccurate);

                await StepToAsync(vm, frames, 1.0, 40);
                await vm.MarkClipStartShortcutCommand.ExecuteAsync(null);
                await StepToAsync(vm, frames, 6.5, 202);
                // 回退两帧验证“上一帧”同样逐帧精确。
                await vm.PreviousFrameCommand.ExecuteAsync(null);
                await vm.PreviousFrameCommand.ExecuteAsync(null);
                Assert.Equal(frames[200], CurrentPts(vm));
                await vm.MarkClipEndShortcutCommand.ExecuteAsync(null);
                await Settle();
                await AssertEveryFrameStrip(vm, frames);

                var clip = Assert.Single(vm.Clips);
                Assert.Equal(frames[40], clip.StartPts);
                Assert.Equal(frames[200], clip.EndPts);
                Assert.True(clip.RequiresFrameAccurate);

                // 帧精确片段存在时不能静默切回关键帧模式。
                vm.IsKeyFrameMode = true;
                await Settle();
                Assert.True(vm.IsFrameAccurateMode);

                var project = (CutterProject)typeof(MainWindowViewModel).GetMethod("CaptureProject", Private)!
                    .Invoke(vm, [false])!;
                Assert.Equal(5, project.Version);
                Assert.Equal(ClipCutMode.FrameAccurate, project.CutMode);
                Assert.True(Assert.Single(project.Clips).FrameAccurate);

                var task = ((FrameAccurateExportSettings Settings, long StartPts, long? EndPts))
                    typeof(MainWindowViewModel).GetMethod("CreateFrameAccurateTask", Private)!.Invoke(vm, [clip])!;
                var output = Path.Combine(Path.GetTempPath(), $"tscutter-frame-accurate-ui-{Guid.NewGuid():N}.ts");
                try
                {
                    await new FrameAccurateExportService().ExportAsync(new FrameAccurateExportRequest(output,
                        [new FrameAccurateExportSegment(source, task.StartPts, task.EndPts, task.Settings)]));
                    var numbers = FrameAccurateExportTests.DecodeNumbers(output);
                    Assert.Equal(Enumerable.Range(40, 161), numbers);
                }
                finally { File.Delete(output); }

                // 单节目 TS：片段和合计都显示带“~”的预估大小，两处一致。
                Assert.StartsWith("Size: ~", clip.EstimatedSizeStr);
                Assert.Equal(clip.EstimatedSizeStr, vm.SelectedClipEstimatedSizeStr);
                clip.IsSelected = true;
                var extra = new PickedClip
                {
                    InFileInfo = clip.InFileInfo, StartTime = 7, EndTime = 8,
                    StartPosition = clip.EndPosition + 188 * 10, EndPosition = clip.EndPosition + 188 * 20,
                    UsesFrameAccurateExport = true, CanEstimateFrameAccurateSize = true, IsSelected = true
                };
                vm.Clips.Add(extra);
                try
                {
                    Assert.Equal(2, vm.SelectedClipCount);
                    Assert.StartsWith("Size: ~", extra.EstimatedSizeStr);
                    Assert.StartsWith("Size: ~", vm.SelectedClipEstimatedSizeStr);
                }
                finally
                {
                    vm.Clips.Remove(extra);
                    extra.Dispose();
                }
                // 关闭主窗口会关闭视频，界面检查放在最后。
                await AssertMainWindowLayout(vm);
            }
            finally
            {
                typeof(MainWindowViewModel).GetMethod("Close")?.Invoke(vm, null);
            }
            return true;
        }, CancellationToken.None);
    }

    /// <summary>
    /// 文件以带前导画面的 IDR 开头（前导画面显示在时间轴起点之前）：逐帧导航条在片头、片尾都应填满，
    /// 不出现负时间；从首帧向前逐帧不会退到时间轴起点之前。
    /// </summary>
    [NativeMediaFact("frameaccurate")]
    public async Task EveryFrameStripFillsWindowsAtBothEnds()
    {
        var source = Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), "hevc-leading-start.ts");
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await headless.Dispatch<bool>(async () =>
        {
            var strip = new TSCutter.GUI.ViewModels.Preview.KeyFrameStripViewModel();
            strip.SetVisibleCount(9);
            strip.SetSource(new TSCutter.GUI.Services.Preview.KeyFrameStripSource(source, null, false, null, EveryFrame: true));
            var video = new TSCutter.GUI.Models.Preview.VideoInstance(source);
            try
            {
                await video.InitVideoAsync();
                foreach (var time in new[] { 0, video.GetVideoDurationInSeconds() })
                {
                    strip.RequestAt(time, immediate: true);
                    for (var attempt = 0; attempt < 300 && (strip.Items.Count == 0 || strip.Items.Any(item => item.IsPlaceholder)); attempt++)
                    {
                        await strip.IdleTask;
                        await Settle();
                    }
                    var items = strip.Items;
                    Assert.Equal(9, items.Count(item => !item.IsPlaceholder));
                    Assert.All(items, item => Assert.True(item.Timestamp >= TimeSpan.Zero, $"{item.Timestamp}"));
                    for (var i = 1; i < items.Count; i++)
                        Assert.True(items[i].Pts > items[i - 1].Pts);
                    Assert.Equal(time == 0 ? items[0] : items[^1], Assert.Single(items, item => item.IsCurrent));
                }
                var first = await video.DecodeAtTimeAsync(TimeSpan.Zero);
                try { Assert.Null(await video.DecodeAdjacentFrameAsync(first.FramePts, -1)); }
                finally
                {
                    first.BitmapLease?.Dispose();
                    if (first.BitmapLease is null)
                        first.Bitmap?.Dispose();
                }
            }
            finally
            {
                strip.SetSource(null);
                await strip.IdleTask;
                video.Dispose();
            }
            return true;
        }, CancellationToken.None);
    }

    /// <summary>
    /// 真实主窗口在常规与窄窗口（含最小宽度）、浅色与深色主题下：工具栏右侧的帧精确开关完整显示并处于按下状态，
    /// 逐帧按钮可见可用。设置 TSCUTTER_FRAME_ACCURATE_CAPTURE 时另存截图供人工检查。
    /// </summary>
    private static async Task AssertMainWindowLayout(MainWindowViewModel vm)
    {
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        try
        {
            foreach (var theme in new[] { ClassicTheme.Standard, CustomTheme.DarkStandard, CustomTheme.Windows11, CustomTheme.Windows11Dark })
            foreach (var width in new[] { 1280, 900, 670 })
            {
                Avalonia.Application.Current!.RequestedThemeVariant = theme;
                window.Width = width;
                for (var i = 0; i < 3; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                }
                // 宽度变化后导航条按逐帧补齐两侧（相邻帧读取路径），不应留下占位。
                for (var attempt = 0; attempt < 200 && vm.KeyFrameStrip.Items.Any(item => item.IsPlaceholder); attempt++)
                {
                    await vm.KeyFrameStrip.IdleTask;
                    await Settle();
                }
                Assert.Equal(vm.KeyFrameStrip.VisibleCount, vm.KeyFrameStrip.Items.Count(item => !item.IsPlaceholder));
                window.UpdateLayout();
                if (Environment.GetEnvironmentVariable("TSCUTTER_FRAME_ACCURATE_CAPTURE") is { Length: > 0 } capture)
                {
                    Directory.CreateDirectory(capture);
                    using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
                    bitmap.Render(window);
                    bitmap.Save(Path.Combine(capture, $"{theme.Key}-{width}.png"));
                }
                // 帧精确开关在工具栏右侧：完整可见、不越出窗口，处于按下状态。
                var toggle = FrameAccurateToggle(window);
                Assert.True(toggle.IsEffectivelyVisible, $"{theme.Key} {width}: frame-accurate toggle hidden");
                var origin = toggle.TranslatePoint(default, window)!.Value;
                Assert.True(origin.X >= 0 && origin.X + toggle.Bounds.Width <= window.Bounds.Width + 0.5,
                    $"{theme.Key} {width}: frame-accurate toggle is clipped");
                Assert.True(toggle.IsChecked);
                // 已有帧精确片段时点击关闭会被拦截（弹出说明），按钮保持按下。
                if (width == 1280)
                {
                    await Click(window, toggle);
                    Assert.True(vm.IsFrameAccurateMode);
                    Assert.True(toggle.IsChecked);
                }
                foreach (var command in new[] { vm.PreviousFrameCommand, vm.NextFrameCommand })
                {
                    var button = window.GetVisualDescendants().OfType<Button>().Single(item => ReferenceEquals(item.Command, command));
                    Assert.True(button.IsEffectivelyVisible && button.IsEffectivelyEnabled, $"{theme.Key} {width}: frame step button");
                }
            }
        }
        finally
        {
            Avalonia.Application.Current!.RequestedThemeVariant = ClassicTheme.Standard;
            window.Close();
        }
    }

    /// <summary>帧精确模式的导航条显示连续的每一帧（无跳帧），当前帧高亮，并标出 P/B 等画面类型。</summary>
    private static async Task AssertEveryFrameStrip(MainWindowViewModel vm, IReadOnlyList<long> frames)
    {
        var strip = vm.KeyFrameStrip;
        for (var attempt = 0; attempt < 200 && (strip.Items.Count == 0 || strip.Items.Any(item => item.IsPlaceholder)); attempt++)
        {
            await strip.IdleTask;
            await Settle();
        }
        var items = strip.Items;
        Assert.NotEmpty(items);
        Assert.DoesNotContain(items, item => item.IsPlaceholder);
        Assert.Equal(CurrentPts(vm), Assert.Single(items, item => item.IsCurrent).Pts);
        var indices = items.Select(item => frames.ToList().IndexOf(item.Pts)).ToList();
        Assert.Equal(Enumerable.Range(indices[0], items.Count), indices);
        Assert.Contains(items, item => item.KeyFrameType is "P" or "B");
        Assert.All(items, item => Assert.False(string.IsNullOrEmpty(item.KeyFrameType)));
    }

    private static long CurrentPts(MainWindowViewModel vm) =>
        (long)typeof(MainWindowViewModel).GetProperty("CurrentPts", Private)!.GetValue(vm)!;

    /// <summary>先按时间定位到附近关键帧，再逐帧前进或后退到目标帧。</summary>
    private static async Task StepToAsync(MainWindowViewModel vm, IReadOnlyList<long> frames, double seconds, int target)
    {
        await vm.SeekToTimeAndDrawFrameAsync(TimeSpan.FromSeconds(seconds));
        await Settle();
        var index = frames.ToList().IndexOf(CurrentPts(vm));
        Assert.True(index >= 0, "seek did not land on a source frame");
        while (index != target)
        {
            await (index < target ? vm.NextFrameCommand : vm.PreviousFrameCommand).ExecuteAsync(null);
            index += index < target ? 1 : -1;
            Assert.Equal(frames[index], CurrentPts(vm));
        }
    }

    /// <summary>
    /// 导出窗口：帧精确任务显示计划条（重新编码/直接复制分布与当前位置）、当前阶段与累计统计；两段合并时阶段说明带片段序号。
    /// 在第一段复制阶段核对计划条已显示且有尺寸。设置 TSCUTTER_FRAME_ACCURATE_CAPTURE 时保存该时刻的截图（中英文、浅色/深色）。
    /// </summary>
    [NativeMediaFact("frameaccurate")]
    public async Task OutputWindowShowsEncodeAndCopyPhases()
    {
        var source = Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), "h264.mp4");
        var frames = FrameAccurateExportTests.DecodeFrames(source);
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await headless.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var localization = new LocalizationService();
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(localization);
            // 计划条与图例使用应用级状态色（正式程序由 App.axaml 合并）。
            Avalonia.Application.Current!.Resources.MergedDictionaries.Add(
                new Avalonia.Markup.Xaml.Styling.ResourceInclude(new Uri("avares://TSCutterGUI/"))
                {
                    Source = new Uri("avares://TSCutterGUI/Themes/StatusColors.axaml")
                });
            try
            {
                foreach (var (language, theme) in new[] { ("zh-CN", ClassicTheme.Standard), ("en-US", CustomTheme.DarkStandard) })
                {
                    localization.SwitchLanguage(language);
                    Avalonia.Application.Current!.RequestedThemeVariant = theme;
                    var output = Path.Combine(Path.GetTempPath(), $"tscutter-output-window-{Guid.NewGuid():N}.mp4");
                    var settings = FrameAccurateExportTests.Settings(source);
                    var vm = new ViewModels.Editing.OutputWindowViewModel();
                    vm.SetFrameAccurateTask(new FrameAccurateExportRequest(output,
                    [
                        new FrameAccurateExportSegment(source, frames[40], frames[200], settings),
                        new FrameAccurateExportSegment(source, frames[250], frames[290], settings)
                    ]), "");
                    var window = new Views.Editing.OutputWindow { DataContext = vm };
                    var phases = new List<string>();
                    var captured = false;
                    var copying = string.Format(LocalizationManager.Instance.String_Output_FrameAccurate_Segment, 1, 2,
                        LocalizationManager.Instance.String_Output_FrameAccurate_Copying);
                    // 记录每次阶段文字变化；位置最后更新，此时计划、阶段与统计都已是同一次进度报告的值。
                    vm.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName == nameof(vm.FrameAccuratePhaseText))
                            phases.Add(vm.FrameAccuratePhaseText);
                        if (e.PropertyName != nameof(vm.FrameAccuratePosition) || captured || vm.FrameAccuratePhaseText != copying)
                            return;
                        captured = true;
                        window.UpdateLayout();
                        var bar = window.GetVisualDescendants().OfType<TSCutter.GUI.Controls.FrameAccuratePlanBar>().Single();
                        Assert.True(bar.IsEffectivelyVisible);
                        Assert.NotNull(bar.EncodeBrush);
                        Assert.NotNull(bar.CopyBrush);
                        Assert.True(bar.Bounds.Width > 300 && bar.Bounds.Height > 0);
                        Assert.Equal(5, bar.Parts!.Count);
                        Assert.InRange(bar.Position, bar.Parts[1].Start, bar.Parts[1].Start + bar.Parts[1].Duration);
                        if (Environment.GetEnvironmentVariable("TSCUTTER_FRAME_ACCURATE_CAPTURE") is { Length: > 0 } capture)
                        {
                            Directory.CreateDirectory(capture);
                            using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
                            bitmap.Render(window);
                            bitmap.Save(Path.Combine(capture, $"output-{language}-{theme.Key}.png"));
                        }
                    };
                    try
                    {
                        window.Show();
                        for (var attempt = 0; attempt < 500 && vm.DialogResult is null; attempt++)
                            await Settle();
                        Assert.Null(vm.Exception);
                        Assert.True(vm.DialogResult);
                        Assert.True(captured, string.Join(" | ", phases));
                        var strings = LocalizationManager.Instance;
                        string Segment(int index, string phase) => string.Format(strings.String_Output_FrameAccurate_Segment, index, 2, phase);
                        var expected = new[]
                        {
                            Segment(1, strings.String_Output_FrameAccurate_EncodingHead), copying,
                            Segment(1, strings.String_Output_FrameAccurate_EncodingTail),
                            Segment(2, strings.String_Output_FrameAccurate_EncodingHead),
                            Segment(2, strings.String_Output_FrameAccurate_Copying),
                            strings.String_Output_FrameAccurate_Finishing
                        };
                        // 按出现顺序包含各阶段（定位阶段可能因进度节流而不单独出现）。
                        var position = 0;
                        foreach (var phase in expected)
                        {
                            position = phases.IndexOf(phase, position);
                            Assert.True(position >= 0, $"{phase} not in {string.Join(" | ", phases)}");
                        }
                        Assert.NotEmpty(vm.FrameAccuratePlanText);
                    }
                    finally
                    {
                        window.Close();
                        File.Delete(output);
                    }
                }
            }
            finally
            {
                Avalonia.Application.Current!.RequestedThemeVariant = ClassicTheme.Standard;
            }
            return true;
        }, CancellationToken.None);
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }
}
