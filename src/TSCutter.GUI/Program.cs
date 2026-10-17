using Avalonia;
using System;
using Classic.CommonControls;

namespace TSCutter.GUI;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                // DirectComposition can spin without waiting for VSync on some Windows VMs.
                // Keep the modern GPU path and fall back to the redirection surface instead.
                // Windows 11 使用 DWM 圆角；较旧 Windows 使用直角外框，保留系统阴影。
                // 不设置固定物理像素的模糊层半径，避免与 DPI、最大化状态错位。
                WinUICompositionBackdropCornerRadius = null,
                CompositionMode =
                [
                    Win32CompositionMode.WinUIComposition,
                    Win32CompositionMode.RedirectionSurface
                ]
            })
            .UseMessageBoxSounds()
            .WithInterFont()
            .LogToTrace();
}
