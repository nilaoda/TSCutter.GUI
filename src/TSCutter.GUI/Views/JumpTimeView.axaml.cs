using System;
using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.Views;

public partial class JumpTimeView : ClassicWindow
{
    public JumpTimeView()
    {
        InitializeComponent();
        Loaded += OnInitialized;
    }
    
    private void OnInitialized(object? sender, EventArgs e)
    {
        // 订阅关闭请求
        if (DataContext is JumpTimeViewModel vm)
        {
            vm.RequestClose += Close;
        }
    }
}