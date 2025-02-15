using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.Messaging;
using FluentAvalonia.Styling;
using FluentAvalonia.UI.Media;
using FluentAvalonia.UI.Windowing;
using TSCutter.GUI.Models;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.Views;

// https://dccn.blob.core.windows.net/2022/Slide/%E5%BC%80%E6%BA%90_B6_%E5%91%A8%E6%9D%B0_.NET%20%E7%8E%A9%E8%BD%AC%E9%9F%B3%E8%A7%86%E9%A2%91%E6%93%8D%E4%BD%9C%20FFmpeg.pdf

public partial class MainWindow : AppWindow
{
    private MainWindowViewModel ViewModel => (DataContext as MainWindowViewModel)!;
    
    public MainWindow()
    {
        InitializeComponent();
        TitleBar.ExtendsContentIntoTitleBar = true;
        TitleBar.TitleBarHitTestType = TitleBarHitTestType.Complex;
        Application.Current.ActualThemeVariantChanged += OnActualThemeVariantChanged;
        
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
        
        Console.WriteLine($"Seek to {e}");
        await ViewModel.SeekToTimeAsync(TimeSpan.FromSeconds(e));
        await ViewModel.DrawNextFrameAsync(1);
    }
    
    private void OnActualThemeVariantChanged(object sender, EventArgs e)
    {
        if (IsWindows11)
        {
            if (ActualThemeVariant != FluentAvaloniaTheme.HighContrastTheme)
            {
                TryEnableMicaEffect();
            }
            else
            {
                ClearValue(BackgroundProperty);
                ClearValue(TransparencyBackgroundFallbackProperty);
            }
        }
    }

    private void TryEnableMicaEffect()
    {
        return;
        // TransparencyBackgroundFallback = Brushes.Transparent;
        // TransparencyLevelHint = WindowTransparencyLevel.Mica;

        // The background colors for the Mica brush are still based around SolidBackgroundFillColorBase resource
        // BUT since we can't control the actual Mica brush color, we have to use the window background to create
        // the same effect. However, we can't use SolidBackgroundFillColorBase directly since its opaque, and if
        // we set the opacity the color become lighter than we want. So we take the normal color, darken it and 
        // apply the opacity until we get the roughly the correct color
        // NOTE that the effect still doesn't look right, but it suffices. Ideally we need access to the Mica
        // CompositionBrush to properly change the color but I don't know if we can do that or not
        if (ActualThemeVariant == ThemeVariant.Dark)
        {
            var color = this.TryFindResource("SolidBackgroundFillColorBase",
                ThemeVariant.Dark, out var value) ? (Color2)(Color)value : new Color2(32, 32, 32);

            color = color.LightenPercent(-0.8f);

            Background = new ImmutableSolidColorBrush(color, 0.9);
        }
        else if (ActualThemeVariant == ThemeVariant.Light)
        {
            // Similar effect here
            var color = this.TryFindResource("SolidBackgroundFillColorBase",
                ThemeVariant.Light, out var value) ? (Color2)(Color)value : new Color2(243, 243, 243);

            color = color.LightenPercent(0.5f);

            Background = new ImmutableSolidColorBrush(color, 0.9);
        }
    } 
}