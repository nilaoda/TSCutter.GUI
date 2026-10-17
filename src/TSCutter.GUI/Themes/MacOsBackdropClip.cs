using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace TSCutter.GUI.Themes;

/// <summary>调整系统玻璃材质，并让模糊层、阴影与应用绘制使用相同的窗口轮廓。</summary>
internal sealed class MacOsBackdropClip : AvaloniaObject
{
    private static readonly AttachedProperty<ClipState?> SavedStateProperty =
        AvaloniaProperty.RegisterAttached<MacOsBackdropClip, Window, ClipState?>("SavedState");

    public static void Update(Window window, double? radius, bool useAeroMaterial = true)
    {
        if (!OperatingSystem.IsMacOS() || !window.IsLoaded ||
            window.TryGetPlatformHandle() is not { HandleDescriptor: "NSWindow" } handle)
        {
            window.Classes.Remove("macos-native-corners");
            return;
        }

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
            SetByte(handle.Handle, Selector("setHasShadow:"), value > 0 ? (byte)1 : (byte)0);
            // Avalonia 在模糊模式下仍设置不透明的系统底色，裁剪之外会留下浅色方角。
            SetWindowBackground(handle.Handle, transparent: true);
            if (saved.Backdrop is { } original && original.View == backdrop)
            {
                // 现代主题用侧栏材质近似云母，避免系统标题栏材质将背景压成实色；失焦仍跟随窗口状态。
                SetInteger(backdrop, Selector("setMaterial:"), useAeroMaterial ? HudWindowMaterial : SidebarMaterial);
                SetInteger(backdrop, Selector("setState:"), useAeroMaterial ? ActiveEffectState : original.State);
                // 仅调整模糊层的明暗，避免应用主题与系统外观不一致时出现白色或黑色底层。
                var appearanceName = GetObjectFromUtf8(GetClass("NSString"), Selector("stringWithUTF8String:"),
                    useAeroMaterial || window.ActualThemeVariant == CustomTheme.Windows11Dark
                        ? "NSAppearanceNameDarkAqua" : "NSAppearanceNameAqua");
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
            SetByte(handle.Handle, Selector("setHasShadow:"), saved.HasShadow);
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

    private sealed record ClipState(double Radius, byte MasksToBounds, byte HasShadow, BackdropState? Backdrop);
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
