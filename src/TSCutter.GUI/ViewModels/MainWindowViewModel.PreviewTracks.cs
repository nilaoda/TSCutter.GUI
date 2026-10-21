using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Classic.CommonControls.Dialogs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using TSCutter.GUI.Models;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.ViewModels;

public partial class MainWindowViewModel
{
    private bool _updatingPreviewSelection;
    private PreviewStreamSelection? _activePreviewSelection;
    private CancellationTokenSource? _previewSelectionCancellation;

    [ObservableProperty]
    private IReadOnlyList<PreviewProgramOption> _previewPrograms = [];

    [ObservableProperty]
    private IReadOnlyList<PreviewVideoTrackOption> _previewVideoTracks = [];

    [ObservableProperty]
    private PreviewProgramOption? _selectedPreviewProgram;

    [ObservableProperty]
    private PreviewVideoTrackOption? _selectedPreviewVideoTrack;

    [ObservableProperty]
    private bool _exportCurrentProgramOnly;

    public bool ShowPreviewTrackSelector => PreviewPrograms.Count > 1 || PreviewVideoTracks.Count > 1 ||
                                            _videoInstance?.ProgramCount > 1 || HasPreviewSubtitles;
    public bool HasPreviewPrograms => PreviewPrograms.Any(program => program.Id is not null);
    public bool CanSelectPreviewProgram => CanSwitchPreviewTrack && PreviewPrograms.Count > 1;
    public bool CanSelectPreviewVideo => CanSwitchPreviewTrack && PreviewVideoTracks.Count > 1;
    public bool CanSwitchPreviewTrack => HasCurrentFile && !_isLoadingVideo && !IsDecoding && !_isResolvingClipEnd;
    public bool CanChooseProgramExport => CanEditVideo && ShowProgramExportChoice;
    public bool ShowProgramExportChoice => _videoInstance is { CanBinaryClip: true, ProgramCount: > 1 } &&
                                          _activePreviewSelection?.ProgramId is not null;
    public bool IsProgramExportFiltered => ExportCurrentProgramOnly && ShowProgramExportChoice;
    public bool ShowRawClipSizeEstimate => !IsProgramExportFiltered;
    public string FilteredClipSizeStr => LocalizationManager.Instance.String_SizePrefix
        + LocalizationManager.Instance.String_PreviewTrack_SizeAfterExport;
    private bool CanLoadVideo => !_isLoadingVideo;
    private bool CanUsePreview => IsVideoInitialized && !_isLoadingVideo;
    private int? SelectedOutputProgramId => ExportCurrentProgramOnly && CanChooseProgramExport
        ? _activePreviewSelection?.ProgramId : null;

    partial void OnExportCurrentProgramOnlyChanged(bool value)
    {
        InvalidateClipExports();
        _configService.CurrentConfig.ExportCurrentProgramOnly = value;
        _configService.Save();
        NotifyPreviewTrackChoicesChanged();
    }

    partial void OnSelectedPreviewProgramChanged(PreviewProgramOption? value)
    {
        if (_updatingPreviewSelection)
            return;
        if (value is null)
        {
            SetPreviewTrackChoices(PreviewPrograms, _activePreviewSelection);
            return;
        }
        _updatingPreviewSelection = true;
        PreviewVideoTracks = value.VideoTracks;
        SelectedPreviewVideoTrack = PreviewVideoTracks.FirstOrDefault();
        _updatingPreviewSelection = false;
        NotifyPreviewTrackChoicesChanged();
        _ = SwitchPreviewTrackAsync(SelectedPreviewVideoTrack);
    }

    partial void OnSelectedPreviewVideoTrackChanged(PreviewVideoTrackOption? value)
    {
        if (_updatingPreviewSelection)
            return;
        if (value is null)
            SetPreviewTrackChoices(PreviewPrograms, _activePreviewSelection);
        else
            _ = SwitchPreviewTrackAsync(value);
    }

    private void SetPreviewTrackChoices(IReadOnlyList<PreviewProgramOption> programs,
        PreviewStreamSelection? selection)
    {
        _updatingPreviewSelection = true;
        try
        {
            _activePreviewSelection = selection;
            PreviewPrograms = programs;
            SelectedPreviewProgram = programs.FirstOrDefault(program =>
                program.Id == selection?.ProgramId &&
                program.VideoTracks.Any(track => track.Selection.StreamIndex == selection?.StreamIndex));
            PreviewVideoTracks = SelectedPreviewProgram?.VideoTracks ?? [];
            SelectedPreviewVideoTrack = PreviewVideoTracks.FirstOrDefault(track =>
                track.Selection.StreamIndex == selection?.StreamIndex);
        }
        finally
        {
            _updatingPreviewSelection = false;
        }
        NotifyPreviewTrackChoicesChanged();
    }

    private void NotifyPreviewTrackChoicesChanged()
    {
        OnPropertyChanged(nameof(ShowPreviewTrackSelector));
        OnPropertyChanged(nameof(HasPreviewPrograms));
        OnPropertyChanged(nameof(CanSwitchPreviewTrack));
        OnPropertyChanged(nameof(CanSelectPreviewProgram));
        OnPropertyChanged(nameof(CanSelectPreviewVideo));
        OnPropertyChanged(nameof(CanSelectPreviewSubtitle));
        OnPropertyChanged(nameof(CanChooseProgramExport));
        OnPropertyChanged(nameof(ShowProgramExportChoice));
        OnPropertyChanged(nameof(IsProgramExportFiltered));
        OnPropertyChanged(nameof(ShowRawClipSizeEstimate));
        OnPropertyChanged(nameof(FilteredClipSizeStr));
        OnPropertyChanged(nameof(SelectedClipEstimatedSizeStr));
        LoadVideoClickCommand.NotifyCanExecuteChanged();
        OpenProjectCommand.NotifyCanExecuteChanged();
    }

    private async Task SwitchPreviewTrackAsync(PreviewVideoTrackOption? choice)
    {
        if (choice is null || choice.Selection == _activePreviewSelection)
            return;
        if (!CanSwitchPreviewTrack)
        {
            SetPreviewTrackChoices(PreviewPrograms, _activePreviewSelection);
            return;
        }

        _isLoadingVideo = true;
        DecodingOpCount++;
        NotifyVideoCapabilityChanged();
        using var cancellation = new CancellationTokenSource();
        _previewSelectionCancellation = cancellation;
        VideoInstance? candidate = null;
        DecodeResult? frame = null;
        try
        {
            if (Clips.Count > 0 && !await ShowConfirmAsync(
                    LocalizationManager.Instance.String_PreviewTrack_ClearClipsConfirm))
                return;
            cancellation.Token.ThrowIfCancellationRequested();
            CancelClipEndScan();
            await CompleteScrubPreviewAsync();
            candidate = new VideoInstance(VideoPath,
                _configService.CurrentConfig.PreferHardwareDecoding, choice.Selection);
            await candidate.InitVideoAsync(cancellation.Token);
            var duration = candidate.GetVideoDurationInSeconds();
            if (CurrentTime > 0 && duration > 0)
                await candidate.SeekToTimeAsync(TimeSpan.FromSeconds(Math.Min(CurrentTime, duration)), cancellation.Token);
            frame = await candidate.DecodeNextFrameAsync(cancellationToken: cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();

            // 新轨道首帧成功后才释放旧预览、剪辑与历史；失败或取消保留原状态。
            var previous = _videoInstance;
            ClearVars();
            _videoInstance = candidate;
            _remuxExportSettings = null;
            candidate = null;
            previous?.Dispose();
            VideoInfoText = _videoInstance.GetVideoInfoText();
            DurationMax = duration;
            TimelineViewport.Reset(duration, _videoInstance.EstimatedKeyFrameIntervalSeconds);
            SetPreviewTrackChoices(_videoInstance.PreviewPrograms, _videoInstance.StreamSelection);
            SetSubtitleTrackChoices(_videoInstance.PreviewSubtitleTracks);
            ApplyDecodeResult(frame);
            frame = null;
            UpdateDecodeMode();
            WeakReferenceMessenger.Default.Send(new FitMessage());
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Failed to switch preview track: {exception}");
            await ShowMessageAsync(FormatPreviewFailure(exception),
                LocalizationManager.Instance.String_PreviewTrack_SwitchFailed, MessageBoxIcon.Error);
        }
        finally
        {
            if (frame is not null)
            {
                frame.BitmapLease?.Dispose();
                if (frame.BitmapLease is null)
                    frame.Bitmap?.Dispose();
                frame.GpuFrame?.Dispose();
            }
            candidate?.Dispose();
            if (ReferenceEquals(_previewSelectionCancellation, cancellation))
                _previewSelectionCancellation = null;
            _isLoadingVideo = false;
            DecodingOpCount = Math.Max(0, DecodingOpCount - 1);
            SetPreviewTrackChoices(PreviewPrograms, _activePreviewSelection);
            NotifyVideoCapabilityChanged();
        }
    }
}
