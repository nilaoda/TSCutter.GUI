using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Classic.Avalonia.Theme;
using TSCutter.GUI.Themes;
using Xunit;

namespace TSCutter.GUI.Tests.UI;

[Collection("Avalonia UI")]
public sealed class MacOSMenuBarTests
{
    [Fact]
    public async Task HiddenMenuResolvesNestedResourcesAndCommands()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var executions = 0;
            var action = new MenuItem();
            action.Bind(MenuItem.HeaderProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Test.Menu.Header"));
            action.Bind(MenuItem.CommandProperty, new Avalonia.Data.Binding("Action"));
            var source = new Menu { Items = { new MenuItem { Header = "File", Items = { action } } } };
            var window = new ClassicWindow { DataContext = new { Action = new RelayCommand(() => executions++) },
                Content = new Border { IsVisible = false, Child = source } };
            window.Resources["Test.Menu.Header"] = "Open";
            try
            {
                window.Show();
                using var projection = new MacOSMenuBar.MenuProjection(source);
                var native = Assert.IsType<NativeMenuItem>(Assert.IsType<NativeMenuItem>(projection.Menu.Items[0]).Menu!.Items[0]);
                Assert.Equal("Open", native.Header);
                native.Command!.Execute(null);
                Assert.Equal(1, executions);
                window.Resources["Test.Menu.Header"] = "打开";
                Assert.Equal("打开", native.Header);
                foreach (var variant in new[] { CustomTheme.Windows11, CustomTheme.MacOSDark, CustomTheme.LunaBlue })
                {
                    window.RequestedThemeVariant = variant;
                    Dispatcher.UIThread.RunJobs();
                    projection.RefreshSource();
                    Assert.Equal("打开", native.Header);
                    native.Command!.Execute(null);
                }
                Assert.Equal(4, executions);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task NativeProjectionSharesCommandsStateAndDynamicItems()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var executions = 0;
            var enabled = true;
            var command = new RelayCommand<string>(value => { Assert.Equal("video", value); executions++; }, _ => enabled);
            var action = new MenuItem { Header = "Open", Command = command, CommandParameter = "video", InputGesture = new KeyGesture(Key.O, KeyModifiers.Meta) };
            var theme = new MenuItem { Header = "macOS", ToggleType = MenuItemToggleType.Radio };
            var themeClicks = 0;
            theme.Click += (_, _) => themeClicks++;
            var group = new MenuItem { Header = "File", Items = { action, new Separator(), theme } };
            var source = new Menu { Items = { group } };
            var window = new ClassicWindow { Content = source };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                using var projection = new MacOSMenuBar.MenuProjection(source);
                var nativeGroup = Assert.IsType<NativeMenuItem>(Assert.Single(projection.Menu.Items));
                var nativeAction = Assert.IsType<NativeMenuItem>(nativeGroup.Menu!.Items[0]);
                var nativeTheme = Assert.IsType<NativeMenuItem>(nativeGroup.Menu.Items[2]);
                Assert.IsType<NativeMenuItemSeparator>(nativeGroup.Menu.Items[1]);
                Assert.Equal(action.InputGesture, nativeAction.Gesture);
                nativeAction.Command!.Execute(null);
                Assert.Equal(1, executions);
                enabled = false;
                command.NotifyCanExecuteChanged();
                Assert.False(nativeAction.IsEnabled);
                nativeAction.Command.Execute(null);
                Assert.Equal(1, executions);
                enabled = true;
                command.NotifyCanExecuteChanged();
                Assert.True(nativeAction.IsEnabled);
                action.Header = "打开";
                action.IsVisible = false;
                Assert.Equal("打开", nativeAction.Header);
                Assert.False(nativeAction.IsVisible);
                nativeTheme.Command!.Execute(null);
                Assert.Equal(1, themeClicks);
                Assert.True(theme.IsChecked);
                Assert.True(nativeTheme.IsChecked);
                theme.IsChecked = false;
                Assert.False(nativeTheme.IsChecked);
                group.Items.Add(new MenuItem { Header = "New" });
                Assert.Equal(4, nativeGroup.Menu.Items.Count);
                group.Items.Clear();
                Assert.Null(nativeGroup.Menu);
                // 已移除的原生条目不应再持有命令或响应源菜单变化。
                Assert.Null(nativeAction.Command);
                action.Header = "Detached";
                Assert.Equal("打开", nativeAction.Header);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MenuStaysVisibleWithoutNativeExporterAndRestoresPreviousMenu()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var source = new Menu { Items = { new MenuItem { Header = "File" } } };
            var frame = new Border { Child = source };
            var window = new ClassicWindow { Content = frame, RequestedThemeVariant = CustomTheme.MacOS };
            var previous = new NativeMenu();
            NativeMenu.SetMenu(window, previous);
            try
            {
                window.Show();
                using (var menuBar = new MacOSMenuBar(window, source, frame))
                {
                    Assert.True(frame.IsVisible);
                    Assert.Same(previous, NativeMenu.GetMenu(window));
                    Assert.Equal(OperatingSystem.IsMacOS() ? 1 : 0, previous.Items.Count);
                    window.RequestedThemeVariant = CustomTheme.Windows11;
                    Assert.Same(previous, NativeMenu.GetMenu(window));
                    Assert.True(frame.IsVisible);
                    window.RequestedThemeVariant = CustomTheme.MacOSDark;
                    Assert.True(frame.IsVisible);
                }
                Assert.Same(previous, NativeMenu.GetMenu(window));
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }
}
