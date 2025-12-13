using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.Views;

public partial class SettingsWindow : ClassicWindow
{
    public SettingsWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            // 订阅关闭请求
            if (DataContext is SettingsWindowViewModel vm)
            {
                vm.RequestClose += Close;
            }
        };
    }
}