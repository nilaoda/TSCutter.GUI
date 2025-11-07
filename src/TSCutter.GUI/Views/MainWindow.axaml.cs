using System;
using Avalonia.Controls;
using Classic.Avalonia.Theme;
using CommunityToolkit.Mvvm.Messaging;
using TSCutter.GUI.Models;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.Views;

// https://dccn.blob.core.windows.net/2022/Slide/%E5%BC%80%E6%BA%90_B6_%E5%91%A8%E6%9D%B0_.NET%20%E7%8E%A9%E8%BD%AC%E9%9F%B3%E8%A7%86%E9%A2%91%E6%93%8D%E4%BD%9C%20FFmpeg.pdf

public partial class MainWindow : ClassicWindow
{
    private MainWindowViewModel ViewModel => (DataContext as MainWindowViewModel)!;
    
    public MainWindow()
    {
        InitializeComponent();

        // 监听 ViewModel 发送的 FitMessage
        WeakReferenceMessenger.Default.Register<FitMessage>(this, (r, m) =>
        {
            if (ImageViewer.FitCommand.CanExecute(null))
            {
                ImageViewer.FitCommand.Execute(null);
            }
        });
    }

    private void Window_OnClosing(object? sender, WindowClosingEventArgs e)
    {
        ViewModel.Close();
    }

    private async void CustomSlider_OnValueChangedAfterMouseUp(object? sender, double e)
    {
        if (!ViewModel.IsVideoInitialized) return;

        try
        {
            CustomSlider.IsEnabled = false;
            Console.WriteLine($"Seek to {e}");
            await ViewModel.SeekToTimeAsync(TimeSpan.FromSeconds(e));
            await ViewModel.DrawNextFrameAsync(1);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
        }
        finally
        {
            CustomSlider.IsEnabled = true;
        }
    }
}