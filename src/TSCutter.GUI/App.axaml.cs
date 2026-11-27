using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Classic.CommonControls.Dialogs;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.Avalonia;
using Splat;
using TSCutter.GUI.Converters;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Services.Application.SystemProgress;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Services.FrameSearch;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.Utils.Media;
using TSCutter.GUI.Web;
using TSCutter.GUI.ViewModels;
using TSCutter.GUI.ViewModels.Application;
using TSCutter.GUI.ViewModels.Discs;
using TSCutter.GUI.ViewModels.Editing;
using TSCutter.GUI.ViewModels.FrameSearch;
using TSCutter.GUI.ViewModels.Media;
using TSCutter.GUI.ViewModels.Preview;
using TSCutter.GUI.ViewModels.TransportStream.Analysis;
using TSCutter.GUI.ViewModels.TransportStream.Processing;
using TSCutter.GUI.ViewModels.TransportStream.Repair;

namespace TSCutter.GUI;

public partial class App : Application
{
    public static string CurrentTag => ReleaseInfo.Tag;
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        if (WebUiSession.Current is not null)
        {
            // 离屏弹出的悬停提示只负责显示，鼠标点击应落到提示下方的控件。
            Styles.Add(new Style(selector => selector.OfType<ToolTip>())
            {
                Setters = { new Setter(InputElement.IsHitTestVisibleProperty, false) }
            });
        }
        
        var build = Locator.CurrentMutable;

        build.RegisterLazySingleton(() => (IDialogService)new DialogService(
            new DialogManager(
                viewLocator: new ViewLocator(),
                dialogFactory: WebUiSession.Current is { } webSession
                    ? new WebUiDialogFactory(webSession.Workspace)
                    : new DialogFactory()),
            viewModelFactory: x => Locator.Current.GetService(x)));

        SplatRegistrations.Register<MainWindowViewModel>();
        SplatRegistrations.Register<OutputWindowViewModel>();
        SplatRegistrations.Register<TsExportWarningViewModel>();
        SplatRegistrations.Register<TsTimestampWarningViewModel>();
        SplatRegistrations.Register<ExportSpaceWarningViewModel>();
        SplatRegistrations.Register<ExportPreparationWindowViewModel>();
        SplatRegistrations.Register<RawCutterWindowViewModel>();
        SplatRegistrations.Register<TsCheckWindowViewModel>();
        SplatRegistrations.Register<TsBatchCheckWindowViewModel>();
        SplatRegistrations.Register<TsTimelineRepairWindowViewModel>();
        SplatRegistrations.Register<TsFilterWindowViewModel>();
        SplatRegistrations.Register<TsServiceFilterWindowViewModel>();
        SplatRegistrations.Register<TsMultiSourceRepairWindowViewModel>();
        SplatRegistrations.Register<TsBinaryMergeWindowViewModel>();
        SplatRegistrations.Register<TsPacketViewerWindowViewModel>();
        SplatRegistrations.Register<KeyFrameOverviewWindowViewModel>();
        // 存储方式由宿主选择，模板窗口不感知远程会话。
        var savedFrames = WebUiSession.Current?.SavedFrames ?? new SavedFrameStore();
        build.Register(() => new FrameTemplateManagerWindowViewModel(savedFrames));
        SplatRegistrations.Register<FrameSearchWindowViewModel>();
        SplatRegistrations.Register<ThumbnailSheetWindowViewModel>();
        SplatRegistrations.Register<MediaMuxWindowViewModel>();
        SplatRegistrations.Register<TSCutter.GUI.ViewModels.Player.PlaybackBenchmarkWindowViewModel>();
        SplatRegistrations.Register<TsRemuxWindowViewModel>();
        SplatRegistrations.Register<TsEsExtractorWindowViewModel>();
        SplatRegistrations.Register<AboutWindowViewModel>();
        SplatRegistrations.Register<JumpTimeViewModel>();
        SplatRegistrations.Register<MediainfoWindowViewModel>();
        SplatRegistrations.Register<SettingsWindowViewModel>();
        SplatRegistrations.Register<ExportRecordsWindowViewModel>();
        SplatRegistrations.Register<UpdatesInfoWindowViewModel>();
        SplatRegistrations.Register<CaptureFrameViewModel>();
        SplatRegistrations.Register<ClipDetailsViewModel>();
        SplatRegistrations.Register<ClipPointImportViewModel>();
        SplatRegistrations.Register<RemuxExportOptionsViewModel>();
        SplatRegistrations.Register<TSCutter.GUI.ViewModels.Media.DolbyEOutputWindowViewModel>();
        SplatRegistrations.Register<TSCutter.GUI.ViewModels.Media.ChannelMergeWindowViewModel>();
        SplatRegistrations.Register<DiscTitleSelectionViewModel>();
        // --- 注册单例服务 ---
        var localizationService = new LocalizationService();
        SplatRegistrations.RegisterConstant<ILocalizationService>(localizationService);
        var remoteEditor = WebUiSession.Current is not null;
        var configurationService = new ConfigurationService(localizationService, persistent: !remoteEditor,
            initialConfig: remoteEditor ? WebUiPreferences.ReadSnapshot() : null);
        SplatRegistrations.RegisterConstant<IConfigurationService>(configurationService);
        SplatRegistrations.RegisterConstant<ExportRecordRegistry>(new ExportRecordRegistry());
        SplatRegistrations.SetupIOC();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (OperatingSystem.IsMacOS() &&
            ApplicationLifetime is IClassicDesktopStyleApplicationLifetime macDesktop)
        {
            var shutdownHandler = new MacOSShutdownHandler(
                action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
                () => macDesktop.TryShutdown());
            macDesktop.ShutdownRequested += shutdownHandler.OnShutdownRequested;
        }

        // 自动模式既读取启动时的系统外观，也监听运行期间的变化。
        var configService = Locator.Current.GetService<IConfigurationService>()!;
        if (PlatformSettings is { } platformSettings)
        {
            var themeMonitor = new SystemThemeMonitor(platformSettings, configService);
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Exit += (_, _) => themeMonitor.Dispose();
        }
        else
        {
            AppConfig.IsSystemDarkMode = ActualThemeVariant == ThemeVariant.Dark ||
                                         ActualThemeVariant.InheritVariant == ThemeVariant.Dark;
        }

        // 加载配置
        configService.Load();
        if (WebUiSession.Current is not null)
        {
            if (Environment.GetEnvironmentVariable("TSCUTTER_WEB_THEME") is { } hostTheme)
                configService.ApplyTheme(hostTheme);
            configService.CurrentConfig.AutoCheckForUpdates = false;
            configService.CurrentConfig.RememberRecentItems = false;
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(WebUiPreferences.EnvironmentVariable)))
            {
                if (bool.TryParse(Environment.GetEnvironmentVariable("TSCUTTER_WEB_HW_DECODE"), out var hardwareDecode))
                    configService.CurrentConfig.PreferHardwareDecoding = hardwareDecode;
                configService.CurrentConfig.HardwareDecodingDevice = Environment.GetEnvironmentVariable("TSCUTTER_WEB_HW_DEVICE");
            }
        }
        DialogFolderMemory.Shared = new DialogFolderMemory(configService, BusyCursor.ShowOnActiveWindow);
        FFmpegNativeBootstrapper.Configure(configService.CurrentConfig);

        GC.KeepAlive(typeof(DialogService));
        DialogService.Show(null, MainWindow);
        if (WebUiSession.Current is { } webSession &&
            ApplicationLifetime is IClassicDesktopStyleApplicationLifetime webDesktop)
        {
            var mainWindow = webDesktop.Windows.OfType<Views.MainWindow>().FirstOrDefault()
                ?? throw new InvalidOperationException("The remote editor window did not open.");
            webDesktop.MainWindow = mainWindow;
            webDesktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            webSession.Start(webDesktop, mainWindow);
            base.OnFrameworkInitializationCompleted();
            return;
        }
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime progressDesktop)
        {
            SystemProgressService.Initialize(progressDesktop, () => configService.CurrentConfig.ShowSystemProgress);
            progressDesktop.Exit += (_, _) => WebUiLauncher.StopWithOwner();
        }

        // Clean copies left behind by an earlier update, then report its result.
        UpdateService.CleanupStaleTempFiles();
        _ = ShowPendingUpdateResultAsync();

        // 后台静默检查更新
        if (configService.CurrentConfig.AutoCheckForUpdates)
        {
            _ = CheckUpdatesAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
    
    private void ApplicationAboutMenu_OnClick(object? sender, EventArgs e)
    {
        // 从已显示的窗口取得视图模型，确保对话框服务能找到实际的所有者。
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow?.DataContext is MainWindowViewModel viewModel &&
            viewModel.ShowAboutDialogCommand.CanExecute(null))
            viewModel.ShowAboutDialogCommand.Execute(null);
    }

    private async Task CheckUpdatesAsync()
    {
        await Task.Delay(1000);
        // CheckForUpdateAsync uses the release API and manifest but falls back to the
        // original 302 /latest-tag discovery when either is unavailable.
        var info = await VersionChecker.CheckForUpdateAsync();
        if (info is null || !VersionChecker.IsNewerTag(info.Tag, CurrentTag)) return;
        if (ConfigService?.CurrentConfig.SkippedUpdateVersion == info.Tag) return;
        // Feature gate: without in-place support the original browser prompt is used.
        if (ConfigService?.CurrentConfig.EnableInPlaceUpdates == false)
            info.Asset = null;
        if (Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktopApp) return;
        // Use the view model attached to the visible window: the locator's
        // MainWindowViewModel is a different instance and has no view/dialog owner.
        if (desktopApp.MainWindow?.DataContext is not MainWindowViewModel mainViewModel) return;

        if (!info.CanInstallInPlace)
        {
            // Original behavior, unchanged: offer the releases page when no verified
            // package is available for this platform.
            var result = await MessageBox.ShowDialog(desktopApp.MainWindow!,
                string.Format(LocalizationManager.Instance.String_UpdatesInfo_NewVersionInfo, CurrentTag, info.Tag)
                + Environment.NewLine + Environment.NewLine
                + LocalizationManager.Instance.String_UpdatesInfo_NewVersionAlert,
                LocalizationManager.Instance.String_UpdatesInfo_NewVersionTitle,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (result == MessageBoxResult.Yes)
            {
                mainViewModel.CheckUpdateCommand.Execute(null);
            }
            return;
        }

        mainViewModel.ShowUpdateDialogCommand.Execute(info);
    }

    private static async Task ShowPendingUpdateResultAsync()
    {
        if (WebUiSession.Current is not null)
            return;
        var result = UpdateService.ReadHelperResult();
        if (result is null)
            return;
        UpdateService.ClearHelperResult();
        if (result.Success)
            return;
        if (Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow is null)
            return;
        await MessageBox.ShowDialog(desktop.MainWindow,
            string.Format(LocalizationManager.Instance.String_Update_Error, result.Message),
            LocalizationManager.Instance.String_Update_FailedTitle,
            MessageBoxButtons.Ok, MessageBoxIcon.Error);
    }

    /// <summary>Closes the app after the update helper has been started.</summary>
    public static void ShutdownForUpdate()
    {
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
        else
            Environment.Exit(0);
    }

    public static MainWindowViewModel MainWindow => Locator.Current.GetService<MainWindowViewModel>()!;
    public static OutputWindowViewModel OutputDialog => Locator.Current.GetService<OutputWindowViewModel>()!;
    public static RawCutterWindowViewModel RawCutterDialog => Locator.Current.GetService<RawCutterWindowViewModel>()!;
    public static TsCheckWindowViewModel TsCheckDialog => Locator.Current.GetService<TsCheckWindowViewModel>()!;
    public static TsTimelineRepairWindowViewModel TsTimelineRepairDialog => Locator.Current.GetService<TsTimelineRepairWindowViewModel>()!;
    public static TsFilterWindowViewModel TsFilterDialog => Locator.Current.GetService<TsFilterWindowViewModel>()!;
    public static TsServiceFilterWindowViewModel TsServiceFilterDialog => Locator.Current.GetService<TsServiceFilterWindowViewModel>()!;
    public static TsMultiSourceRepairWindowViewModel TsMultiSourceRepairDialog => Locator.Current.GetService<TsMultiSourceRepairWindowViewModel>()!;
    public static TsBinaryMergeWindowViewModel TsBinaryMergeDialog => Locator.Current.GetService<TsBinaryMergeWindowViewModel>()!;
    public static TsPacketViewerWindowViewModel TsPacketViewerDialog => Locator.Current.GetService<TsPacketViewerWindowViewModel>()!;
    public static TsRemuxWindowViewModel TsRemuxDialog => Locator.Current.GetService<TsRemuxWindowViewModel>()!;
    public static TsEsExtractorWindowViewModel TsEsExtractorDialog => Locator.Current.GetService<TsEsExtractorWindowViewModel>()!;
    public static JumpTimeViewModel JumpTimeDialog => Locator.Current.GetService<JumpTimeViewModel>()!;
    public static AboutWindowViewModel AboutDialog => Locator.Current.GetService<AboutWindowViewModel>()!;
    public static MediainfoWindowViewModel MediainfoDialog => Locator.Current.GetService<MediainfoWindowViewModel>()!;
    public static SettingsWindowViewModel SettingsDialog => Locator.Current.GetService<SettingsWindowViewModel>()!;
    public static UpdatesInfoWindowViewModel UpdatesInfoDialog => Locator.Current.GetService<UpdatesInfoWindowViewModel>()!;
    public static CaptureFrameViewModel CaptureFrameDialog => Locator.Current.GetService<CaptureFrameViewModel>()!;
    public static ThumbnailSheetWindowViewModel ThumbnailSheetDialog => Locator.Current.GetService<ThumbnailSheetWindowViewModel>()!;
    public static InverseBoolConverter InverseBoolConverter => InverseBoolConverter.Instance;
    public static IDialogService DialogService => Locator.Current.GetService<IDialogService>()!;
    public static ILocalizationService LocalizationService => Locator.Current.GetService<ILocalizationService>()!;
    public static IConfigurationService? ConfigService => Locator.Current.GetService<IConfigurationService>();
    // 会话内导出记录：不依赖队列项或片段，切换源文件后仍可查看。
    public static ExportRecordRegistry? ExportRecordRegistry => Locator.Current.GetService<ExportRecordRegistry>();
}
