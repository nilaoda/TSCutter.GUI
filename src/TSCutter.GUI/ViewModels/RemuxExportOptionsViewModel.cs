using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using TSCutter.GUI.Models;

namespace TSCutter.GUI.ViewModels;

public partial class RemuxExportTrackChoice : ObservableObject
{
    public int Index { get; init; }
    public string Description { get; init; } = "";
    public bool CanChange { get; init; } = true;
    [ObservableProperty] private bool _isSelected;
}

public partial class RemuxExportOptionsViewModel : ViewModelBase, IModalDialogViewModel
{
    public IReadOnlyList<string> Containers { get; } = ["mp4", "mkv"];
    [ObservableProperty] private string _container = "mp4";
    public List<RemuxExportTrackChoice> Tracks { get; } = [];
    public bool? DialogResult { get; private set; }
    public event Action? RequestClose;

    [RelayCommand] private void Confirm() { DialogResult = true; RequestClose?.Invoke(); }
    [RelayCommand] private void Cancel() => RequestClose?.Invoke();

    internal RemuxExportSettings Apply(RemuxExportSettings source)
    {
        var result = source.Snapshot();
        result.Container = Container;
        result.StreamIndices = Tracks.Where(track => track.IsSelected).Select(track => track.Index).ToList();
        return result;
    }
}
