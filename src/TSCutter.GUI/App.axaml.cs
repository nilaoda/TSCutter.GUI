using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Classic.CommonControls.Dialogs;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.Avalonia;
using Splat;
using TSCutter.GUI.Services;
using TSCutter.GUI.Utils;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI;

public partial class App : Application
{
    public const string CurrentTag = "alphabuild_20251214";
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
        SplatRegistrations.Register<AboutWindowViewModel>();
        SplatRegistrations.Register<JumpTimeViewModel>();
        SplatRegistrations.Register<MediainfoWindowViewModel>();
        SplatRegistrations.Register<SettingsWindowViewModel>();
        SplatRegistrations.Register<UpdatesInfoWindowViewModel>();
        // --- 注册单例服务 ---
        var localizationService = new LocalizationService();
        SplatRegistrations.RegisterConstant<ILocalizationService>(localizationService);
        var configurationService = new ConfigurationService(localizationService);
        SplatRegistrations.RegisterConstant<IConfigurationService>(configurationService);
        SplatRegistrations.SetupIOC();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var configService = Locator.Current.GetService<IConfigurationService>()!;
        configService.Load();

        GC.KeepAlive(typeof(DialogService));
        DialogService.Show(null, MainWindow);

        // 后台静默检查更新
        if (configService.CurrentConfig.AutoCheckForUpdates)
        {
            _ = CheckUpdatesAsync();
        }
        base.OnFrameworkInitializationCompleted();
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
        if (latestTag != CurrentTag && Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopApp)
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
    public static JumpTimeViewModel JumpTimeDialog => Locator.Current.GetService<JumpTimeViewModel>()!;
    public static AboutWindowViewModel AboutDialog => Locator.Current.GetService<AboutWindowViewModel>()!;
    public static MediainfoWindowViewModel MediainfoDialog => Locator.Current.GetService<MediainfoWindowViewModel>()!;
    public static SettingsWindowViewModel SettingsDialog => Locator.Current.GetService<SettingsWindowViewModel>()!;
    public static UpdatesInfoWindowViewModel UpdatesInfoDialog => Locator.Current.GetService<UpdatesInfoWindowViewModel>()!;
    public static IDialogService DialogService => Locator.Current.GetService<IDialogService>()!;
    public static ILocalizationService LocalizationService => Locator.Current.GetService<ILocalizationService>()!;
}