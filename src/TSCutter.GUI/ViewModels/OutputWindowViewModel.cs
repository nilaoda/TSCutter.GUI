using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using TSCutter.GUI.Models;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.ViewModels;

public partial class OutputWindowViewModel : ViewModelBase, IModalDialogViewModel, ICloseable
{
    [ObservableProperty]
    private bool? _dialogResult;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private double _percent;

    public string Title => $"Output File - {Percent:0.00}%";
    public PickedClip? SelectedClip { get; set; }
    public string? OutputPath { get; set; }
    
    private CancellationTokenSource _cts = new();

    [RelayCommand]
    private async Task OutputAsync()
    {
        try
        {
            await CommonUtil.CopyFileAsync(SelectedClip!.InFileInfo, OutputPath!, SelectedClip!.StartPosition, SelectedClip!.EndPosition, percent => Percent = percent, _cts.Token);
            DialogResult = true;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("File copy was canceled.");
        }
        catch (Exception e)
        {
            DialogResult = false;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private void CancelOutput()
    {
        if (DialogResult is not null) return;
        
        _cts.Cancel();
        DialogResult = true;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }
    
    public event EventHandler? RequestClose;
}