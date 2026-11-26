using System;
using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels.Application;

namespace TSCutter.GUI.Views.Application;

public partial class UpdatesInfoWindow : ClassicWindow
{
    public UpdatesInfoWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        // The view model is a singleton, so subscribe/unsubscribe with the window lifetime.
        if (DataContext is UpdatesInfoWindowViewModel vm)
            vm.RequestClose += Close;
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        if (DataContext is UpdatesInfoWindowViewModel vm)
            vm.RequestClose -= Close;
    }
}
