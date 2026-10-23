using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Classic.Avalonia.Theme;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Themes;
using Xunit;

namespace TSCutter.GUI.Tests.UI;

[Collection("Avalonia UI")]
public sealed class MacOSMenuBarTests
{
    [MacOSMenuFact]
    public async Task LanguageSwitchRefreshesNativeTopLevelTitlesAndKeepsRootAndCommands()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var languages = new LocalizationService();
            languages.SwitchLanguage("en-US");
            var executions = 0;
            var open = new MenuItem
            {
                Command = new RelayCommand(() => executions++),
                InputGesture = new KeyGesture(Key.O, KeyModifiers.Meta)
            };
            var file = new MenuItem { Items = { open } };
            var undo = new MenuItem { IsEnabled = false };
            var edit = new MenuItem { Items = { undo } };
            foreach (var (item, key) in new[]
                     {
                         (file, "String.Menu.File"), (open, "String.Menu.OpenVideo"),
                         (edit, "String.Menu.Edit"), (undo, "String.Menu.Undo")
                     })
                item.Bind(MenuItem.HeaderProperty,
                    new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(key));
            var source = new Menu { Items = { file, edit } };
            var frame = new Border { Child = source };
            var window = new ClassicWindow { Content = frame };
            void Flush()
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
            }
            try
            {
                window.Show();
                using var menuBar = new MacOSMenuBar(window, source, frame);
                Flush();
                var root = NativeMenu.GetMenu(window)!;
                foreach (var theme in new[] { CustomTheme.MacOS, CustomTheme.Windows11, CustomTheme.MacOSDark })
                {
                    window.RequestedThemeVariant = theme;
                    Flush();
                    foreach (var (code, fileHeader, editHeader, undoHeader) in new[]
                             {
                                 ("zh-CN", "文件", "编辑", "撤销"),
                                 ("zh-TW", "檔案", "編輯", "復原"),
                                 ("en-US", "_File", "_Edit", "_Undo")
                             })
                    {
                        var previous = Assert.IsType<NativeMenuItem>(root.Items[0]);
                        var previousOpen = Assert.IsType<NativeMenuItem>(Assert.Single(previous.Menu!.Items));
                        languages.SwitchLanguage(code);
                        Flush();
                        Assert.Same(root, NativeMenu.GetMenu(window));
                        var nativeFile = Assert.IsType<NativeMenuItem>(root.Items[0]);
                        var nativeEdit = Assert.IsType<NativeMenuItem>(root.Items[1]);
                        // 新条目使 Avalonia 重新创建 NSMenu，刷新菜单栏实际使用的子菜单标题。
                        Assert.NotSame(previous, nativeFile);
                        Assert.Null(previousOpen.Command);
                        Assert.Equal(fileHeader, nativeFile.Header);
                        Assert.Equal(editHeader, nativeEdit.Header);
                        var nativeOpen = Assert.IsType<NativeMenuItem>(Assert.Single(nativeFile.Menu!.Items));
                        var nativeUndo = Assert.IsType<NativeMenuItem>(Assert.Single(nativeEdit.Menu!.Items));
                        Assert.Equal(undoHeader, nativeUndo.Header);
                        Assert.False(nativeUndo.IsEnabled);
                        Assert.Equal(open.InputGesture, nativeOpen.Gesture);
                        nativeOpen.Command!.Execute(null);
                    }
                }
                Assert.Equal(9, executions);
                languages.SwitchLanguage("zh-CN");
                menuBar.Dispose();
                Flush();
                Assert.Same(root, NativeMenu.GetMenu(window));
                Assert.Empty(root.Items);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [MacOSMenuFact]
    public async Task BoundThemeSubmenuSurvivesSwitchingFromAnOpenDesktopMenu()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var themes = new MenuItem();
            themes.Bind(ItemsControl.ItemsSourceProperty, new Avalonia.Data.Binding("Themes"));
            var view = new MenuItem { Header = "View", Items = { themes } };
            var source = new Menu { Items = { view } };
            var frame = new Border { Child = source };
            var choices = new[] { "Classic", "Windows 11", "macOS" }.Select(name => new MenuItem
            {
                Header = name, ToggleType = MenuItemToggleType.Radio
            }).ToArray();
            var window = new ClassicWindow
            {
                Content = frame, DataContext = new { Themes = choices },
                RequestedThemeVariant = CustomTheme.Windows11
            };
            void Flush()
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
            }
            try
            {
                window.Show();
                Flush();
                using var menuBar = new MacOSMenuBar(window, source, frame);
                for (var pass = 0; pass < 2; pass++)
                {
                    view.IsSubMenuOpen = true;
                    themes.IsSubMenuOpen = true;
                    Flush();
                    Assert.Equal(3, themes.ItemCount);
                    window.RequestedThemeVariant = pass == 0 ? CustomTheme.MacOS : CustomTheme.MacOSDark;
                    frame.IsVisible = false;
                    Flush();
                    var nativeView = Assert.IsType<NativeMenuItem>(Assert.Single(NativeMenu.GetMenu(window)!.Items));
                    var nativeThemes = Assert.IsType<NativeMenuItem>(Assert.Single(nativeView.Menu!.Items));
                    Assert.Equal(3, themes.ItemCount);
                    Assert.NotNull(nativeThemes.Menu);
                    Assert.Equal(choices.Select(choice => choice.Header),
                        nativeThemes.Menu.Items.Cast<NativeMenuItem>().Select(item => item.Header));
                    window.RequestedThemeVariant = CustomTheme.Windows11;
                    frame.IsVisible = true;
                    Flush();
                }
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

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
            var action = new MenuItem { Header = "打开视频(_O)...", Command = command, CommandParameter = "video", InputGesture = new KeyGesture(Key.O, KeyModifiers.Meta) };
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
                Assert.Equal("打开视频...", nativeAction.Header);
                Assert.Equal("打开视频(_O)...", action.Header);
                Assert.Equal(action.InputGesture, nativeAction.Gesture);
                nativeAction.Command!.Execute(null);
                Assert.Equal(1, executions);
                action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal(2, executions);
                enabled = false;
                command.NotifyCanExecuteChanged();
                Assert.False(nativeAction.IsEnabled);
                nativeAction.Command.Execute(null);
                Assert.Equal(2, executions);
                enabled = true;
                command.NotifyCanExecuteChanged();
                Assert.True(nativeAction.IsEnabled);
                action.Header = "檔案(_F)";
                action.IsVisible = false;
                Assert.Equal("檔案", nativeAction.Header);
                Assert.Equal("檔案(_F)", action.Header);
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
                Assert.Equal("檔案", nativeAction.Header);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task NativeMenuPersistsAcrossThemesAndRestoresPreviousItemsOnDispose()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var source = new Menu { Items = { new MenuItem { Header = "File" } } };
            var frame = new Border { Child = source };
            var window = new ClassicWindow { Content = frame, RequestedThemeVariant = CustomTheme.Windows11 };
            var previousItem = new NativeMenuItem("Previous");
            var previous = new NativeMenu { previousItem };
            NativeMenu.SetMenu(window, previous);
            try
            {
                window.Show();
                using (var menuBar = new MacOSMenuBar(window, source, frame))
                {
                    var nativeItem = Assert.Single(previous.Items);
                    foreach (var variant in new[]
                             {
                                 ClassicTheme.Standard, CustomTheme.DarkStandard, CustomTheme.LunaBlue, CustomTheme.LunaDark,
                                 CustomTheme.Aero, CustomTheme.AeroDark, CustomTheme.Windows8, CustomTheme.Windows8Dark,
                                 CustomTheme.Windows10, CustomTheme.Windows10Dark, CustomTheme.Windows11, CustomTheme.Windows11Dark,
                                 CustomTheme.MacOS, CustomTheme.MacOSDark, CustomTheme.Windows11
                             })
                    {
                        window.RequestedThemeVariant = variant;
                        Dispatcher.UIThread.RunJobs();
                        window.UpdateLayout();
                        Assert.Same(previous, NativeMenu.GetMenu(window));
                        Assert.Same(nativeItem, Assert.Single(previous.Items));
                        // Headless 没有原生导出器，所有主题都应保留窗口内菜单作为回退。
                        Assert.True(frame.IsVisible);
                    }
                    Assert.Equal(OperatingSystem.IsMacOS() ? "File" : "Previous",
                        Assert.IsType<NativeMenuItem>(nativeItem).Header);
                }
                Assert.Same(previous, NativeMenu.GetMenu(window));
                Assert.Same(previousItem, Assert.Single(previous.Items));
                Assert.True(frame.IsVisible);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }
}

public sealed class MacOSMenuFactAttribute : FactAttribute
{
    public MacOSMenuFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
            Skip = "需要 macOS 才会启用原生菜单投射。";
    }
}
