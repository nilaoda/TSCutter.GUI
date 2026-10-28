using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.VisualTree;
using Classic.Avalonia.Theme;
using TSCutter.GUI.Utils.Application;

namespace TSCutter.GUI.Themes;

/// <summary>为自绘标题栏提供窗口菜单，拖动交给平台，窗口操作遵循标题按钮的限制。</summary>
internal sealed class DesktopWindowMenu : AvaloniaObject
{
    private static readonly AttachedProperty<ContextMenu?> OpenMenuProperty =
        AvaloniaProperty.RegisterAttached<DesktopWindowMenu, ClassicWindow, ContextMenu?>("OpenMenu");

    public static void OnContextRequested(ClassicWindow window, ContextRequestedEventArgs e)
    {
        if (e.Handled || IsMenuSource(e.Source) || window.WindowState == WindowState.FullScreen || !e.TryGetPosition(window, out var point))
            return;

        var captions = FindCaption(window, point);
        if (captions == null)
            return;

        bool CanRestore() => window.WindowState is WindowState.Maximized or WindowState.Minimized;
        bool CanMinimize() => window.CanMinimize && window.WindowState != WindowState.Minimized && CanUse(captions, "PART_MinimizeButton");
        bool CanMaximize() => window.CanResize && window.CanMaximize && window.WindowState == WindowState.Normal && CanUse(captions, "PART_RestoreButton");

        bool CanUseNativeCommand(WindowsSystemMenu.Command command) => command switch
        {
            WindowsSystemMenu.Command.Restore => CanRestore(),
            WindowsSystemMenu.Command.Move => window.WindowState == WindowState.Normal,
            WindowsSystemMenu.Command.Size => window.CanResize && window.WindowState == WindowState.Normal,
            WindowsSystemMenu.Command.Minimize => CanMinimize(),
            WindowsSystemMenu.Command.Maximize => CanMaximize(),
            WindowsSystemMenu.Command.Close => CanUse(captions, "PART_CloseButton"),
            _ => false
        };
        if (WindowsSystemMenu.TryShow(window, point, CanUseNativeCommand))
        {
            e.Handled = true;
            return;
        }

        // 定位矩形需有面积，零尺寸矩形求交集后会丢失点击坐标。
        var menu = new ContextMenu
        {
            Focusable = true,
            Placement = PlacementMode.AnchorAndGravity,
            PlacementTarget = window,
            PlacementAnchor = PopupAnchor.TopLeft,
            PlacementGravity = PopupGravity.BottomRight,
            PlacementRect = new Rect(point, new Size(1, 1)),
        };
        var localization = App.LocalizationService;
        var strings = new LocalizationManager(localization);
        MenuItem Item(Func<string> textProvider, Func<bool> canExecute, Action execute)
        {
            var item = new MenuItem { Header = textProvider(), IsEnabled = canExecute() };
            void RefreshHeader() => item.Header = textProvider();
            localization.LanguageChanged += RefreshHeader;
            menu.Closed += (_, _) => localization.LanguageChanged -= RefreshHeader;
            item.Click += (_, _) =>
            {
                if (!canExecute())
                    return;
                menu.Close();
                execute();
            };
            return item;
        }
        menu.Items.Add(Item(() => strings.String_Window_Restore, CanRestore, () => window.WindowState = WindowState.Normal));
        menu.Items.Add(Item(() => strings.String_Window_Minimize, CanMinimize, () => window.WindowState = WindowState.Minimized));
        menu.Items.Add(Item(() => strings.String_Window_Maximize, CanMaximize, () => window.WindowState = WindowState.Maximized));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(() => strings.String_Window_Close, () => CanUse(captions, "PART_CloseButton"), window.Close));

        // 只在菜单打开期间监听窗口关闭，关闭菜单后释放订阅和保存的引用。
        void WindowClosed(object? sender, EventArgs args) => menu.Close();
        menu.Closed += (_, _) =>
        {
            window.Closed -= WindowClosed;
            if (window.GetValue(OpenMenuProperty) == menu)
            {
                window.ClearValue(OpenMenuProperty);
                window.Classes.Remove("window-menu-open");
            }
        };
        window.GetValue(OpenMenuProperty)?.Close();
        window.SetValue(OpenMenuProperty, menu);
        window.Classes.Add("window-menu-open");
        window.Closed += WindowClosed;
        // 标题控件不在关闭覆盖层之内，挂在窗口上以接入框架的外部点击关闭机制。
        menu.Open(window);
        e.Handled = true;
    }

    public static void OnOutsidePointerPressed(ClassicWindow window, PointerPressedEventArgs e)
    {
        if (window.GetValue(OpenMenuProperty) is not { IsOpen: true } menu || IsMenuSource(e.Source))
            return;

        // 正文交给框架覆盖层处理；标题栏和边框位于覆盖层外，需在事件下发前关闭菜单。
        if (e.Source is LightDismissOverlayLayer)
            return;
        menu.Close();
        if (e.GetCurrentPoint(window).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed &&
            FindCaption(window, e.GetPosition(window)) != null)
            e.Handled = true;
    }

    public static void OnPointerPressed(ClassicWindow window, PointerPressedEventArgs e)
    {
        if (e.Handled || IsMenuSource(e.Source) || e.GetCurrentPoint(window).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
            return;
        var captions = FindCaption(window, e.GetPosition(window));
        if (captions == null)
            return;

        // 标题面板接收右键后，同时将左键拖动交给平台，双击沿用标题按钮的限制。
        if (e.ClickCount == 2)
        {
            if (window.CanResize && window.CanMaximize && CanUse(captions, "PART_RestoreButton"))
                window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
        else if (e.ClickCount == 1)
            window.BeginMoveDrag(e);
        e.Handled = true;
    }

    private static bool IsMenuSource(object? source) => source is Visual visual &&
        (visual is ContextMenu || visual.GetVisualAncestors().Any(parent => parent is ContextMenu));

    private static bool CanUse(CaptionButtons captions, string name) => captions.GetVisualDescendants().OfType<Button>()
        .Any(button => button.Name == name && button.IsEffectivelyVisible && button.IsEffectivelyEnabled);

    private static CaptionButtons? FindCaption(ClassicWindow window, Point point)
    {
        if (window.WindowState == WindowState.FullScreen)
            return null;
        var layout = window.GetVisualDescendants().OfType<DockPanel>()
            .FirstOrDefault(panel => panel.Name == "RootLayout" && panel.TemplatedParent == window);
        if (layout?.Children.FirstOrDefault() is not TemplatedControl title || !title.IsEffectivelyVisible ||
            title.TranslatePoint(default, window) is not { } origin || !new Rect(origin, title.Bounds.Size).Contains(point))
            return null;

        // 标题按钮仍使用自身交互，不在其上弹出另一个菜单或启动窗口拖动。
        var captions = title.GetVisualDescendants().OfType<CaptionButtons>().FirstOrDefault();
        if (captions == null || captions.TranslatePoint(default, window) is not { } captionOrigin ||
            new Rect(captionOrigin, captions.Bounds.Size).Contains(point))
            return null;
        return captions;
    }
}
