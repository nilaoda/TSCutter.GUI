using System;
using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.Views;

public partial class CaptureFrameView : ClassicWindow
{
    public CaptureFrameView()
    {
        InitializeComponent();
        Loaded += OnInitialized;
    }

    private void OnInitialized(object? sender, EventArgs e)
    {
        if (DataContext is CaptureFrameViewModel vm)
        {
            vm.RequestClose += Close;
        }
    }
}