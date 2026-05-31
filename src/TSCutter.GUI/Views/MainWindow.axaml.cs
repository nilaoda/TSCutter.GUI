using System;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Classic.Avalonia.Theme;
using CommunityToolkit.Mvvm.Messaging;
using TSCutter.GUI.Models;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.Views;

public partial class MainWindow : ClassicWindow
{
    private MainWindowViewModel ViewModel => (DataContext as MainWindowViewModel)!;
    
    public MainWindow()
    {
        InitializeComponent();

        WeakReferenceMessenger.Default.Register<FitMessage>(this, (r, m) =>
        {
            if (ImageViewer.FitCommand.CanExecute(null))
            {
                ImageViewer.FitCommand.Execute(null);
            }
        });

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.Clips.CollectionChanged += OnClipsCollectionChanged;
            }
        };
    }

    private void OnClipsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            Dispatcher.UIThread.Post(() =>
            {
                ClipsScrollViewer.ScrollToEnd();
            }, DispatcherPriority.Loaded);
        }
    }

    private void Window_OnClosing(object? sender, WindowClosingEventArgs e)
    {
        ViewModel.Close();
    }

    private void ClipCard_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { DataContext: PickedClip clip })
        {
            ViewModel.SelectClip(clip);
        }
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
