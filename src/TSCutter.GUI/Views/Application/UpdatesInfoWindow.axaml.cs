using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels.Application;

namespace TSCutter.GUI.Views.Application;

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