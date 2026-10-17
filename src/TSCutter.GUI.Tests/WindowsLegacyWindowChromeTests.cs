using TSCutter.GUI.Themes;
using Xunit;

namespace TSCutter.GUI.Tests;

public sealed class WindowsLegacyWindowChromeTests
{
    [Theory]
    [InlineData(0x10870000u)] // BorderOnly retains WS_BORDER but removes WS_SYSMENU.
    [InlineData(0x11CF0000u)] // Maximized.
    [InlineData(0x30CF0000u)] // Minimized.
    public void SystemMenuIsRestoredWithoutChangingNativeFrameOrWindowActions(uint style)
    {
        const uint resize = 0x00040000, menu = 0x00080000, minimize = 0x00020000, maximize = 0x00010000;
        const uint extendedStyle = 0x00240100; // No-redirection surface, taskbar entry, native raised edge.
        var result = WindowsLegacyWindowChrome.PreserveSystemMenu(style, extendedStyle);
        Assert.Equal(style | menu, result.style);
        Assert.Equal(resize | menu | minimize | maximize, result.style & (resize | menu | minimize | maximize));
        Assert.Equal(style & 0x21000000u, result.style & 0x21000000u);
        Assert.Equal(extendedStyle, result.exStyle);
    }
}
