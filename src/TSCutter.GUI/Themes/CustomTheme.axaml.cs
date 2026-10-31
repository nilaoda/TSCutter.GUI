using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Classic.Avalonia.Theme;

namespace TSCutter.GUI.Themes;

public class CustomTheme : Styles
{
    static CustomTheme()
    {
        // 自绘标题栏共用窗口菜单，菜单外观仍按各自主题绘制。
        Control.ContextRequestedEvent.AddClassHandler<ClassicWindow>(DesktopWindowMenu.OnContextRequested);
        InputElement.PointerPressedEvent.AddClassHandler<ClassicWindow>(DesktopWindowMenu.OnOutsidePointerPressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.PointerPressedEvent.AddClassHandler<ClassicWindow>(DesktopWindowMenu.OnPointerPressed);
        InputElement.PointerPressedEvent.AddClassHandler<ClassicWindow>(MacOSWindowHeader.OnPointerPressed);

        // 随控件自身主题更新共用及各主题样式类；类处理器不持有控件实例。
        static void UpdateThemeClasses(Control control)
        {
            var variant = control.ActualThemeVariant;
            // macOS 外观沿用 Fluent 的控件配色与模板，只替换窗口外壳。
            var fluent = IsWindows11(variant) || IsMacOS(variant);
            control.Classes.Set("desktop-theme", IsDesktopTheme(variant));
            control.Classes.Set("luna", IsLuna(variant));
            control.Classes.Set("aero", IsAero(variant));
            control.Classes.Set("windows8", IsWindows8(variant));
            control.Classes.Set("windows10", IsWindows10(variant));
            control.Classes.Set("windows11", IsWindows11(variant));
            control.Classes.Set("fluent", fluent);
            control.Classes.Set("macos", IsMacOS(variant));
            control.Classes.Set("modern-caption", IsWindows10(variant) || fluent);
            control.Classes.Set("flat-theme", IsWindows8(variant) || IsWindows10(variant) || fluent);
            if (control is ClassicWindow window)
            {
                WindowsLegacyWindowChrome.Update(window);
                window.Classes.Set("macos-native-titlebar", UsesNativeTitleBar(variant));
                window.Classes.Set("windows11-snap", IsWindows11(variant) &&
                    OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000));
                UpdateGlassClass(window);
            }
        }

        // 只有系统实际提供背景模糊时才启用半透明绘制，单纯透明仍使用实色回退。
        static void UpdateGlassClass(ClassicWindow window)
        {
            MacOsBackdropClip.RefreshTransparency(window);
            WindowsWindowCorners.Update(window);
            var variant = window.ActualThemeVariant;
            // macOS 外观使用系统标题栏和系统材质，与 Windows 11 的 Mica 分支分开处理。
            var nativeTitleBar = UsesNativeTitleBar(variant);
            var modern = IsWindows11(variant);
            var glass = (IsAero(variant) || modern || nativeTitleBar) &&
                (window.ActualTransparencyLevel == WindowTransparencyLevel.Blur ||
                 window.ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur ||
                 window.ActualTransparencyLevel == WindowTransparencyLevel.Mica);
            window.Classes.Set("aero-glass", glass && !modern && !nativeTitleBar);
            window.Classes.Set("windows11-backdrop", glass && modern);
            window.Classes.Set("macos-backdrop", glass && nativeTitleBar);
            // Avalonia 的控件裁剪不会裁到独立的系统模糊层，需同步原生容器圆角。
            // 原生标题栏窗口已由 AppKit 统一裁剪，重复设置会让边缘透明度相乘。
            double? radius = glass || nativeTitleBar ? 0 : null;
            if (glass && !nativeTitleBar &&
                window.WindowState is not (WindowState.Maximized or WindowState.FullScreen) &&
                window.TryFindResource("DesktopWindowCornerRadius", window.ActualThemeVariant, out var resource) &&
                resource is CornerRadius corners)
                radius = corners.TopLeft;
            // 原生标题栏只有在拿到系统模糊层时才让窗口透明；仅有透明模式时保持系统实色底，否则整窗不可见。
            MacOsBackdropClip.Update(window, radius,
                useAeroMaterial: !modern && !nativeTitleBar,
                clearWindowBackground: !nativeTitleBar || glass,
                useNativeFrame: nativeTitleBar);
        }

        Window.WindowOpenedEvent.AddClassHandler<ClassicWindow>((window, _) => MacOSWindowPlacement.Prepare(window));
        TopLevel.FrameSizeProperty.Changed.AddClassHandler<ClassicWindow>((window, _) => MacOSWindowPlacement.Complete(window));

        Control.LoadedEvent.AddClassHandler<ClassicWindow>((window, _) =>
        {
            WindowsLegacyWindowChrome.Update(window);
            UpdateGlassClass(window);
        });
        Window.ExtendClientAreaToDecorationsHintProperty.Changed.AddClassHandler<ClassicWindow>((window, _) => WindowsWindowCorners.Update(window));
        Window.WindowStateProperty.Changed.AddClassHandler<ClassicWindow>((window, _) => UpdateGlassClass(window));
        TopLevel.ActualThemeVariantProperty.Changed.AddClassHandler<ClassicWindow>((window, _) => UpdateThemeClasses(window));
        TopLevel.ActualTransparencyLevelProperty.Changed.AddClassHandler<ClassicWindow>((window, _) => UpdateGlassClass(window));

        // Avalonia 11.3 的模板清理只遍历视觉子树，弹出层中的旧容器不会随模板一起释放。
        // 在替换菜单或下拉框模板前重置旧面板，避免同一控件同时挂到新旧面板上。
        static void ReleasePopupItems(ItemsControl control)
        {
            if (control.Presenter is not { Panel: not null } presenter)
                return;

            // 先关闭旧弹出层，防止布局过程重新测量旧面板并抢回已释放的子项。
            if (control is MenuItem menu)
                menu.SetCurrentValue(MenuItem.IsSubMenuOpenProperty, false);
            else if (control is ComboBox combo)
                combo.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false);

            var panel = presenter.ItemsPanel;
            // 虚拟面板脱离时只移除子项，不清理生成容器的内容；控件条目仍会被旧呈现器持有。
            var generated = presenter.Panel is VirtualizingPanel virtualPanel
                ? virtualPanel.Children.Where(child => !control.Items.Contains(child)).ToArray()
                : [];
            presenter.SetCurrentValue(ItemsPresenter.ItemsPanelProperty, new FuncTemplate<Panel?>(() => null));
            foreach (var container in generated)
            {
                control.ItemContainerGenerator.ClearItemContainer(container);
                foreach (var content in container.GetVisualDescendants().OfType<ContentPresenter>())
                    content.UpdateChild();
            }
            if (control is ComboBox)
            {
                // 容器脱离旧面板后清除选择标记，由新面板按当前选中值重新设置，避免重复准备时反向取消选择。
                foreach (var item in control.Items)
                    if (item is ComboBoxItem container)
                        container.ClearValue(SelectingItemsControl.IsSelectedProperty);
            }
            // 保留原面板声明，模板变更被撤回时旧模板仍能正常重新生成内容。
            presenter.SetCurrentValue(ItemsPresenter.ItemsPanelProperty, panel);
        }

        TemplatedControl.TemplateProperty.Changed.AddClassHandler<MenuItem>((menu, _) => ReleasePopupItems(menu));
        TemplatedControl.TemplateProperty.Changed.AddClassHandler<ComboBox>((combo, _) => ReleasePopupItems(combo));

        // 弹出菜单有独立的视觉根，按自身主题标记，避免依赖窗口祖先选择器。
        TopLevel.ActualThemeVariantProperty.Changed.AddClassHandler<Menu>((menu, _) => UpdateThemeClasses(menu));
        TopLevel.ActualThemeVariantProperty.Changed.AddClassHandler<MenuItem>((menu, _) => UpdateThemeClasses(menu));
        TopLevel.ActualThemeVariantProperty.Changed.AddClassHandler<ContextMenu>((menu, _) => UpdateThemeClasses(menu));
        TopLevel.ActualThemeVariantProperty.Changed.AddClassHandler<MenuFlyoutPresenter>((menu, _) => UpdateThemeClasses(menu));
        TopLevel.ActualThemeVariantProperty.Changed.AddClassHandler<FlyoutPresenter>((flyout, _) => UpdateThemeClasses(flyout));
        TopLevel.ActualThemeVariantProperty.Changed.AddClassHandler<ColorPicker>((picker, _) => UpdateThemeClasses(picker));
        TopLevel.ActualThemeVariantProperty.Changed.AddClassHandler<NumericUpDown>((input, _) => UpdateThemeClasses(input));
        // 弹层打开时会用 PresenterClasses 覆盖样式类，加载后补回主题标记。
        Control.LoadedEvent.AddClassHandler<FlyoutPresenter>((flyout, _) =>
        {
            UpdateThemeClasses(flyout);
        });

        // 提示框也有独立的弹出根，按自身主题更新，避免漏掉各主题的提示样式。
        TopLevel.ActualThemeVariantProperty.Changed.AddClassHandler<ToolTip>((tip, _) => UpdateThemeClasses(tip));

        // 模板内的按钮保留各自用途，避免普通按钮样式覆盖下拉箭头等模板部件。
        StyledElement.TemplatedParentProperty.Changed.AddClassHandler<Button>((button, _) =>
        {
            // 选色器的色谱开关属于内容操作，需要普通切换按钮的按下、选中和禁用状态。
            var isColorPickerToggle = button is ToggleButton && button.TemplatedParent is ColorPicker;
            button.Classes.Set("desktop-template-button", button.TemplatedParent != null && !isColorPickerToggle);
        });

        // Classic 只在加载或滚动位置变化时检查范围，异步填入内容后箭头会保持禁用。
        // 补齐范围变化通知，确保内容增减后立即刷新箭头状态。
        static void UpdateScrollBarRange(ScrollBar bar) =>
            bar.Classes.Set("__classic_theme_is_empty", bar.Minimum >= bar.Maximum);

        RangeBase.MinimumProperty.Changed.AddClassHandler<ScrollBar>((bar, _) => UpdateScrollBarRange(bar));
        RangeBase.MaximumProperty.Changed.AddClassHandler<ScrollBar>((bar, _) => UpdateScrollBarRange(bar));
    }

    public static ThemeVariant LunaBlue { get; } = new("LunaBlue", ThemeVariant.Light);
    public static ThemeVariant LunaSilver { get; } = new("LunaSilver", ThemeVariant.Light);
    public static ThemeVariant LunaOliveGreen { get; } = new("LunaOliveGreen", ThemeVariant.Light);

    public static ThemeVariant LunaDark { get; } = new("LunaDark", ThemeVariant.Dark);

    public static bool IsLuna(ThemeVariant variant) =>
        variant == LunaBlue || variant == LunaSilver || variant == LunaOliveGreen || variant == LunaDark;

    public static ThemeVariant Aero { get; } = new("Aero", ThemeVariant.Light);

    public static ThemeVariant AeroDark { get; } = new("AeroDark", ThemeVariant.Dark);

    public static bool IsAero(ThemeVariant variant) => variant == Aero || variant == AeroDark;

    public static ThemeVariant Windows8 { get; } = new("Windows8", ThemeVariant.Light);

    public static ThemeVariant Windows8Dark { get; } = new("Windows8Dark", ThemeVariant.Dark);

    public static bool IsWindows8(ThemeVariant variant) => variant == Windows8 || variant == Windows8Dark;

    public static ThemeVariant Windows10 { get; } = new("Windows10", ThemeVariant.Light);

    public static ThemeVariant Windows10Dark { get; } = new("Windows10Dark", ThemeVariant.Dark);

    public static bool IsWindows10(ThemeVariant variant) => variant == Windows10 || variant == Windows10Dark;

    public static ThemeVariant Windows11 { get; } = new("Windows11", ThemeVariant.Light);

    public static ThemeVariant Windows11Dark { get; } = new("Windows11Dark", ThemeVariant.Dark);

    public static bool IsWindows11(ThemeVariant variant) => variant == Windows11 || variant == Windows11Dark;

    // macOS 外观只在 macOS 上使用原生标题栏，其他平台按 Fluent 实色窗口绘制。
    // 显式声明明暗基准：主题字典本身按变体键取值，缺省继承会让深色外观拿到浅色色板。
    public static ThemeVariant MacOS { get; } = new("MacOS", ThemeVariant.Light);

    public static ThemeVariant MacOSDark { get; } = new("MacOSDark", ThemeVariant.Dark);

    public static bool IsMacOS(ThemeVariant variant) => variant == MacOS || variant == MacOSDark;

    public static bool IsDesktopTheme(ThemeVariant variant) =>
        IsLuna(variant) || IsAero(variant) || IsWindows8(variant) || IsWindows10(variant) || IsWindows11(variant) ||
        IsMacOS(variant);

    /// <summary>macOS 外观是否使用系统原生标题栏（红绿灯、双击缩放、全屏由 AppKit 提供）。</summary>
    public static bool UsesNativeTitleBar(ThemeVariant variant) => OperatingSystem.IsMacOS() && IsMacOS(variant);

    public static ThemeVariant DarkStandard { get; } = new("DarkStandard", ThemeVariant.Dark);
}