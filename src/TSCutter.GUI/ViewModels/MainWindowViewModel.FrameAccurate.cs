using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using Classic.CommonControls.Dialogs;
using CommunityToolkit.Mvvm.Input;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Utils.Application;

namespace TSCutter.GUI.ViewModels;

// 剪辑模式：关键帧剪辑（默认）与帧精确剪辑。模式影响选点和导出方式，
// 随工程保存；导出任务创建时固定为快照。
public partial class MainWindowViewModel
{
    private ClipCutMode? _cutMode;

    private ClipCutMode CutMode => _cutMode ??= _configService.CurrentConfig.ClipCutMode;

    private FrameAccurateSupport CurrentFrameAccurateSupport => _videoInstance is { Inited: true } video
        ? video.FrameAccurateSupport : FrameAccurateSupport.Supported;

    /// <summary>未打开视频时保留偏好；当前素材不支持时禁用帧精确选项。</summary>
    public bool CanSelectFrameAccurate => !_isLoadingVideo && CurrentFrameAccurateSupport == FrameAccurateSupport.Supported;

    /// <summary>当前素材实际使用帧精确剪辑。</summary>
    public bool UsesFrameAccurateCut => CutMode == ClipCutMode.FrameAccurate && IsVideoInitialized &&
        CurrentFrameAccurateSupport == FrameAccurateSupport.Supported;

    public bool IsFrameAccurateMode
    {
        get => CutMode == ClipCutMode.FrameAccurate && CurrentFrameAccurateSupport == FrameAccurateSupport.Supported;
        set
        {
            if (value != IsFrameAccurateMode)
                _ = ChangeCutModeAsync(value ? ClipCutMode.FrameAccurate : ClipCutMode.KeyFrame);
        }
    }

    public bool IsKeyFrameMode
    {
        get => !IsFrameAccurateMode;
        set => IsFrameAccurateMode = !value;
    }

    /// <summary>当前素材不支持智能剪辑时禁用开关（悬停仍显示原因）；加载过程中保持可用，避免闪烁。</summary>
    private bool CanToggleFrameAccurate => !HasFrameAccurateUnavailableText;

    /// <summary>已开启智能剪辑但当前素材不支持：开关旁显示警告图标，说明原因并提示改用关键帧剪辑。</summary>
    public bool ShowFrameAccurateWarning => CutMode == ClipCutMode.FrameAccurate && HasFrameAccurateUnavailableText;

    public string FrameAccurateWarningToolTip => ShowFrameAccurateWarning
        ? FrameAccurateUnavailableText + Environment.NewLine + LocalizationManager.Instance.String_CutMode_FallbackKeyFrame
        : "";

    /// <summary>
    /// 工具栏的帧精确开关：开启为帧精确剪辑，关闭为默认的关键帧剪辑。按钮被点击时已自行翻转，
    /// 无论切换成功还是被拦截，都通知界面按实际模式重新显示。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanToggleFrameAccurate))]
    private async Task ToggleFrameAccurateAsync()
    {
        var target = IsFrameAccurateMode ? ClipCutMode.KeyFrame : ClipCutMode.FrameAccurate;
        RestoreCutModeToggles();
        await ChangeCutModeAsync(target);
    }

    /// <summary>
    /// 帧精确开关的提示：开启时写明状态与智能剪辑的规则；关闭时说明当前的关键帧剪辑规则及开启后的效果，
    /// 不再附上开启状态的规则（否则与“已关闭”矛盾）。不可用时说明原因。
    /// </summary>
    public string FrameAccurateToolTip => HasFrameAccurateUnavailableText
        ? FrameAccurateUnavailableText
        : IsFrameAccurateMode
            ? LocalizationManager.Instance.String_CutMode_FrameAccurateOn + Environment.NewLine
              + LocalizationManager.Instance.String_CutMode_FrameAccurateTip
              + (_videoInstance is { Inited: true, UsesGradualRefresh: true }
                  ? Environment.NewLine + LocalizationManager.Instance.String_CutMode_GradualRefreshNote : "")
            : LocalizationManager.Instance.String_CutMode_FrameAccurateOff;

    // 开关在点击时已自行翻转；延后通知，让界面在点击处理结束后按实际模式恢复显示。
    private void RestoreCutModeToggles() => Dispatcher.UIThread.Post(() =>
    {
        OnPropertyChanged(nameof(IsKeyFrameMode));
        OnPropertyChanged(nameof(IsFrameAccurateMode));
    });

    public string FrameAccurateUnavailableText => CurrentFrameAccurateSupport == FrameAccurateSupport.Supported ? ""
        : string.Format(LocalizationManager.Instance.String_CutMode_Unavailable, FormatFrameAccurateSupport(CurrentFrameAccurateSupport));

    public bool HasFrameAccurateUnavailableText => CurrentFrameAccurateSupport != FrameAccurateSupport.Supported;

    /// <summary>
    /// 帧精确导出能否按源字节范围预估大小：单节目 TS 只在首尾重新编码，其余原样复制；
    /// 多节目 TS 只输出预览所在节目，MP4/MKV 等需要重新封装，都只能导出后确定。
    /// </summary>
    private bool CanEstimateFrameAccurateSize => _videoInstance is { Inited: true, CanBinaryClip: true, ProgramCount: <= 1 };

    private static string FormatFrameAccurateSupport(FrameAccurateSupport support)
    {
        var strings = LocalizationManager.Instance;
        return support switch
        {
            FrameAccurateSupport.Codec => strings.String_CutMode_Reason_Codec,
            FrameAccurateSupport.Interlaced => strings.String_CutMode_Reason_Interlaced,
            FrameAccurateSupport.PixelFormat => strings.String_CutMode_Reason_PixelFormat,
            FrameAccurateSupport.DolbyVision => strings.String_CutMode_Reason_DolbyVision,
            FrameAccurateSupport.DolbyVisionContainer => strings.String_CutMode_Reason_DolbyVisionContainer,
            FrameAccurateSupport.DynamicHdr => strings.String_CutMode_Reason_DynamicHdr,
            FrameAccurateSupport.Pulldown => strings.String_CutMode_Reason_Pulldown,
            FrameAccurateSupport.MultipleVideo => strings.String_CutMode_Reason_MultipleVideo,
            FrameAccurateSupport.Encoder => strings.String_CutMode_Reason_Encoder,
            FrameAccurateSupport.GradualRefresh => strings.String_CutMode_Reason_GradualRefresh,
            _ => strings.String_CutMode_Reason_Container
        };
    }

    private VideoInstance? _gradualRefreshWarnedVideo;

    /// <summary>
    /// 逐行刷新码流没有完整关键帧：关键帧剪辑的输出从恢复点开始，开头几秒在任何播放器中都可能花屏。
    /// 智能剪辑的开头由预滚解码后重新编码，不会花屏，不提示。每个打开的文件只提示一次；取消则不导出。
    /// </summary>
    private async Task<bool> ConfirmGradualRefreshExportAsync()
    {
        if (_videoInstance is not { UsesGradualRefresh: true } video || UsesFrameAccurateCut ||
            ReferenceEquals(_gradualRefreshWarnedVideo, video))
            return true;
        if (!await ShowConfirmAsync(LocalizationManager.Instance.String_Export_GradualRefreshWarning))
            return false;
        _gradualRefreshWarnedVideo = video;
        return true;
    }

    private async Task ChangeCutModeAsync(ClipCutMode mode)
    {
        if (mode == ClipCutMode.FrameAccurate && !CanSelectFrameAccurate)
        {
            NotifyCutModeChanged();
            RestoreCutModeToggles();
            // 当前素材不支持时，说明原因（加载过程中的点击直接忽略）。
            if (!_isLoadingVideo && HasFrameAccurateUnavailableText)
                await ShowMessageAsync(FrameAccurateUnavailableText, LocalizationManager.Instance.String_CutMode_FrameAccurate,
                    MessageBoxIcon.Information);
            return;
        }
        // 帧精确片段的起止点可能不是关键帧，也没有关键帧模式所需的字节边界，不能静默对齐。
        var blocked = Clips.Where(clip => clip.RequiresFrameAccurate).ToArray();
        if (mode == ClipCutMode.KeyFrame && blocked.Length > 0)
        {
            NotifyCutModeChanged();
            RestoreCutModeToggles();
            await ShowMessageAsync(string.Format(LocalizationManager.Instance.String_CutMode_SwitchBlocked,
                    string.Join(", ", blocked.Select(clip => $"#{clip.ClipID}"))),
                LocalizationManager.Instance.String_CutMode_KeyFrame, MessageBoxIcon.Warning);
            return;
        }
        _cutMode = mode;
        _configService.CurrentConfig.ClipCutMode = mode;
        _configService.Save();
        // 片段边界不变，已导出文件仍有效；递增版本，避免切换前开始的导出把结果写回新模式。
        _exportSettingsRevision++;
        NotifyCutModeChanged();
    }

    private void ApplyCutMode(ClipCutMode mode)
    {
        _cutMode = mode;
        NotifyCutModeChanged();
    }

    private bool? stripEveryFrame;

    private void NotifyCutModeChanged()
    {
        // 导航条在关键帧与逐帧之间切换；只在实际模式变化时重建，避免频繁的能力通知打断加载。
        if (stripEveryFrame != UsesFrameAccurateCut)
        {
            stripEveryFrame = UsesFrameAccurateCut;
            if (IsVideoInitialized && !IsPlaybackActive)
                RequestKeyFrameStrip(CurrentTime, immediate: true, currentPts: _displayedFramePts);
        }
        foreach (var clip in Clips)
            clip.UsesFrameAccurateExport = UsesFrameAccurateCut;
        OnPropertyChanged(nameof(IsFrameAccurateMode));
        OnPropertyChanged(nameof(IsKeyFrameMode));
        OnPropertyChanged(nameof(UsesFrameAccurateCut));
        OnPropertyChanged(nameof(CanSelectFrameAccurate));
        OnPropertyChanged(nameof(FrameAccurateUnavailableText));
        OnPropertyChanged(nameof(FrameAccurateToolTip));
        OnPropertyChanged(nameof(HasFrameAccurateUnavailableText));
        OnPropertyChanged(nameof(ShowFrameAccurateWarning));
        OnPropertyChanged(nameof(FrameAccurateWarningToolTip));
        ToggleFrameAccurateCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SelectedClipEstimatedSizeStr));
        OnPropertyChanged(nameof(HasUnsavedProjectChanges));
        PreviousFrameCommand.NotifyCanExecuteChanged();
        NextFrameCommand.NotifyCanExecuteChanged();
        MergeSelectedClipsCommand.NotifyCanExecuteChanged();
    }

    private bool CanStepFrame => UsesFrameAccurateCut && CanEditVideo;

    [RelayCommand(CanExecute = nameof(CanStepFrame))]
    private Task PreviousFrameAsync() => StepFrameAsync(-1);

    [RelayCommand(CanExecute = nameof(CanStepFrame))]
    private Task NextFrameAsync() => StepFrameAsync(1);

    private async Task StepFrameAsync(int direction)
    {
        if (!CanStepFrame || IsDecoding || CurrentPts == long.MinValue)
            return;
        var anchor = CurrentPts;
        var frameAnchor = _displayedFrameAnchor;
        if (ShowKeyFrameStrip)
            KeyFrameStrip.PreviewStep(direction);
        await DecodeExactFrameAsync(video => video.DecodeAdjacentFrameAsync(anchor, direction, frameAnchor));
    }

    /// <summary>逐帧连续移动多帧；画面没有变化（到达片头片尾或解码失败）时停止并返回 false。</summary>
    private async Task<bool> StepFramesAsync(int count)
    {
        for (var index = 0; index < Math.Abs(count); index++)
        {
            var before = CurrentPts;
            await StepFrameAsync(Math.Sign(count));
            if (CurrentPts == before)
                return false;
        }
        return true;
    }

    /// <summary>精确解码相邻帧并显示；解码失败时提示，结束后导航条按实际画面更新。</summary>
    private async Task DecodeExactFrameAsync(Func<VideoInstance, Task<DecodeResult?>> decode)
    {
        var video = _videoInstance!;
        try
        {
            await RunDecodeOperationAsync(async () =>
            {
                var result = await decode(video);
                if (result is null)
                    return;
                if (!ReferenceEquals(video, _videoInstance))
                {
                    DisposePreviewResult(result);
                    return;
                }
                // 逐帧画面不是播放帧，标记时不再吸附到关键帧。
                lastFrameFromPlayback = false;
                ApplyDecodeResult(result);
            });
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception);
            if (ReferenceEquals(video, _videoInstance))
                await ShowMessageAsync(exception is MediaReadTimeoutException
                        ? LocalizationManager.Instance.String_MediaReadTimeout
                        : LocalizationManager.Instance.String_Navigate_FrameStepFailed,
                    LocalizationManager.Instance.String_FailedToDecode, MessageBoxIcon.Error);
        }
        finally
        {
            UpdateDecodeMode();
            RequestKeyFrameStrip(CurrentTime, currentPts: _displayedFramePts);
        }
    }

    /// <summary>为片段创建帧精确导出快照；终点未标记时保留到文件末尾。</summary>
    private (FrameAccurateExportSettings Settings, long StartPts, long? EndPts) CreateFrameAccurateTask(PickedClip clip) =>
        (CreateFrameAccurateExportSettings(_videoInstance!), FrameAccurateStartPts(clip),
            clip.EndPosition < 0 ? null : clip.EndPts);

    /// <summary>
    /// 帧精确导出按 PTS 精确定位起点。从文件开头开始的片段在旧版本中记为 PTS 0，而源时间戳常常不从 0 开始
    /// （例如 MP4 首帧 PTS 为 B 帧延迟、TS 为大的时间戳）：早于时间轴原点的起点按原点的画面处理。
    /// </summary>
    private long FrameAccurateStartPts(PickedClip clip) => Math.Max(clip.StartPts, _videoInstance!.FirstFramePts);

    private IReadOnlyList<FrameAccurateExportSegment> CreateFrameAccurateSegments(IEnumerable<PickedClip> clips)
    {
        var settings = CreateFrameAccurateExportSettings(_videoInstance!);
        var segments = new List<FrameAccurateExportSegment>();
        foreach (var clip in clips.OrderBy(FrameAccurateStartPts))
        {
            var start = FrameAccurateStartPts(clip);
            long? end = clip.EndPosition < 0 ? null : clip.EndPts;
            // DVD 按画面签名定位：起点被钳到时间轴原点时不再是标记的画面，改按原点处理。
            var startSignature = start == clip.StartPts ? clip.StartSignature : null;
            var endSignature = end is null ? null : clip.EndSignature;
            // 重叠或相接的片段合并为一段，避免同一画面重复输出。
            if (segments.Count > 0 && (segments[^1].EndPts is null || start <= segments[^1].EndPts))
            {
                var previous = segments[^1];
                segments[^1] = previous.EndPts is null || end is null
                    ? previous with { EndPts = null, EndSignature = null }
                    : end.Value > previous.EndPts.Value ? previous with { EndPts = end, EndSignature = endSignature } : previous;
            }
            else segments.Add(new FrameAccurateExportSegment(clip.InFileInfo.FullName, start, end, settings.Snapshot(),
                StartSignature: startSignature, EndSignature: endSignature));
        }
        return segments;
    }

    private static string FormatFrameAccurateError(FrameAccurateExportException exception)
    {
        var strings = LocalizationManager.Instance;
        var message = exception.Code switch
        {
            FrameAccurateExportError.SameFile => strings.String_MergeClips_OutputMatchesSource,
            FrameAccurateExportError.Unsupported => string.Format(strings.String_FrameAccurate_Error_Unsupported,
                FormatFrameAccurateSupport(exception.Support)),
            FrameAccurateExportError.UnsupportedTrack => strings.String_RemuxExport_Error_Track,
            FrameAccurateExportError.FrameNotFound => strings.String_FrameAccurate_Error_FrameNotFound,
            FrameAccurateExportError.NoRandomAccessPoint => strings.String_FrameAccurate_Error_NoRandomAccessPoint,
            FrameAccurateExportError.ReencodeTooLong => string.Format(strings.String_FrameAccurate_Error_TooLong,
                FrameAccurateExportService.MaxReencodeSeconds),
            FrameAccurateExportError.IncompatibleStream => strings.String_FrameAccurate_Error_Incompatible,
            FrameAccurateExportError.TimestampNormalizationRequired => strings.String_FrameAccurate_Timestamps_Warning,
            FrameAccurateExportError.NonMonotonicTimestamp => strings.String_RemuxExport_Error_Timestamp,
            FrameAccurateExportError.SourceChanged => strings.String_MergeClips_SourceChanged,
            FrameAccurateExportError.EncodeFailed => strings.String_FrameAccurate_Error_Encode,
            _ => strings.String_FrameAccurate_Error_Invalid
        };
        return string.IsNullOrWhiteSpace(exception.Detail) ? message : message + "\n\n" + exception.Detail;
    }
}
