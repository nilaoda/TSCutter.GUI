using Avalonia;
using System;
using System.Linq;
using Classic.CommonControls;
using Avalonia.Headless;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.Utils.Files;
using TSCutter.GUI.Web;

namespace TSCutter.GUI;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        // Hidden helper used by in-place updates on Windows: a copy of the running
        // executable replaces the original after the main process has exited.
        if (args.Length >= 5 && args[0] == "--apply-update")
            return UpdateService.RunWindowsHelper(args[1], args[2], args[3], args[4], args.Skip(5).ToArray());
#if TSCUTTER_GRAPHICS_STATIC
        if (args.Length == 1 && args[0] == "--verify-static-graphics")
            return Utils.Media.NativeGraphicsSmokeTest.Run();
#endif
#if TSCUTTER_FFMPEG_STATIC
        if (args.Length > 0 && args[0] == "--verify-static-ffmpeg")
            return args.Length == 2 ? FFmpeg.FFmpegStaticSmokeTest.Run(args[1]) : 2;
#endif
        if (args is ["--web-ui", ..])
        {
            try
            {
                var session = WebUiSession.Configure(args);
                MediaFileAccess.ConfigureForProcess(new MediaFileAccess(session.Workspace, LocalizeWorkspaceError));
            }
            catch (Exception error) when (error is ArgumentException or System.IO.IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine(error.Message);
                return 2;
            }

            return AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .UseWebUiRendering()
                .WithInterFont()
                .With(CreateFontManagerOptions())
                .StartWithClassicDesktopLifetime([]);
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static string LocalizeWorkspaceError(MediaFileAccessError error)
    {
        var strings = LocalizationManager.Instance;
        return error switch
        {
            MediaFileAccessError.InputOutsideWorkspace => strings.String_WebUi_MediaOutside,
            MediaFileAccessError.OutputOutsideWorkspace => strings.String_WebUi_OutputOutside,
            MediaFileAccessError.ProjectOutsideWorkspace => strings.String_WebUi_ProjectOutside,
            MediaFileAccessError.OutputExists => strings.String_WebUi_OutputExists,
            MediaFileAccessError.OutputMatchesSource => strings.String_TsFilter_Error_SameFile,
            _ => throw new ArgumentOutOfRangeException(nameof(error))
        };
    }

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
            .With(CreateFontManagerOptions())
            .LogToTrace();

    internal static Avalonia.Media.FontManagerOptions CreateFontManagerOptions() => new()
    {
        FontFallbacks = OperatingSystem.IsMacOS() ? new Themes.MacOSFontFallbacks() : null
    };
}
