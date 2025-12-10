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
    private string _infoText = LocalizationManager.Instance.String_MediaInfo_Loading;
    
    [ObservableProperty]
    private string _btnContent = LocalizationManager.Instance.String_MediaInfo_CopyAll;

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
                    BtnContent = LocalizationManager.Instance.String_MediaInfo_Copied;
                    _ = Task.Delay(1000).ContinueWith(_ =>
                    {
                        Dispatcher.UIThread.Post(() => BtnContent = LocalizationManager.Instance.String_MediaInfo_CopyAll);
                    });
                }
            }
        }
    }

    [RelayCommand]
    private async Task BuildInfoAsync()
    {
        InfoText = LocalizationManager.Instance.String_MediaInfo_Loading;
        await Task.Run(() =>
        {
            try
            {
                var result = MediaInfoBuilder.Build(FilePath);
                Dispatcher.UIThread.Post(() => InfoText = result);
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => InfoText = string.Format(LocalizationManager.Instance.String_MediaInfo_Failed, ex.Message));
            }
        });
    }
}