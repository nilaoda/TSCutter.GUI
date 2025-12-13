using System;
using Avalonia;
using Avalonia.Markup.Xaml;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.Avalonia;
using Splat;
using TSCutter.GUI.Services;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI;

public partial class App : Application
{
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
        // --- 注册单例服务 ---
        var localizationService = new LocalizationService();
        SplatRegistrations.RegisterConstant<ILocalizationService>(localizationService);
        var configurationService = new ConfigurationService(localizationService);
        SplatRegistrations.RegisterConstant<IConfigurationService>(configurationService);
        SplatRegistrations.SetupIOC();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Locator.Current.GetService<IConfigurationService>()!.Load();

        GC.KeepAlive(typeof(DialogService));
        DialogService.Show(null, MainWindow);

        base.OnFrameworkInitializationCompleted();
    }
    
    public static MainWindowViewModel MainWindow => Locator.Current.GetService<MainWindowViewModel>()!;
    public static OutputWindowViewModel OutputDialog => Locator.Current.GetService<OutputWindowViewModel>()!;
    public static JumpTimeViewModel JumpTimeDialog => Locator.Current.GetService<JumpTimeViewModel>()!;
    public static AboutWindowViewModel AboutDialog => Locator.Current.GetService<AboutWindowViewModel>()!;
    public static MediainfoWindowViewModel MediainfoDialog => Locator.Current.GetService<MediainfoWindowViewModel>()!;
    public static SettingsWindowViewModel SettingsDialog => Locator.Current.GetService<SettingsWindowViewModel>()!;
    public static IDialogService DialogService => Locator.Current.GetService<IDialogService>()!;
    public static ILocalizationService LocalizationService => Locator.Current.GetService<ILocalizationService>()!;
}