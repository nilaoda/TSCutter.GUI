using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using TSCutter.GUI.Models;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.ViewModels;

public partial class OutputWindowViewModel : ViewModelBase, IModalDialogViewModel
{
    [ObservableProperty]
    public partial bool? DialogResult { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentStr))]
    public partial double Percent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedStr))]
    public partial double Speed { get; set; }

    public string PercentStr => $"{Percent:0.00}%";
    public string SpeedStr => $"{CommonUtil.FormatFileSize(Speed)}/s";
    public PickedClip? SelectedClip { get; set; }
    public string? OutputPath { get; set; }
    public Exception? Exception { get; private set; }
    
    private CancellationTokenSource _cts = new();
    private DateTime _lastUpdateTime;
    private long _bytesCopied = 0;

    [RelayCommand]
    private async Task OutputAsync()
    {
        try
        {
            _lastUpdateTime = DateTime.Now;
            await CommonUtil.CopyFileAsync(SelectedClip!.InFileInfo, OutputPath!, SelectedClip!.StartPosition, SelectedClip!.EndPosition,
                (percent, bytesCopied) =>
                {
                    Percent = percent;
                    _bytesCopied += bytesCopied;
                    UpdateSpeed();
                }, _cts.Token);
            DialogResult = true;
            RequestClose?.Invoke();
        } 
        catch (OperationCanceledException)
        {
            Console.WriteLine("File copy was canceled.");
        }
        catch (Exception e)
        {
            Exception = e;
            DialogResult = false;
            RequestClose?.Invoke();
        }
    }
    
    private void UpdateSpeed()
    {
        var elapsedTime = (DateTime.Now - _lastUpdateTime).TotalSeconds;
        if (elapsedTime < 1) return; // per second
        Speed = _bytesCopied / elapsedTime;
        _lastUpdateTime = DateTime.Now;
        _bytesCopied = 0;
    }

    [RelayCommand]
    private void CancelOutput()
    {
        if (DialogResult is not null) return;
        
        _cts.Cancel();
        DialogResult = true;
        RequestClose?.Invoke();
    }
    
    public void OnClosing(CancelEventArgs e)
    {
        CancelOutput();
    }

    public event Action? RequestClose;
}