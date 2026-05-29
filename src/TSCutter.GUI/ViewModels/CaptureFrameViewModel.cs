using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;

namespace TSCutter.GUI.ViewModels;

public partial class CaptureFrameViewModel : ViewModelBase, IModalDialogViewModel
{
    [ObservableProperty]
    public partial bool IsSaveToFile { get; set; } = true;

    [ObservableProperty]
    public partial bool IsPngFormat { get; set; } = true;

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Confirm()
    {
        DialogResult = true;
        RequestClose?.Invoke();
    }

    public bool? DialogResult { get; private set; }

    public event Action? RequestClose;
}