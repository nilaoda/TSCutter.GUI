using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Classic.Avalonia.Theme;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.Avalonia;
using Splat;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Themes;
using TSCutter.GUI.ViewModels;
using TSCutter.GUI.Views;
using Xunit;

namespace TSCutter.GUI.Tests.UI;

[Collection("Avalonia UI")]
public sealed class ClipSelectionTests
{
    [Fact]
    public async Task CheckboxesAndCurrentEditorKeepSelectionAndLayoutAcrossThemes()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var locale = new LocalizationService();
            locale.SwitchLanguage("zh-CN");
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
            Avalonia.Application.Current!.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://TSCutterGUI/"))
            { Source = new Uri("avares://TSCutterGUI/Themes/StatusColors.axaml") });
            var vm = new MainWindowViewModel(
                new DialogService(DispatchProxy.Create<IDialogManager, UnusedDialogs>()), new MemoryConfiguration());
            var clips = Enumerable.Range(1, 3).Select(index => new PickedClip
            {
                Name = "片段 " + index, InFileInfo = new FileInfo(typeof(ClipSelectionTests).Assembly.Location),
                StartTime = index * 10, EndTime = index * 10 + index * 5, EndPosition = 188
            }).ToArray();
            foreach (var clip in clips)
            {
                clip.ReplaceStartThumbnail(new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(2, 2)));
                vm.Clips.Add(clip);
            }
            clips[1].Color = "purple";
            var window = new MainWindow { DataContext = vm, Width = 1000, Height = 760 };
            try
            {
                window.Show();
                void Flush()
                {
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                }
                void Click(Control control, RawInputModifiers modifiers = RawInputModifiers.None)
                {
                    var point = control.TranslatePoint(new Point(4, control.Bounds.Height / 2), window)!.Value;
                    window.MouseDown(point, MouseButton.Left, modifiers);
                    window.MouseUp(point, MouseButton.Left, modifiers);
                    Flush();
                }
                foreach (var theme in new[]
                         {
                             ClassicTheme.Standard, CustomTheme.DarkStandard,
                             CustomTheme.LunaBlue, CustomTheme.LunaDark,
                             CustomTheme.Aero, CustomTheme.AeroDark,
                             CustomTheme.Windows8, CustomTheme.Windows8Dark,
                             CustomTheme.Windows10, CustomTheme.Windows10Dark,
                             CustomTheme.Windows11, CustomTheme.Windows11Dark,
                             CustomTheme.MacOS, CustomTheme.MacOSDark
                         })
                {
                    Avalonia.Application.Current!.RequestedThemeVariant = theme;
                    vm.SelectClip(clips[0]);
                    vm.SelectClip(clips[2], KeyModifiers.Control);
                    Flush();
                    var cards = window.GetVisualDescendants().OfType<Border>()
                        .Where(border => border.Classes.Contains("clip-card")).ToArray();
                    Assert.Equal(3, cards.Length);
                    var checks = cards.Select(card => card.GetVisualDescendants().OfType<CheckBox>().Single()).ToArray();
                    var sizes = cards.Select(card => card.Bounds.Size).ToArray();
                    var names = cards.Select((card, index) => card.GetVisualDescendants().OfType<TextBlock>()
                        .Single(text => text.Text == clips[index].DisplayName)).ToArray();
                    var gap = names[0].TranslatePoint(default, cards[0])!.Value.X
                        - checks[0].TranslatePoint(new Point(checks[0].Bounds.Width, 0), cards[0])!.Value.X;
                    Assert.InRange(gap, 0, 10);
                    Assert.Equal([true, false, true], checks.Select(check => check.IsChecked == true));
                    Assert.NotEqual(((ISolidColorBrush)cards[0].BorderBrush!).Color,
                        ((ISolidColorBrush)cards[1].BorderBrush!).Color);
                    if (theme == ClassicTheme.Standard || theme == CustomTheme.Windows11Dark)
                    {
                        // 传统和现代控件各验证一次事件路由，其他主题只检查布局。
                        Click(checks[0]);
                        Click(checks[2]);
                        Assert.Equal(0, vm.SelectedClipCount);
                        Assert.Null(vm.SelectedClip);
                        Assert.All(clips, clip => Assert.False(clip.IsActive));
                        Assert.Empty(vm.SelectedClipsSummaryStr);
                        Assert.Empty(vm.SelectedClipEstimatedSizeStr);
                        Click(names[0]);
                        Click(checks[2]);
                        Assert.Same(vm.MergeSelectedClipsCommand, vm.ClipOutputCommand);
                        // 点击已经勾选的卡片也要退出多选，只保留当前编辑项。
                        Click(names[0]);
                        Assert.Same(clips[0], vm.SelectedClip);
                        Assert.Equal([true, false, false], checks.Select(check => check.IsChecked == true));
                        Assert.Same(vm.SaveVideoClickCommand, vm.ClipOutputCommand);
                        Click(names[2], RawInputModifiers.Control);
                        Assert.Equal([true, false, true], checks.Select(check => check.IsChecked == true));
                        Click(names[1], RawInputModifiers.Control | RawInputModifiers.Shift);
                        Assert.All(checks, check => Assert.True(check.IsChecked));
                        Click(names[0], RawInputModifiers.Meta);
                        Assert.Equal([false, true, true], checks.Select(check => check.IsChecked == true));
                        Click(names[0]);
                        Click(checks[2], RawInputModifiers.Shift);
                        Assert.All(checks, check => Assert.True(check.IsChecked));
                        Click(names[1], RawInputModifiers.Shift);
                        Assert.Equal([true, true, false], checks.Select(check => check.IsChecked == true));
                        // 缩略图里的修饰键点击不能触发跳转命令，将多选重置为单选。
                        var thumbnail = cards[2].GetVisualDescendants().OfType<Button>()
                            .Single(button => ReferenceEquals(button.Command, vm.JumpToStartTimeCommand) && !button.Classes.Contains("hyperlink"));
                        Click(thumbnail, RawInputModifiers.Control);
                        Assert.All(checks, check => Assert.True(check.IsChecked));
                        Click(thumbnail, RawInputModifiers.Control);
                        Assert.Equal([true, true, false], checks.Select(check => check.IsChecked == true));
                        Assert.Equal(sizes, cards.Select(card => card.Bounds.Size));
                    }
                    vm.SelectAllClipsCommand.Execute(null);
                    Flush();
                    Assert.All(checks, check => Assert.True(check.IsChecked));
                    clips[0].ExportStatus = ClipExportStatus.Done;
                    clips[0].OutputFileInfo = new FileInfo("output.ts");
                    Flush();
                    Assert.Equal(sizes, cards.Select(card => card.Bounds.Size));
                    clips[0].OutputFileInfo = null;
                    clips[0].ExportStatus = ClipExportStatus.Pending;
                    vm.IsRemoveMode = true;
                    Flush();
                    Assert.All(clips, clip => Assert.True(clip.IsRemoval));
                    Assert.NotNull(cards[0].BorderBrush);
                    Assert.Equal(sizes, cards.Select(card => card.Bounds.Size));
                    vm.IsRemoveMode = false;
                    window.Width = 670;
                    Flush();
                    Assert.All(checks, check => Assert.True(check.Bounds.Width > 0));
                    foreach (var card in cards)
                    {
                        var marker = card.GetVisualDescendants().OfType<TextBlock>()
                            .Single(text => text.Classes.Contains("clip-current-editor"));
                        Assert.True(marker.TranslatePoint(new Point(marker.Bounds.Width, 0), card)!.Value.X <= card.Bounds.Width);
                    }
                    window.Width = 1000;
                }
            }
            finally { vm.Close(); window.Close(); }
            return true;
        }, CancellationToken.None);
    }

    [NativeMediaFact("keyframes")]
    public async Task ClearingSelectionDisablesClipActionsAndSelectingAgainRestoresThem()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(new LocalizationService());
            var vm = new MainWindowViewModel(
                new DialogService(DispatchProxy.Create<IDialogManager, UnusedDialogs>()), new MemoryConfiguration())
            { VideoPath = Path.Combine(NativeTestEnvironment.FixtureDirectory("keyframes"), "fixed.mp4") };
            var window = new MainWindow { DataContext = vm };
            try
            {
                Assert.True(await (Task<bool>)typeof(MainWindowViewModel)
                    .GetMethod("LoadVideoAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [null])!);
                vm.AddClipCommand.Execute(null);
                vm.AddClipCommand.Execute(null);
                var clips = vm.Clips.ToArray();
                vm.SelectAllClipsCommand.Execute(null);
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var check = window.GetVisualDescendants().OfType<CheckBox>()
                    .Single(control => ReferenceEquals(control.DataContext, clips[0]));
                check.Focus();
                window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
                Assert.False(clips[0].IsSelected);
                Assert.False(vm.IsPlaybackActive);
                vm.SelectAllClipsCommand.Execute(null);
                var commands = new[] { vm.RemoveClipCommand, vm.SaveVideoClickCommand, vm.AddToQueueCommand,
                    vm.MarkClipStartCommand, vm.MarkClipEndCommand };
                Assert.All(commands, command => Assert.True(command.CanExecute(null)));
                var notifications = 0;
                vm.RemoveClipCommand.CanExecuteChanged += (_, _) => notifications++;
                foreach (var clip in clips) vm.SelectClip(clip, KeyModifiers.Control);
                Assert.Null(vm.SelectedClip);
                Assert.Equal(0, vm.SelectedClipCount);
                Assert.All(commands, command => Assert.False(command.CanExecute(null)));
                Assert.False(vm.MergeSelectedClipsCommand.CanExecute(null));
                Assert.True(notifications > 0);
                Assert.Empty(vm.SelectedClipsSummaryStr);
                Assert.Empty(vm.SelectedClipEstimatedSizeStr);
                vm.SelectClip(clips[0], KeyModifiers.Control);
                Assert.Same(clips[0], vm.SelectedClip);
                Assert.All(commands, command => Assert.True(command.CanExecute(null)));
            }
            finally { vm.Close(); window.Close(); }
            return true;
        }, CancellationToken.None);
    }

    public class UnusedDialogs : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
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
