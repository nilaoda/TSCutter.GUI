using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Services.TransportStream.Analysis;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.ViewModels.Preview;

namespace TSCutter.GUI.ViewModels;

// 节目带：文件或光盘标题有章节时按章节分段；没有章节的录制 TS 在后台读取 EPG，按节目分段。
// EPG 只在开头探测；开头没有时视为不存在，用户跳转后才在当前位置有限地再探测。可按段跳转或添加片段。
public partial class MainWindowViewModel
{
    // 跳转后停留这么久才在当前位置探测 EPG，连续跳转只探测最后停下的位置。
    private static readonly TimeSpan EpgProbeDelay = TimeSpan.FromSeconds(1);
    private CancellationTokenSource? _epgCancellation;
    private EpgSource? _epgSource;
    private IReadOnlyList<MediaChapter> _chapters = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTimelineSegments), nameof(TimelineSegmentSummaryText),
        nameof(SegmentJumpToStartText), nameof(SegmentJumpToEndText), nameof(SegmentAddClipText))]
    private IReadOnlyList<TimelineSegmentItem> _timelineSegments = [];

    /// <summary>当前文件的 EPG（录制中的节目段与读到的全部节目）。</summary>
    public EpgGuide EpgGuide { get; private set; } = EpgGuide.Empty;

    public bool HasTimelineSegments => TimelineSegments.Count > 0;

    private bool ShowsChapters => TimelineSegments is [{ Kind: TimelineSegmentKind.Chapter }, ..];

    public string TimelineSegmentSummaryText => !HasTimelineSegments ? string.Empty
        : string.Format(ShowsChapters ? LocalizationManager.Instance.String_Chapter_Summary
            : LocalizationManager.Instance.String_Epg_Summary, TimelineSegments.Count);

    public string SegmentJumpToStartText => ShowsChapters
        ? LocalizationManager.Instance.String_Chapter_JumpToStart : LocalizationManager.Instance.String_Epg_JumpToStart;

    public string SegmentJumpToEndText => ShowsChapters
        ? LocalizationManager.Instance.String_Chapter_JumpToEnd : LocalizationManager.Instance.String_Epg_JumpToEnd;

    public string SegmentAddClipText => ShowsChapters
        ? LocalizationManager.Instance.String_Chapter_AddClip : LocalizationManager.Instance.String_Epg_AddClip;

    private void StartTimelineSegmentLoad(VideoInstance video)
    {
        ResetTimelineSegments();
        if (!ShowTimelineSegments)
            return;
        var chapters = video.GetChapters(_discTitleInfo);
        if (chapters.Count > 0)
        {
            _chapters = chapters;
            TimelineSegments = TimelineSegmentItem.FromChapters(chapters, DurationMax);
            return;
        }
        if (!video.CanReadEpg)
            return;
        long length;
        try
        {
            length = new FileInfo(VideoPath).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }
        var (videoPid, serviceId) = video.GetEpgStreamInfo();
        var duration = video.GetVideoDurationInSeconds();
        _epgSource = new EpgSource(video, VideoPath, videoPid, serviceId, video.FirstFramePts, duration,
            TsEpgReader.ProbeWindowBytes(length, duration));
        StartEpgRead(_epgSource, () => 0, TimeSpan.Zero);
    }

    /// <summary>
    /// 开头读不到 EPG 的录制：用户跳转并停留后，在当前画面的位置探测一次；已探测过的范围不再读取，
    /// 读到后不再探测。播放期间不探测；开头的读取完成前也不探测，以免取消它。
    /// </summary>
    private void RequestEpgProbeAtCurrentPosition()
    {
        if (_epgSource is { ProbedOffsets.Count: > 0 } source && ReferenceEquals(source.Video, _videoInstance))
            StartEpgRead(source, () => PositionInFile, EpgProbeDelay);
    }

    private void StartEpgRead(EpgSource source, Func<long> anchorOffset, TimeSpan delay)
    {
        _epgCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _epgCancellation = cancellation;
        _ = ReadEpgAsync(source, anchorOffset, delay, cancellation);
    }

    private async Task ReadEpgAsync(EpgSource source, Func<long> anchorOffset, TimeSpan delay,
        CancellationTokenSource cancellation)
    {
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellation.Token);
            // 停留后再取位置：跳转期间的中间画面不探测。
            var offset = anchorOffset();
            if (offset < 0 || source.ProbedOffsets.Any(probed => offset >= probed && offset < probed + source.ProbeBytes))
                return;
            var guide = await TsEpgReader.ReadAsync(source.Path, source.VideoPid, source.ServiceId, source.FirstFramePts,
                source.Duration, offset, cancellation.Token);
            // 读取期间关闭或更换了视频时丢弃结果。
            if (cancellation.IsCancellationRequested || !ReferenceEquals(source, _epgSource))
                return;
            source.ProbedOffsets.Add(offset);
            if (!guide.HasPrograms)
                return;
            _epgSource = null;
            EpgGuide = guide;
            TimelineSegments = TimelineSegmentItem.FromEpg(guide);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // EPG 只是辅助信息，读取失败不影响剪辑。
            Console.WriteLine($"EPG unavailable: {exception.Message}");
        }
        finally
        {
            // 读取都在界面线程上开始与结束：先摘下字段再释放，之后的取消不会碰到已释放的对象。
            if (ReferenceEquals(_epgCancellation, cancellation))
                _epgCancellation = null;
            cancellation.Dispose();
        }
    }

    /// <summary>章节/节目带开关，与“视图”菜单和设置共用配置。</summary>
    public bool ShowTimelineSegments
    {
        get => _configService.CurrentConfig.ShowTimelineSegments;
        set
        {
            if (value == ShowTimelineSegments)
                return;
            _configService.CurrentConfig.ShowTimelineSegments = value;
            _configService.Save();
        }
    }

    /// <summary>
    /// 关闭时停止 EPG 读取并清空章节与节目段；开启时为当前文件重新读取。
    /// 视频仍在载入时，由载入完成后的节目带读取按设置处理。
    /// </summary>
    private void OnTimelineSegmentsPreferenceChanged()
    {
        OnPropertyChanged(nameof(ShowTimelineSegments));
        if (!ShowTimelineSegments)
            ResetTimelineSegments();
        else if (_videoInstance is { Inited: true } video && !_isLoadingVideo && !HasTimelineSegments && _epgSource is null)
            StartTimelineSegmentLoad(video);
    }

    private void ResetTimelineSegments()
    {
        _epgCancellation?.Cancel();
        _epgCancellation = null;
        _epgSource = null;
        EpgGuide = EpgGuide.Empty;
        _chapters = [];
        TimelineSegments = [];
    }

    /// <summary>读取 EPG 所需的录制信息，以及已探测过的位置（每次探测读取 ProbeBytes 字节）。</summary>
    private sealed record EpgSource(VideoInstance Video, string Path, int VideoPid, int? ServiceId, long FirstFramePts,
        double Duration, long ProbeBytes)
    {
        public List<long> ProbedOffsets { get; } = [];
    }

    // 标签与悬停说明按语言生成，切换语言后重建。
    private void RefreshTimelineSegmentTexts()
    {
        if (_chapters.Count > 0)
            TimelineSegments = TimelineSegmentItem.FromChapters(_chapters, DurationMax);
        else if (EpgGuide.HasPrograms)
            TimelineSegments = TimelineSegmentItem.FromEpg(EpgGuide);
    }

    /// <summary>媒体信息中的节目/章节列表：有章节时列出章节，否则列出读到的全部 EPG 节目。</summary>
    private IReadOnlyList<SegmentListItem> CreateSegmentListItems() => ShowsChapters
        ? SegmentListItem.FromChapters(TimelineSegments)
        : SegmentListItem.FromEpg(EpgGuide, TimelineSegments);

    [RelayCommand]
    private async Task JumpToSegmentStartAsync(TimelineSegmentItem? item)
    {
        if (item is null || !IsVideoInitialized)
            return;
        await PausePlaybackForNavigationAsync();
        await SeekToTimeAndDrawFrameAsync(TimeSpan.FromSeconds(item.JumpStartSeconds));
    }

    [RelayCommand]
    private async Task JumpToSegmentEndAsync(TimelineSegmentItem? item)
    {
        if (item is null || !IsVideoInitialized)
            return;
        await PausePlaybackForNavigationAsync();
        await SeekToTimeAndDrawFrameAsync(TimeSpan.FromSeconds(item.EndsAtEnd ? DurationMax : item.EndSeconds));
    }

    /// <summary>
    /// 按一段的范围添加片段：定位到段的开始添加片段，再定位到段的结束标记终点（到结尾的段保留到文件末尾），
    /// 起止与手动标记相同地按当前剪辑模式确定。片段以节目名或章节名命名；EPG 时间不精确，节目片段标记为需要核对起止。
    /// </summary>
    [RelayCommand]
    private async Task AddClipFromSegmentAsync(TimelineSegmentItem? item)
    {
        if (item is null || !CanMarkVideo)
            return;
        await PausePlaybackForNavigationAsync();
        await SeekToTimeAndDrawFrameAsync(TimeSpan.FromSeconds(item.JumpStartSeconds));
        if (!CanMarkVideo)
            return;
        RecordHistory();
        var clip = AddClipCore();
        clip.Name = item.Title;
        if (!item.EndsAtEnd)
        {
            await SeekToTimeAndDrawFrameAsync(TimeSpan.FromSeconds(item.EndSeconds));
            if (ReferenceEquals(SelectedClip, clip))
                await MarkClipEndCoreAsync(ensureSelectedClip: false);
        }
        if (item.Kind == TimelineSegmentKind.EpgProgram && Clips.Contains(clip))
            clip.NeedsBoundaryReview = true;
    }
}
