using System;
using System.Globalization;
using System.Threading;
using Avalonia;
using Avalonia.Markup.Xaml;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.Avalonia;
using Splat;
using TSCutter.GUI.Utils;
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
        SplatRegistrations.SetupIOC();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        GC.KeepAlive(typeof(DialogService));
        DialogService.Show(null, MainWindow);

        DetectLanguage();
        base.OnFrameworkInitializationCompleted();
    }
    
    public static MainWindowViewModel MainWindow => Locator.Current.GetService<MainWindowViewModel>()!;
    public static OutputWindowViewModel OutputDialog => Locator.Current.GetService<OutputWindowViewModel>()!;
    public static JumpTimeViewModel JumpTimeDialog => Locator.Current.GetService<JumpTimeViewModel>()!;
    public static AboutWindowViewModel AboutDialog => Locator.Current.GetService<AboutWindowViewModel>()!;
    public static MediainfoWindowViewModel MediainfoDialog => Locator.Current.GetService<MediainfoWindowViewModel>()!;
    public static IDialogService DialogService => Locator.Current.GetService<IDialogService>()!;

    private static void DetectLanguage()
    {
        var currLoc = Thread.CurrentThread.CurrentUICulture.Name;
        var loc = currLoc switch
        {
            "zh-CN" or "zh-SG" => "zh-CN",
            _ when currLoc.StartsWith("zh-") => "zh-TW",
            _ => "en-US"
        };
        
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo(loc);
        Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(loc);
        Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo(loc);
        LocalizationManager.Instance.SwitchLanguage(loc);
    } 
}