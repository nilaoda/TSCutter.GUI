using Classic.Avalonia.Theme;
using Avalonia.Interactivity;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.Views;

public partial class MediainfoWindow : ClassicWindow
{
    public MediainfoWindow()
    {
        InitializeComponent();
    }

    private void Window_OnLoaded(object? sender, RoutedEventArgs e) =>
        (DataContext as MediainfoWindowViewModel)?.BuildInfoCommand.Execute(null);
}
