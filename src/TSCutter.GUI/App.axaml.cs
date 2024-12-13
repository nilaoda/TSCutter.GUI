using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.Avalonia;
using Splat;
using TSCutter.GUI.ViewModels;
using TSCutter.GUI.Views;

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
                dialogFactory: new DialogFactory().AddFluent(FluentMessageBoxType.ContentDialog)),
            viewModelFactory: x => Locator.Current.GetService(x)));

        SplatRegistrations.Register<MainWindowViewModel>();
        SplatRegistrations.Register<OutputWindowViewModel>();
        SplatRegistrations.Register<AboutWindowViewModel>();
        SplatRegistrations.Register<JumpTimeViewModel>();
        SplatRegistrations.SetupIOC();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        GC.KeepAlive(typeof(DialogService));
        DialogService.Show(null, MainWindow);

        base.OnFrameworkInitializationCompleted();
    }
    
    public static MainWindowViewModel MainWindow => Locator.Current.GetService<MainWindowViewModel>()!;
    public static OutputWindowViewModel OutputDialog => Locator.Current.GetService<OutputWindowViewModel>()!;
    public static JumpTimeViewModel JumpTimeDialog => Locator.Current.GetService<JumpTimeViewModel>()!;
    public static AboutWindowViewModel AboutDialog => Locator.Current.GetService<AboutWindowViewModel>()!;
    public static IDialogService DialogService => Locator.Current.GetService<IDialogService>()!;

}