using System;
using System.IO;
using Avalonia.Interactivity;
using Classic.Avalonia.Theme;
using TSCutter.GUI.Utils;

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

    private void Notices_OnClick(object? sender, RoutedEventArgs e)
    {
        // 定位随发布包提供的声明文件，不在窗口打开时读取许可文本。
        CommonUtil.OpenFileLocation(Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.txt"));
    }
}
