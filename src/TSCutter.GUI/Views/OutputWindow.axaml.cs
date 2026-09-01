using System;
using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.Views;

public partial class OutputWindow : ClassicWindow
{
    public OutputWindow()
    {
        InitializeComponent();
        Loaded += OnInitialized;
    }
    
    private void OnInitialized(object? sender, EventArgs e)
    {
        if (DataContext is OutputWindowViewModel vm)
        {
            vm.RequestClose += Close;
            if (vm.IsBatchMode)
                Height = 280;
        }
    }
}
