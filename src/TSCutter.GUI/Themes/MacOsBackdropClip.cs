using System;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Styling;

namespace TSCutter.GUI.Themes;

/// <summary>调整系统玻璃材质，并让模糊层、阴影与应用绘制使用相同的窗口轮廓。</summary>
internal sealed class MacOsBackdropClip : AvaloniaObject
{
    private static readonly AttachedProperty<ClipState?> SavedStateProperty =
        AvaloniaProperty.RegisterAttached<MacOsBackdropClip, Window, ClipState?>("SavedState");

    private static readonly AttachedProperty<bool> RefreshingTransparencyProperty =
        AvaloniaProperty.RegisterAttached<MacOsBackdropClip, Window, bool>("RefreshingTransparency");

    public static void RefreshTransparency(Window window)
    {
        // Avalonia.Native 11.3 遇到与当前模式相同的 AcrylicBlur 时会继续遍历候选项，
        // 错误回退到 Transparent 或 None。此时重新申请一次，恢复系统材质。
        var variant = window.ActualThemeVariant;
        if (!OperatingSystem.IsMacOS() ||
            !(CustomTheme.IsMacOS(variant) || CustomTheme.IsWindows11(variant) || CustomTheme.IsAero(variant)) ||
            window.TryGetPlatformHandle() is not { HandleDescriptor: "NSWindow" } ||
            (window.ActualTransparencyLevel != WindowTransparencyLevel.None &&
             window.ActualTransparencyLevel != WindowTransparencyLevel.Transparent) ||
            !window.TransparencyLevelHint.Contains(WindowTransparencyLevel.AcrylicBlur) ||
            window.GetValue(RefreshingTransparencyProperty))
            return;
        window.SetValue(RefreshingTransparencyProperty, true);
        try
        {
            // 临时撤回材质请求，再由原样式重新申请；同步释放覆盖，不留下本地属性值。
            // 重新申请时平台已处于 None，可避开“当前模式相同”的错误分支。
            using (window.SetValue(TopLevel.TransparencyLevelHintProperty,
                       Array.Empty<WindowTransparencyLevel>(), BindingPriority.Animation)) { }
        }
        finally { window.ClearValue(RefreshingTransparencyProperty); }
    }

    internal static void EnsureResizableWindow(Window window)
    {
        if (!OperatingSystem.IsMacOS() || window.TryGetPlatformHandle() is not { HandleDescriptor: "NSWindow" } handle)
            return;
        // Classic 只在激活时补回缩放标记；主题切换和全屏退出也需要恢复。
        if (window.SystemDecorations == SystemDecorations.None && window.CanResize)
        {
            var style = GetInteger(handle.Handle, Selector("styleMask"));
            if ((style & ResizableWindowMask) == 0)
                SetInteger(handle.Handle, Selector("setStyleMask:"), style | ResizableWindowMask);
        }
    }

    public static void Update(Window window, double? radius, bool useAeroMaterial = true, bool clearWindowBackground = true,
        bool useNativeFrame = false)
    {
        // Loaded 可能晚于首帧；原生句柄和图层就绪后立即设置材质，避免先显示系统默认材质再跳变。
        if (!OperatingSystem.IsMacOS() ||
            window.TryGetPlatformHandle() is not { HandleDescriptor: "NSWindow" } handle)
        {
            window.Classes.Remove("macos-native-corners");
            return;
        }

        EnsureResizableWindow(window);

        var saved = window.GetValue(SavedStateProperty);
        if (radius == null && saved == null)
        {
            window.Classes.Remove("macos-native-corners");
            return;
        }

        // 只操作 Avalonia 已创建的容器图层，不插入原生视图，不参与鼠标事件。
        var view = GetObject(handle.Handle, Selector("contentView"));
        if (view == IntPtr.Zero)
        {
            window.Classes.Remove("macos-native-corners");
            return;
        }
        var layer = GetObject(view, Selector("layer"));
        if (layer == IntPtr.Zero)
        {
            window.Classes.Remove("macos-native-corners");
            return;
        }
        var backdrop = FindBackdrop(view);

        if (radius is { } value)
        {
            if (saved == null)
            {
                saved = new ClipState(
                    GetDouble(layer, Selector("cornerRadius")), GetByte(layer, Selector("masksToBounds")),
                    GetByte(handle.Handle, Selector("hasShadow")),
                    RespondsToSelector(handle.Handle, Selector("respondsToSelector:"), Selector("titlebarSeparatorStyle")) != 0
                        ? GetInteger(handle.Handle, Selector("titlebarSeparatorStyle")) : null,
                    backdrop == IntPtr.Zero ? null : CaptureBackdrop(backdrop));
                window.SetValue(SavedStateProperty, saved);
            }
            // 系统可能在切换透明模式时重建模糊视图，记录新视图原值后继续应用材质。
            if (backdrop != IntPtr.Zero && saved.Backdrop?.View != backdrop)
            {
                saved.Backdrop?.Appearance.Dispose();
                saved = saved with { Backdrop = CaptureBackdrop(backdrop) };
                window.SetValue(SavedStateProperty, saved);
            }
            SetDouble(layer, Selector("setCornerRadius:"), value);
            SetByte(layer, Selector("setMasksToBounds:"), value > 0 ? (byte)1 : (byte)0);
            // 无边框窗口默认关闭系统阴影；玻璃窗口恢复阴影，最大化时关闭。
            SetByte(handle.Handle, Selector("setHasShadow:"), (useNativeFrame && window.WindowState == WindowState.Normal || value > 0) ? (byte)1 : (byte)0);
            // macOS 11+ 默认分隔线在深色材质上形成黑边；仅原生标题栏主题关闭它。
            if (saved.TitlebarSeparatorStyle is { } separator)
                SetInteger(handle.Handle, Selector("setTitlebarSeparatorStyle:"), useNativeFrame ? NoTitlebarSeparator : separator);
            // Avalonia 在模糊模式下仍设置不透明的系统底色，裁剪之外会留下浅色方角。
            if (clearWindowBackground)
                SetWindowBackground(handle.Handle, transparent: true);
            else
                RestoreWindowBackground(handle.Handle);
            if (saved.Backdrop is { } original && original.View == backdrop)
            {
                // 现代主题用侧栏材质近似云母，避免系统标题栏材质将背景压成实色；失焦仍跟随窗口状态。
                SetInteger(backdrop, Selector("setMaterial:"), useAeroMaterial ? HudWindowMaterial : SidebarMaterial);
                SetInteger(backdrop, Selector("setState:"), useAeroMaterial ? ActiveEffectState : original.State);
                // 仅调整模糊层的明暗，避免应用主题与系统外观不一致时出现白色或黑色底层。
                // 按变体自带的暗色基准判断，新增的深色外观不必再逐个登记。
                var dark = useAeroMaterial || window.ActualThemeVariant == ThemeVariant.Dark ||
                           window.ActualThemeVariant.InheritVariant == ThemeVariant.Dark;
                var appearanceName = GetObjectFromUtf8(GetClass("NSString"), Selector("stringWithUTF8String:"),
                    dark ? "NSAppearanceNameDarkAqua" : "NSAppearanceNameAqua");
                var appearance = GetObjectWithArgument(GetClass("NSAppearance"), Selector("appearanceNamed:"), appearanceName);
                if (appearance != IntPtr.Zero)
                    SetObject(backdrop, Selector("setAppearance:"), appearance);
            }
            // CALayer 已裁剪整个客户区和材质，撤销应用的第二次裁剪，避免边缘透明度相乘。
            window.Classes.Set("macos-native-corners", true);
        }
        else if (saved != null)
        {
            window.Classes.Remove("macos-native-corners");
            // 离开玻璃主题时恢复原值，避免影响其他主题的窗口装饰。
            SetDouble(layer, Selector("setCornerRadius:"), saved.Radius);
            SetByte(layer, Selector("setMasksToBounds:"), saved.MasksToBounds);
            // 退出原生标题栏后无边框主题不保留其系统阴影。
            SetByte(handle.Handle, Selector("setHasShadow:"),
                window.SystemDecorations == SystemDecorations.None ? (byte)0 : saved.HasShadow);
            if (saved.TitlebarSeparatorStyle is { } separator)
                SetInteger(handle.Handle, Selector("setTitlebarSeparatorStyle:"), separator);
            // 与 Avalonia 的透明模式保持一致，避免透明主题被重新盖上系统底色。
            SetWindowBackground(handle.Handle, window.ActualTransparencyLevel == WindowTransparencyLevel.Transparent);
            if (saved.Backdrop is { } original && original.View == backdrop)
            {
                SetInteger(backdrop, Selector("setMaterial:"), original.Material);
                SetInteger(backdrop, Selector("setState:"), original.State);
                SetObject(backdrop, Selector("setAppearance:"), original.Appearance.DangerousGetHandle());
            }
            saved.Backdrop?.Appearance.Dispose();
            window.ClearValue(SavedStateProperty);
        }
    }

    private static IntPtr FindBackdrop(IntPtr view)
    {
        // 仅寻找公开的背景模糊视图，跳过原生标题栏内部的材质层。
        var children = GetObject(view, Selector("subviews"));
        var count = GetInteger(children, Selector("count"));
        var effectClass = GetClass("NSVisualEffectView");
        for (nint index = 0; index < count; index++)
        {
            var child = GetObjectAtIndex(children, Selector("objectAtIndex:"), (nuint)index);
            if (IsKindOfClass(child, Selector("isKindOfClass:"), effectClass) != 0 &&
                GetInteger(child, Selector("blendingMode")) == BehindWindowBlendingMode)
                return child;
        }
        return IntPtr.Zero;
    }

    private static void SetWindowBackground(IntPtr window, bool transparent) =>
        SetObject(window, Selector("setBackgroundColor:"),
            GetObject(GetClass("NSColor"), Selector(transparent ? "clearColor" : "windowBackgroundColor")));

    // 没有模糊层时必须回到系统实色底，否则整窗透明会显示成白屏。
    private static void RestoreWindowBackground(IntPtr window) => SetWindowBackground(window, transparent: false);

    private sealed record ClipState(double Radius, byte MasksToBounds, byte HasShadow, nint? TitlebarSeparatorStyle, BackdropState? Backdrop);
    private sealed record BackdropState(IntPtr View, nint Material, nint State, RetainedAppearance Appearance);

    private static BackdropState CaptureBackdrop(IntPtr view) => new(view,
        GetInteger(view, Selector("material")), GetInteger(view, Selector("state")),
        new RetainedAppearance(GetObject(view, Selector("appearance"))));

    // 保留原始外观对象，切换主题或更换模糊视图后释放；窗口直接关闭时也能回收。
    private sealed class RetainedAppearance : SafeHandle
    {
        public RetainedAppearance(IntPtr appearance) : base(IntPtr.Zero, ownsHandle: true)
        {
            if (appearance != IntPtr.Zero)
                SetHandle(GetObject(appearance, Selector("retain")));
        }

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle()
        {
            GetObject(handle, Selector("release"));
            return true;
        }
    }

    // AppKit 公共枚举：NSVisualEffectMaterialHUDWindow、NSVisualEffectMaterialSidebar、NSVisualEffectStateActive、NSVisualEffectBlendingModeBehindWindow。
    private const nint ResizableWindowMask = 1 << 3; // NSWindowStyleMaskResizable
    private const nint NoTitlebarSeparator = 1; // NSTitlebarSeparatorStyleNone (macOS 11+)
    private const nint HudWindowMaterial = 13;
    private const nint SidebarMaterial = 7;
    private const nint ActiveEffectState = 1;
    private const nint BehindWindowBlendingMode = 0;

    private const string ObjCLibrary = "/usr/lib/libobjc.dylib";

    [DllImport(ObjCLibrary, EntryPoint = "objc_getClass")]
    private static extern IntPtr GetClass(string name);

    [DllImport(ObjCLibrary, EntryPoint = "sel_registerName")]
    private static extern IntPtr Selector(string name);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern IntPtr GetObject(IntPtr target, IntPtr selector);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern IntPtr GetObjectWithArgument(IntPtr target, IntPtr selector, IntPtr argument);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern IntPtr GetObjectFromUtf8(IntPtr target, IntPtr selector,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern IntPtr GetObjectAtIndex(IntPtr target, IntPtr selector, nuint index);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern byte IsKindOfClass(IntPtr target, IntPtr selector, IntPtr type);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern byte RespondsToSelector(IntPtr target, IntPtr selector, IntPtr method);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint GetInteger(IntPtr target, IntPtr selector);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SetInteger(IntPtr target, IntPtr selector, nint value);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SetObject(IntPtr target, IntPtr selector, IntPtr value);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern double GetDouble(IntPtr target, IntPtr selector);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern byte GetByte(IntPtr target, IntPtr selector);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SetDouble(IntPtr target, IntPtr selector, double value);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SetByte(IntPtr target, IntPtr selector, byte value);
}
