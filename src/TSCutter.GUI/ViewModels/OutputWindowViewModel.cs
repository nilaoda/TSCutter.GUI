using System;
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
    private double _percent;
    
    public PickedClip? SelectedClip { get; set; }
    public string? OutputPath { get; set; }

    [RelayCommand]
    private async Task OutputAsync()
    {
        try
        {
            await CommonUtil.CopyFileAsync(SelectedClip!.InFileInfo, OutputPath!, SelectedClip!.StartPosition, SelectedClip!.EndPosition, percent => Percent = percent);
            DialogResult = true;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e)
        {
            DialogResult = false;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
    }
    
    public event EventHandler? RequestClose;
}