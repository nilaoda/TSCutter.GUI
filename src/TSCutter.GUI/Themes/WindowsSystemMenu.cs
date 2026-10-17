using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Threading;
using Classic.Avalonia.Theme;

namespace TSCutter.GUI.Themes;

/// <summary>显示 HWND 自带的系统菜单，外观、文案和窗口命令由宿主 Windows 提供。</summary>
internal static class WindowsSystemMenu
{
    internal enum Command : uint
    {
        Size = 0xF000,
        Move = 0xF010,
        Minimize = 0xF020,
        Maximize = 0xF030,
        Close = 0xF060,
        Restore = 0xF120
    }

    public static bool TryShow(ClassicWindow window, Point point, Func<Command, bool> canExecute)
    {
        if (!OperatingSystem.IsWindows() ||
            window.TryGetPlatformHandle() is not { HandleDescriptor: "HWND" } handle ||
            GetSystemMenu(handle.Handle, false) == IntPtr.Zero)
            return false;

        var screenPoint = window.PointToScreen(point);
        // ContextRequested 发生在输入事件派发期间，等右键释放和捕获清理后再进入原生菜单循环。
        Dispatcher.UIThread.Post(() =>
        {
            if (window.IsVisible && window.WindowState != Avalonia.Controls.WindowState.FullScreen &&
                window.TryGetPlatformHandle()?.Handle == handle.Handle)
                Show(handle.Handle, screenPoint, canExecute);
        });
        return true;
    }

    private static void Show(IntPtr window, PixelPoint point, Func<Command, bool> canExecute)
    {
        var menu = GetSystemMenu(window, false);
        if (menu == IntPtr.Zero)
            return;

        var commands = Enum.GetValues<Command>();
        var states = new uint[commands.Length];
        var defaultItem = GetMenuDefaultItem(menu, false, 1); // GMDI_USEDISABLED
        try
        {
            for (var i = 0; i < commands.Length; i++)
            {
                states[i] = GetMenuState(menu, (uint)commands[i], 0); // MF_BYCOMMAND
                // NoChrome 会禁用原生 Close；显示期间按实际标题按钮恢复可用状态。
                EnableMenuItem(menu, (uint)commands[i], canExecute(commands[i]) ? 0u : 1u); // MF_ENABLED / MF_GRAYED
            }
            SetMenuDefaultItem(menu, (uint)Command.Close, false);
            SetForegroundWindow(window);
            var selected = (Command)TrackPopupMenuEx(menu, 0x0102, point.X, point.Y, window, IntPtr.Zero); // TPM_RETURNCMD | TPM_RIGHTBUTTON
            // 原生菜单循环中可能改变窗口能力，执行前再次检查；保留 Closing 取消等平台行为。
            if (Enum.IsDefined(selected) && canExecute(selected) && IsWindow(window))
                SendMessage(window, 0x0112, (nuint)selected, PackPoint(point)); // WM_SYSCOMMAND
        }
        finally
        {
            // HMENU 由系统拥有，不能 DestroyMenu；离开后恢复框架原有状态和默认项。
            if (IsWindow(window))
            {
                for (var i = 0; i < commands.Length; i++)
                    if (states[i] != uint.MaxValue)
                        EnableMenuItem(menu, (uint)commands[i], states[i] & 3u);
                SetMenuDefaultItem(menu, defaultItem, false);
                PostMessage(window, 0, 0, IntPtr.Zero); // WM_NULL，让取消菜单后的激活、关闭处理继续进行。
            }
        }
    }

    private static IntPtr PackPoint(PixelPoint point) =>
        new(unchecked((int)(((uint)point.Y & 0xFFFFu) << 16 | ((uint)point.X & 0xFFFFu))));

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetSystemMenu(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool revert);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetMenuState(IntPtr menu, uint item, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint EnableMenuItem(IntPtr menu, uint item, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetMenuDefaultItem(IntPtr menu, [MarshalAs(UnmanagedType.Bool)] bool byPosition, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
    private static extern IntPtr SendMessage(IntPtr window, uint message, nuint wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, nuint wParam, IntPtr lParam);
}
