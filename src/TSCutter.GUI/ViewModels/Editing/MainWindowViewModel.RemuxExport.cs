using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Utils.Application;

namespace TSCutter.GUI.ViewModels.Editing;

public partial class MainWindowViewModel
{
    private RemuxExportSettings? _remuxExportSettings;
    private long _exportSettingsRevision;

    private void InvalidateClipExports()
    {
        _exportSettingsRevision++;
        foreach (var clip in Clips) clip.InvalidateExport();
    }
    public bool IsRemuxExport => _videoInstance is { Inited: true, CanRemuxClip: true };
    private RemuxExportSettings? CurrentRemuxSettings => IsRemuxExport
        ? _remuxExportSettings ??= _videoInstance!.CreateExportSettings() : null;
    private string ExportFileFilterName => IsRemuxExport
        ? LocalizationManager.Instance.String_RemuxExport_Files : LocalizationManager.Instance.String_TsFiles;
    private bool CanConfigureRemuxExport => CanEditVideo && IsRemuxExport;

    [RelayCommand(CanExecute = nameof(CanConfigureRemuxExport))]
    private async Task RemuxExportOptionsAsync()
    {
        var video = _videoInstance;
        var settings = CurrentRemuxSettings!.Snapshot();
        var dialog = _dialogService.CreateViewModel<RemuxExportOptionsViewModel>();
        dialog.Initialize(settings, video!.ExportTracks.Select(track => new RemuxExportTrackChoice
        {
            Index = track.Index, Description = track.Description,
            CanChange = track.Index != settings.ReferenceVideoIndex,
            IsTextSubtitle = track.Type == AVMediaType.AVMEDIA_TYPE_SUBTITLE && track.Codec != "hdmv_pgs_subtitle",
            IsSelected = settings.StreamIndices.Contains(track.Index)
        }), video.ClipExtension == "mkv");
        await _dialogService.ShowDialogAsync(this, dialog);
        if (dialog.DialogResult != true || !ReferenceEquals(video, _videoInstance)) return;
        var updated = dialog.Apply(settings);
        if (updated.Container != settings.Container || updated.FastSubtitleSeek != settings.FastSubtitleSeek ||
            !updated.StreamIndices.SequenceEqual(settings.StreamIndices))
            InvalidateClipExports();
        _remuxExportSettings = updated;
        OnPropertyChanged(nameof(HasUnsavedProjectChanges));
    }

    private IReadOnlyList<RemuxExportSegment> CreateMergedRemuxSegments(IEnumerable<PickedClip> clips)
    {
        var ranges = clips.OrderBy(clip => clip.StartTime).ToArray();
        var merged = new List<RemuxExportSegment>();
        foreach (var clip in ranges)
        {
            double? end = clip.EndPosition < 0 ? null : clip.EndTime;
            if (merged.Count > 0 && (merged[^1].EndSeconds is null || clip.StartTime <= merged[^1].EndSeconds))
            {
                var previous = merged[^1];
                merged[^1] = previous with { EndSeconds = previous.EndSeconds is null || end is null
                    ? null : Math.Max(previous.EndSeconds.Value, end.Value) };
            }
            else merged.Add(new RemuxExportSegment(clip.InFileInfo.FullName, clip.StartTime, end,
                CurrentRemuxSettings!.Snapshot()));
        }
        return merged;
    }

    public bool HasRemuxQueue => ExportQueue.Any(item => item.RemuxSettings is not null);

    private static string FormatRemuxExportError(RemuxExportException exception)
    {
        var strings = LocalizationManager.Instance;
        var message = exception.Code switch
        {
            RemuxExportError.SameFile => strings.String_MergeClips_OutputMatchesSource,
            RemuxExportError.UnsupportedContainer => strings.String_RemuxExport_Error_Container,
            RemuxExportError.UnsupportedTrack => strings.String_RemuxExport_Error_Track,
            RemuxExportError.IncompatibleStreams => strings.String_RemuxExport_Error_Compatibility,
            RemuxExportError.InvalidBoundary => strings.String_RemuxExport_Error_Boundary,
            RemuxExportError.UnsafeGop => strings.String_RemuxExport_Error_Gop,
            RemuxExportError.SubtitleTiming => strings.String_RemuxExport_Error_SubtitleTiming,
            RemuxExportError.SourceChanged => strings.String_MergeClips_SourceChanged,
            _ => strings.String_RemuxExport_Error_Timestamp
        };
        return string.IsNullOrWhiteSpace(exception.Message) ? message : message + "\n\n" + exception.Message;
    }
}
