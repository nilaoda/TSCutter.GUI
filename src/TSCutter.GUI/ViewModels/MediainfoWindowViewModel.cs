using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.ViewModels;

public partial class MediainfoWindowViewModel : ViewModelBase, IModalDialogViewModel
{
    public string FilePath { get; set; } = string.Empty;
    public bool? DialogResult { get; }
    
    [ObservableProperty]
    private string _infoText = "Loading...";
    
    [ObservableProperty]
    private string _btnContent = "_Copy All";

    [RelayCommand]
    private async Task CopyAsync()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopApp)
        {
            var success = false;
            try
            {
                await desktopApp.MainWindow!.Clipboard!.SetTextAsync(InfoText);
                success = true;
            }
            finally
            {
                if (success)
                {
                    BtnContent = "Copied!";
                    _ = Task.Delay(1000).ContinueWith(_ =>
                    {
                        Dispatcher.UIThread.Post(() => BtnContent = "_Copy All");
                    });
                }
            }
        }
    }

    [RelayCommand]
    private async Task BuildInfoAsync()
    {
        InfoText = "Loading...";
        await Task.Run(() =>
        {
            try
            {
                var result = MediaInfoBuilder.Build(FilePath);
                Dispatcher.UIThread.Post(() => InfoText = result);
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => InfoText = $"Failed to load media info:\n{ex}");
            }
        });
    }
}