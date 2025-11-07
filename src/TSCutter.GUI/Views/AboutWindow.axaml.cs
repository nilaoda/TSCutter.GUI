using Avalonia.Interactivity;
using Classic.Avalonia.Theme;

namespace TSCutter.GUI.Views;

public partial class AboutWindow : ClassicWindow
{
    public AboutWindow()
    {
        InitializeComponent();
    }

    private void Button_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}