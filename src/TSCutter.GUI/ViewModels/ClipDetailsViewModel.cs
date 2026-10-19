using System;
using System.Collections.Generic;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using TSCutter.GUI.Models;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.ViewModels;

public sealed record ClipColorChoice(string Value, string Name)
{
    public IBrush Brush => ClipLabels.Brush(Value);
    public bool IsNone => string.IsNullOrEmpty(Value);
}

public partial class ClipDetailsViewModel : ViewModelBase, IModalDialogViewModel
{
    [ObservableProperty] private string _clipName = "";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private ClipColorChoice? _selectedColor;
    public IReadOnlyList<ClipColorChoice> Colors { get; } =
    [
        new("", LocalizationManager.Instance.String_ClipDetails_ColorNone),
        new("blue", LocalizationManager.Instance.String_ClipDetails_ColorBlue),
        new("green", LocalizationManager.Instance.String_ClipDetails_ColorGreen),
        new("amber", LocalizationManager.Instance.String_ClipDetails_ColorAmber),
        new("red", LocalizationManager.Instance.String_ClipDetails_ColorRed),
        new("purple", LocalizationManager.Instance.String_ClipDetails_ColorPurple)
    ];
    public bool? DialogResult { get; private set; }
    public event Action? RequestClose;

    [RelayCommand]
    private void Confirm()
    {
        if (!ClipLabels.IsValid(ClipName, Notes, SelectedColor?.Value ?? "")) return;
        DialogResult = true;
        RequestClose?.Invoke();
    }

    [RelayCommand] private void Cancel() => RequestClose?.Invoke();
}
