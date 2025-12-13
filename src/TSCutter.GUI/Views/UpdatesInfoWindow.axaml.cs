using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.Views;

public partial class UpdatesInfoWindow : ClassicWindow
{
    public UpdatesInfoWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            // 订阅关闭请求
            if (DataContext is UpdatesInfoWindowViewModel vm)
            {
                vm.RequestClose += Close;
            }
        };
    }
}