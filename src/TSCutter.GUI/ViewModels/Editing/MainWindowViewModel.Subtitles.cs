using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Classic.CommonControls.Dialogs;
using CommunityToolkit.Mvvm.ComponentModel;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Services.Preview;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.Utils.Media;

namespace TSCutter.GUI.ViewModels.Editing;

public partial class MainWindowViewModel
{
    private SubtitlePreviewService? _subtitlePreview;
    private CancellationTokenSource? _subtitleLoadCancellation, _subtitleRenderCancellation;
    private bool _updatingSubtitleSelection;
    private SubtitleOverlay? _subtitleOverlay;

    [ObservableProperty]
    private IReadOnlyList<PreviewSubtitleTrackOption> _previewSubtitleTracks = [];
    [ObservableProperty]
    private PreviewSubtitleTrackOption? _selectedPreviewSubtitleTrack;
    [ObservableProperty]
    private bool _isSubtitleLoading;

    public SubtitleOverlay? SubtitleOverlay => _subtitleOverlay;
    public bool HasPreviewSubtitles => PreviewSubtitleTracks.Count > 1;
    public bool CanSelectPreviewSubtitle => IsVideoInitialized && !_isLoadingVideo;

    private void SetSubtitleTrackChoices(IReadOnlyList<PreviewSubtitleTrackOption> tracks)
    {
        StopSubtitlePreview();
        _updatingSubtitleSelection = true;
        try
        {
            PreviewSubtitleTracks = tracks.Count == 0 ? [] : new[] { new PreviewSubtitleTrackOption(null, "") }.Concat(tracks).ToArray();
            SelectedPreviewSubtitleTrack = PreviewSubtitleTracks.FirstOrDefault();
        }
        finally { _updatingSubtitleSelection = false; }
        OnPropertyChanged(nameof(HasPreviewSubtitles));
        OnPropertyChanged(nameof(ShowPreviewTrackSelector));
        OnPropertyChanged(nameof(CanSelectPreviewSubtitle));
    }

    partial void OnSelectedPreviewSubtitleTrackChanged(PreviewSubtitleTrackOption? value)
    {
        if (!_updatingSubtitleSelection) _ = SwitchSubtitleTrackAsync(value);
    }

    private async Task SwitchSubtitleTrackAsync(PreviewSubtitleTrackOption? choice)
    {
        StopSubtitlePreview();
        if (choice?.Selection is not { } selection || _videoInstance is not { Inited: true } video) return;
        using var cancellation = new CancellationTokenSource();
        _subtitleLoadCancellation = cancellation;
        IsSubtitleLoading = true;
        SubtitlePreviewService? candidate = null;
        try
        {
            var path = VideoPath;
            var origin = video.TimelineOriginSeconds;
            var duration = DurationMax;
            var codedSize = video.CodedVideoSize;
            var syncOffset = video.TsSyncOffset;
            candidate = await Task.Run(() => SubtitlePreviewService.Load(path, selection, codedSize, origin,
                duration, syncOffset, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(video, _videoInstance)) return;
            _subtitlePreview = candidate;
            candidate = null;
            await RefreshSubtitleOverlayAsync();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!cancellation.IsCancellationRequested && ReferenceEquals(video, _videoInstance))
            {
                SelectedPreviewSubtitleTrack = PreviewSubtitleTracks.FirstOrDefault();
                await ShowMessageAsync(FFmpegNativeBootstrapper.BuildLoadFailureMessage(exception),
                    LocalizationManager.Instance.String_SubtitlePreview_Failed, MessageBoxIcon.Error);
            }
        }
        finally
        {
            candidate?.Dispose();
            if (ReferenceEquals(_subtitleLoadCancellation, cancellation))
            {
                _subtitleLoadCancellation = null;
                IsSubtitleLoading = false;
            }
        }
    }

    private async Task RefreshSubtitleOverlayAsync()
    {
        _subtitleRenderCancellation?.Cancel();
        SetSubtitleOverlay(null);
        if (_subtitlePreview is not { } service) return;
        using var cancellation = new CancellationTokenSource();
        _subtitleRenderCancellation = cancellation;
        SubtitleOverlay? overlay = null;
        try
        {
            var time = _displayedFrameTime;
            var size = DecodedFrameSourceSize;
            overlay = await Task.Run(() => service.Render(time, size, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(service, _subtitlePreview)) return;
            SetSubtitleOverlay(overlay);
            overlay = null;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (!ReferenceEquals(service, _subtitlePreview)) { }
        catch (Exception exception)
        {
            if (!cancellation.IsCancellationRequested && ReferenceEquals(service, _subtitlePreview))
            {
                SelectedPreviewSubtitleTrack = PreviewSubtitleTracks.FirstOrDefault();
                await ShowMessageAsync(FFmpegNativeBootstrapper.BuildLoadFailureMessage(exception),
                    LocalizationManager.Instance.String_SubtitlePreview_Failed, MessageBoxIcon.Error);
            }
        }
        finally
        {
            overlay?.Dispose();
            if (ReferenceEquals(_subtitleRenderCancellation, cancellation)) _subtitleRenderCancellation = null;
        }
    }

    private void SetSubtitleOverlay(SubtitleOverlay? overlay)
    {
        var previous = _subtitleOverlay;
        _subtitleOverlay = overlay;
        OnPropertyChanged(nameof(SubtitleOverlay));
        // 旧图与视频位图一样延迟到本次界面更新后释放，避免绘制持有已释放的图像。
        if (previous is not null) Dispatcher.UIThread.Post(previous.Dispose, DispatcherPriority.Background);
    }

    private void StopSubtitlePreview()
    {
        _subtitleLoadCancellation?.Cancel();
        _subtitleRenderCancellation?.Cancel();
        _subtitleLoadCancellation = _subtitleRenderCancellation = null;
        IsSubtitleLoading = false;
        var previous = _subtitlePreview;
        _subtitlePreview = null;
        SetSubtitleOverlay(null);
        // 关闭或换轨不等待后台渲染；服务内部锁保证最后一次渲染结束后才释放原生资源。
        if (previous is not null) _ = Task.Run(previous.Dispose);
    }
}
