using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Utils.Editing;
using TSCutter.GUI.ViewModels.Editing;

namespace TSCutter.GUI.ViewModels;

public partial class MainWindowViewModel
{
    [RelayCommand(CanExecute = nameof(CanEditClipDetails))]
    private async Task EditClipDetailsAsync(PickedClip? clip)
    {
        if (!CanEditClipDetails(clip)) return;
        var dialog = _dialogService.CreateViewModel<ClipDetailsViewModel>();
        dialog.ClipName = clip!.Name;
        dialog.Notes = clip.Notes;
        dialog.SelectedColor = dialog.Colors.First(color => color.Value == clip.Color);
        await _dialogService.ShowDialogAsync(this, dialog);
        if (dialog.DialogResult == true && CanEditClipDetails(clip))
            UpdateClipDetails(clip, dialog.ClipName, dialog.Notes, dialog.SelectedColor?.Value ?? "");
    }

    private bool CanEditClipDetails(PickedClip? clip) => CanEditVideo && clip is not null && Clips.Contains(clip);

    internal void UpdateClipDetails(PickedClip clip, string name, string notes, string color)
    {
        if (!CanEditClipDetails(clip) || !ClipLabels.IsValid(name, notes, color)) return;
        name = name.Trim();
        notes = notes.Trim();
        if (clip.Name == name && clip.Notes == notes && clip.Color == color) return;
        RecordHistory();
        clip.Name = name;
        clip.Notes = notes;
        clip.Color = color;
        NotifyClipSelectionChanged();
    }

    private string OutputNameTemplate => ClipOutputNaming.IsValidTemplate(_configService.CurrentConfig.ClipOutputTemplate)
        ? _configService.CurrentConfig.ClipOutputTemplate : ClipOutputNaming.DefaultTemplate;

    private string CreateClipOutputName(PickedClip clip) => ClipOutputNaming.Generate(
        OutputNameTemplate, clip.InFileInfo.FullName, clip.Name,
        Math.Max(1, Clips.IndexOf(clip) + 1), clip.StartTime, clip.EndTime, ClipExtension);
}
