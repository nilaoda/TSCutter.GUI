using System;
using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.Views;

public partial class RawCutterWindow : ClassicWindow
{
    public RawCutterWindow()
    {
        InitializeComponent();
        Loaded += OnInitialized;
    }

    private void OnInitialized(object? sender, EventArgs e)
    {
        if (DataContext is RawCutterWindowViewModel vm)
        {
            vm.RequestClose += Close;
        }
    }
}