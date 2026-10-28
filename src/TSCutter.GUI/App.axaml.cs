using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Classic.CommonControls.Dialogs;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.Avalonia;
using Splat;
using TSCutter.GUI.Converters;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.Utils.Media;
using TSCutter.GUI.ViewModels;
using TSCutter.GUI.ViewModels.Application;
using TSCutter.GUI.ViewModels.Discs;
using TSCutter.GUI.ViewModels.Editing;
using TSCutter.GUI.ViewModels.FrameSearch;
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
        
        var build = Locator.CurrentMutable;

        build.RegisterLazySingleton(() => (IDialogService)new DialogService(
            new DialogManager(
                viewLocator: new ViewLocator(),
                dialogFactory: new DialogFactory()),
            viewModelFactory: x => Locator.Current.GetService(x)));

        SplatRegistrations.Register<MainWindowViewModel>();
        SplatRegistrations.Register<OutputWindowViewModel>();
        SplatRegistrations.Register<TsExportWarningViewModel>();
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
        SplatRegistrations.Register<FrameTemplateManagerWindowViewModel>();
        SplatRegistrations.Register<FrameSearchWindowViewModel>();
        SplatRegistrations.Register<ThumbnailSheetWindowViewModel>();
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
        SplatRegistrations.Register<RemuxExportOptionsViewModel>();
        SplatRegistrations.Register<DiscTitleSelectionViewModel>();
        // --- 注册单例服务 ---
        var localizationService = new LocalizationService();
        SplatRegistrations.RegisterConstant<ILocalizationService>(localizationService);
        var configurationService = new ConfigurationService(localizationService);
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
        FFmpegNativeBootstrapper.Configure(configService.CurrentConfig);

        GC.KeepAlive(typeof(DialogService));
        DialogService.Show(null, MainWindow);

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
        // var info = await VersionChecker.CheckUpdateAsync();
        // if (info != null && info.TagName != CurrentTag)
        // {
        //     var viewModel = _dialogService.CreateViewModel<UpdatesInfoWindowViewModel>();
        //     await _dialogService.ShowDialogAsync(this, viewModel);
        // }
        var latestTag = await VersionChecker.GetLatestTagAsync();
        if (VersionChecker.IsNewerTag(latestTag, CurrentTag) && Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopApp)
        {
            var result = await MessageBox.ShowDialog(desktopApp.MainWindow!,
                string.Format(LocalizationManager.Instance.String_UpdatesInfo_NewVersionInfo, CurrentTag, latestTag)
                + Environment.NewLine + Environment.NewLine
                + LocalizationManager.Instance.String_UpdatesInfo_NewVersionAlert,
                LocalizationManager.Instance.String_UpdatesInfo_NewVersionTitle,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (result == MessageBoxResult.Yes)
            {
                MainWindow.CheckUpdateCommand.Execute(null);
            }
        }
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
