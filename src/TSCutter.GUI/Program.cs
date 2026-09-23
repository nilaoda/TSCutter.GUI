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
