using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.FrameworkDialogs;
using TSCutter.GUI.Models.Discs;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Player.Diagnostics;
using TSCutter.GUI.Rendering;
using TSCutter.GUI.Utils.Application;

namespace TSCutter.GUI.ViewModels.Player;

public sealed class BenchmarkSizeChoice(PixelSize size, bool current = false) : ObservableObject
{
    public PixelSize Size { get; } = size;
    public bool Current { get; } = current;
    internal void Refresh() => OnPropertyChanged(nameof(DisplayText));
    public string DisplayText => Current
        ? $"{LocalizationManager.Instance.String_PlaybackBenchmark_CurrentPreview} ({Size.Width}×{Size.Height})"
        : $"{Size.Width}×{Size.Height}";
}

public sealed class BenchmarkDecoderChoice(bool hardware) : ObservableObject
{
    public bool Hardware { get; } = hardware;
    internal void Refresh() => OnPropertyChanged(nameof(DisplayText));
    public string DisplayText => Hardware ? LocalizationManager.Instance.String_PlaybackBenchmark_Auto
        : LocalizationManager.Instance.String_PlaybackBenchmark_Software;
}

public sealed partial class PlaybackBenchmarkRow(PlaybackBenchmarkStage stage) : ObservableObject
{
    public PlaybackBenchmarkStage Stage { get; } = stage;
    [ObservableProperty]
    private PlaybackBenchmarkResult? _result;
    public string StageText => Stage switch
    {
        PlaybackBenchmarkStage.Decode => LocalizationManager.Instance.String_PlaybackBenchmark_Decode,
        PlaybackBenchmarkStage.Conversion => LocalizationManager.Instance.String_PlaybackBenchmark_Conversion,
        _ => LocalizationManager.Instance.String_PlaybackBenchmark_Playback
            + PresentationSuffix
    };
    private string PresentationSuffix => Result is not { } value ? string.Empty : " · "
        + (value.HardwareSurfacePresentation ? LocalizationManager.Instance.String_PlaybackBenchmark_GpuDirectPresentation
            : value.GpuPresentation ? "GPU" : "CPU");
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FpsText))]
    private double? _liveFramesPerSecond;
    public string FpsText => (Result?.FramesPerSecond ?? LiveFramesPerSecond) is { } fps ? $"{fps:F1}" : "—";
    public string FactorText => Result is { SourceFrameRate: > 0 } value ? $"{value.RealtimeFactor:F2}×" : "—";
    public string RatingText => Result?.Rating switch
    {
        PlaybackBenchmarkRating.Good => LocalizationManager.Instance.String_PlaybackBenchmark_Good,
        PlaybackBenchmarkRating.Marginal => LocalizationManager.Instance.String_PlaybackBenchmark_Marginal,
        PlaybackBenchmarkRating.Insufficient => LocalizationManager.Instance.String_PlaybackBenchmark_Insufficient,
        _ => "—"
    };
    public bool IsGood => Result?.Rating == PlaybackBenchmarkRating.Good;
    public bool IsMarginal => Result?.Rating == PlaybackBenchmarkRating.Marginal;
    public bool IsInsufficient => Result?.Rating == PlaybackBenchmarkRating.Insufficient;
    public string DetailText => Result is not { } value ? string.Empty : Stage switch
    {
        PlaybackBenchmarkStage.Decode => string.Format(LocalizationManager.Instance.String_PlaybackBenchmark_ReadDetail,
            value.ReadFraction, value.HardwareDecoding ? "H/W" : "S/W", value.FrameCount),
        PlaybackBenchmarkStage.Conversion => string.Format(LocalizationManager.Instance.String_PlaybackBenchmark_ConvertDetail,
            value.OutputSize.Width, value.OutputSize.Height, value.ElapsedSeconds * 1000 / value.FrameCount),
        _ => (value.HardwareSurfacePresentation ? LocalizationManager.Instance.String_PlaybackBenchmark_GpuDirectPresentation
            : value.GpuPresentation ? LocalizationManager.Instance.String_PlaybackBenchmark_GpuPresentation
            : LocalizationManager.Instance.String_PlaybackBenchmark_CpuPresentation) + " · "
            + string.Format(LocalizationManager.Instance.String_PlaybackBenchmark_PlayDetail,
                value.LateDrops, value.BusyDrops, value.Recoveries)
    };
    partial void OnResultChanged(PlaybackBenchmarkResult? value) => Refresh();
    internal void Refresh() => OnPropertyChanged(string.Empty);
}

public partial class PlaybackBenchmarkWindowViewModel(IDialogService dialogs) : ViewModelBase, IModalDialogViewModel
{
    private static readonly SemaphoreSlim TestGate = new(1, 1);
    private PlaybackBenchmarkSource? source;
    private CancellationTokenSource? cancellation;
    private IBitmapFrameLease? previewLease;
    private bool opened, closed;
    private bool toneMappingPreference = true;
    private long generation;
    public bool? DialogResult { get; }
    public Func<Task>? BeforeTestAsync { get; set; }
    public Action? AfterTest { get; set; }
    public ObservableCollection<PlaybackBenchmarkRow> Rows { get; } = new(
        Enum.GetValues<PlaybackBenchmarkStage>().Select(stage => new PlaybackBenchmarkRow(stage)));
    public IReadOnlyList<BenchmarkDecoderChoice> DecoderChoices { get; } = [new(true), new(false)];
    public ObservableCollection<BenchmarkSizeChoice> SizeChoices { get; } = [];

    [ObservableProperty] private string _filePath = string.Empty;
    [ObservableProperty] private IReadOnlyList<PlaybackBenchmarkTrack> _tracks = [];
    [ObservableProperty] private PlaybackBenchmarkTrack? _selectedTrack;
    [ObservableProperty] private BenchmarkDecoderChoice _selectedDecoder = new(true);
    [ObservableProperty] private BenchmarkSizeChoice? _selectedSize;
    [ObservableProperty] private bool _toneMapping = true;
    [ObservableProperty] private double _startSeconds;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCopy))]
    private bool _hasError;
    [ObservableProperty] private string _diagnosisText = string.Empty;
    [ObservableProperty] private Bitmap? _previewBitmap;
    [ObservableProperty] private IGpuFrameLease? _previewGpuFrame;
    [ObservableProperty] private PixelSize _previewSourceSize;
    [ObservableProperty] private double _previewWidth = 16;
    [ObservableProperty] private double _previewHeight = 9;

    public bool CanEdit => !IsBusy;
    public bool HasHdrTrack => SelectedTrack?.DynamicRange is VideoDynamicRange.Hdr or VideoDynamicRange.Hlg or VideoDynamicRange.DolbyVision;
    public bool CanToneMap => CanEdit && HasHdrTrack;
    public bool CanStart => !IsBusy && source is not null && SelectedTrack is not null && SelectedSize is not null;
    public bool HasResults => Rows.Any(row => row.Result is not null);
    public bool CanCopy => HasResults || HasError;
    public string SourceInfo => SelectedTrack is { } track
        ? $"{track.Codec} · {track.Width}×{track.Height} · {track.FrameRate:0.###} FPS" : "—";
    public double MaximumStartSeconds => source is { DurationSeconds: > 0 } value ? value.DurationSeconds : 365 * 86400;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanToneMap));
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
        BrowseCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedTrackChanged(PlaybackBenchmarkTrack? value)
    {
        OnPropertyChanged(nameof(SourceInfo));
        OnPropertyChanged(nameof(HasHdrTrack));
        OnPropertyChanged(nameof(CanToneMap));
        ToneMapping = HasHdrTrack && toneMappingPreference;
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
        ClearResults();
    }
    partial void OnSelectedSizeChanged(BenchmarkSizeChoice? value) => ClearResults();
    partial void OnSelectedDecoderChanged(BenchmarkDecoderChoice value) => ClearResults();
    partial void OnToneMappingChanged(bool value)
    {
        if (HasHdrTrack)
            toneMappingPreference = value;
        ClearResults();
    }
    partial void OnStartSecondsChanged(double value) => ClearResults();

    public async Task InitializeAsync(string path, DiscInput? disc, PreviewStreamSelection? track,
        double startSeconds, PixelSize outputSize, bool hardware, bool mapping, double? timelineOrigin = null)
    {
        if (IsBusy || closed)
            return;
        FilePath = path;
        source = null;
        SelectedTrack = null;
        Tracks = [];
        ClearResults();
        if (SizeChoices.Count == 0)
        {
            SizeChoices.Add(new(outputSize.Width > 0 && outputSize.Height > 0 ? outputSize : new(1600, 900), true));
            foreach (var size in new[] { new PixelSize(1280, 720), new PixelSize(1920, 1080), new PixelSize(3840, 2160) })
                SizeChoices.Add(new(size));
        }
        SelectedSize ??= SizeChoices[0];
        SelectedDecoder = DecoderChoices.First(choice => choice.Hardware == hardware);
        toneMappingPreference = mapping;
        ToneMapping = false;
        using var operation = new CancellationTokenSource();
        cancellation = operation;
        var current = ++generation;
        IsBusy = true;
        HasError = false;
        StatusText = LocalizationManager.Instance.String_PlaybackBenchmark_Loading;
        try
        {
            var description = await PlaybackBenchmarkService.DescribeAsync(path, disc, operation.Token);
            if (closed || current != generation)
                return;
            if (track is not null && timelineOrigin is { } origin)
                description = description with
                {
                    Tracks = description.Tracks.Select(item => item.Selection.StreamId == track.StreamId &&
                        (item.Selection.StreamIndex == track.StreamIndex || track.ProgramId is not null)
                            ? item with { Selection = track, OriginSeconds = origin } : item).ToArray()
                };
            source = description;
            Tracks = description.Tracks;
            SelectedTrack = Tracks.FirstOrDefault(item => item.Selection == track) ?? Tracks[0];
            OnPropertyChanged(nameof(MaximumStartSeconds));
            StartSeconds = Math.Clamp(startSeconds, 0, MaximumStartSeconds);
            StatusText = LocalizationManager.Instance.String_PlaybackBenchmark_Ready;
        }
        catch (Exception exception) { SetFailure(exception, operation.Token); }
        finally
        {
            if (ReferenceEquals(cancellation, operation))
                cancellation = null;
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task BrowseAsync()
    {
        var paths = await dialogs.ShowOpenFilesDialogAsync(this, new OpenFileDialogSettings
        {
            Title = LocalizationManager.Instance.String_PlaybackBenchmark_Title,
            Filters = [new(LocalizationManager.Instance.String_VideoFiles, ["ts", "m2ts", "mp4", "mkv", "mov", "avi", "mpeg", "vob", "avs", "avs2", "avs3"]),
                new(LocalizationManager.Instance.String_AllFiles, "*")]
        });
        if (paths.FirstOrDefault() is not { } path || closed)
            return;
        await InitializeAsync(path.LocalPath, null, null, 0, SizeChoices[0].Size,
            SelectedDecoder.Hardware, ToneMapping);
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (source is null || SelectedTrack is null || SelectedSize is null || closed)
            return;
        if (!await TestGate.WaitAsync(0))
        {
            HasError = true;
            StatusText = LocalizationManager.Instance.String_PlaybackBenchmark_Busy;
            return;
        }
        using var operation = new CancellationTokenSource();
        cancellation = operation;
        var current = ++generation;
        var prepared = false;
        ClearResults();
        HasError = false;
        IsBusy = true;
        try
        {
            if (BeforeTestAsync is { } prepare)
            {
                prepared = true;
                await prepare();
            }
            operation.Token.ThrowIfCancellationRequested();
            var request = new PlaybackBenchmarkRequest(source, SelectedTrack, StartSeconds, SelectedSize.Size,
                SelectedDecoder.Hardware, ToneMapping);
            var updates = new Progress<PlaybackBenchmarkProgress>(update =>
            {
                if (closed || current != generation || !IsBusy)
                    return;
                Progress = ((int)update.Stage + update.Fraction) / 3 * 100;
                if (update.FramesPerSecond is { } fps)
                    Rows[(int)update.Stage].LiveFramesPerSecond = fps;
                StatusText = string.Format(LocalizationManager.Instance.String_PlaybackBenchmark_Running,
                    Rows[(int)update.Stage].StageText);
                if (update.Completed is { } result)
                {
                    Rows[(int)result.Stage].Result = result;
                    OnPropertyChanged(nameof(HasResults));
                    OnPropertyChanged(nameof(CanCopy));
                }
            });
            var results = await PlaybackBenchmarkService.RunAsync(request, updates, ShowPreview, operation.Token);
            if (closed || current != generation)
                return;
            foreach (var result in results)
                Rows[(int)result.Stage].Result = result;
            Progress = 100;
            DiagnosisText = DescribeConclusion(results);
            StatusText = LocalizationManager.Instance.String_PlaybackBenchmark_Completed;
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(CanCopy));
        }
        catch (Exception exception) { SetFailure(exception, operation.Token); }
        finally
        {
            if (ReferenceEquals(cancellation, operation))
                cancellation = null;
            // 清理完成后丢弃同一轮迟到的进度消息。
            ++generation;
            try
            {
                if (prepared)
                    AfterTest?.Invoke();
            }
            finally
            {
                TestGate.Release();
                IsBusy = false;
            }
        }
    }

    internal static string DescribeConclusion(IReadOnlyList<PlaybackBenchmarkResult> results)
    {
        var strings = LocalizationManager.Instance;
        if (results.Any(result => result.SourceFrameRate <= 0))
            return strings.String_PlaybackBenchmark_Unknown;
        var decode = results.First(result => result.Stage == PlaybackBenchmarkStage.Decode);
        var conversion = results.First(result => result.Stage == PlaybackBenchmarkStage.Conversion);
        var playback = results.First(result => result.Stage == PlaybackBenchmarkStage.Playback);
        if (playback.GpuPresentation)
        {
            // GPU 播放不经过 CPU 转换阶段，不能把该阶段的低分误报为当前瓶颈。
            if (playback.Rating != PlaybackBenchmarkRating.Good)
                return decode.Rating == PlaybackBenchmarkRating.Insufficient
                    ? strings.String_PlaybackBenchmark_DecodeLow : strings.String_PlaybackBenchmark_PipelineLow;
            return decode.Rating == PlaybackBenchmarkRating.Marginal
                ? strings.String_PlaybackBenchmark_Margin : strings.String_PlaybackBenchmark_Smooth;
        }
        if (decode.Rating == PlaybackBenchmarkRating.Insufficient && conversion.Rating == PlaybackBenchmarkRating.Insufficient)
            return strings.String_PlaybackBenchmark_BothLow;
        if (decode.Rating == PlaybackBenchmarkRating.Insufficient)
            return strings.String_PlaybackBenchmark_DecodeLow;
        if (conversion.Rating == PlaybackBenchmarkRating.Insufficient)
            return strings.String_PlaybackBenchmark_ConvertLow;
        if (results.Any(result => result.Stage == PlaybackBenchmarkStage.Playback && result.Rating != PlaybackBenchmarkRating.Good))
            return strings.String_PlaybackBenchmark_PipelineLow;
        return results.Any(result => result.Rating == PlaybackBenchmarkRating.Marginal)
            ? strings.String_PlaybackBenchmark_Margin : strings.String_PlaybackBenchmark_Smooth;
    }

    private void SetFailure(Exception exception, CancellationToken token)
    {
        if (closed)
            return;
        if (token.IsCancellationRequested)
        {
            HasError = false;
            StatusText = LocalizationManager.Instance.String_PlaybackBenchmark_Cancelled;
            return;
        }
        HasError = true;
        StatusText = exception is PlaybackBenchmarkException error ? error.Failure switch
        {
            PlaybackBenchmarkFailure.TooFewFrames => LocalizationManager.Instance.String_PlaybackBenchmark_TooFewFrames,
            PlaybackBenchmarkFailure.InvalidTimestamps => LocalizationManager.Instance.String_PlaybackBenchmark_InvalidTimeline,
            _ => LocalizationManager.Instance.String_PlaybackBenchmark_Timeout
        } : exception is MediaReadTimeoutException ? LocalizationManager.Instance.String_MediaReadTimeout : exception.Message;
    }

    private void ShowPreview(DecodeResult frame)
    {
        if (closed)
        {
            frame.GpuFrame?.Dispose();
            frame.BitmapLease?.Dispose();
            if (frame.BitmapLease is null)
                frame.Bitmap?.Dispose();
            return;
        }
        var oldLease = previewLease;
        var oldBitmap = PreviewBitmap;
        PreviewGpuFrame?.Dispose();
        PreviewGpuFrame = frame.GpuFrame;
        PreviewSourceSize = frame.SourcePixelSize;
        previewLease = frame.BitmapLease;
        PreviewBitmap = frame.Bitmap;
        PreviewWidth = Math.Max(1, frame.SourcePixelSize.Width);
        PreviewHeight = Math.Max(1, frame.SourcePixelSize.Height);
        oldLease?.Dispose();
        if (oldLease is null)
            oldBitmap?.Dispose();
    }

    private void ClearResults()
    {
        foreach (var row in Rows)
        {
            row.Result = null;
            row.LiveFramesPerSecond = null;
        }
        DiagnosisText = string.Empty;
        if (!IsBusy && source is not null)
        {
            HasError = false;
            StatusText = LocalizationManager.Instance.String_PlaybackBenchmark_Ready;
        }
        ClearPreview();
        Progress = 0;
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(CanCopy));
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => cancellation?.Cancel();

    [RelayCommand]
    private async Task CopyAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || desktop.MainWindow?.Clipboard is not { } clipboard)
            return;
        var text = new StringBuilder().AppendLine(LocalizationManager.Instance.String_PlaybackBenchmark_Title)
            .AppendLine(SourceInfo).AppendLine($"{SelectedSize?.DisplayText} · {SelectedDecoder.DisplayText}");
        foreach (var row in Rows.Where(row => row.Result is not null))
            text.AppendLine($"{row.StageText}: {row.FpsText} FPS · {row.FactorText} · {row.RatingText}").AppendLine(row.DetailText);
        text.AppendLine(DiagnosisText);
        if (HasError)
            text.AppendLine(StatusText.Replace(FilePath, "<source>", StringComparison.Ordinal));
        await clipboard.SetTextAsync(text.ToString());
    }

    private void ClearPreview()
    {
        var bitmap = PreviewBitmap;
        PreviewBitmap = null;
        PreviewGpuFrame?.Dispose();
        PreviewGpuFrame = null;
        previewLease?.Dispose();
        if (previewLease is null)
            bitmap?.Dispose();
        previewLease = null;
    }

    public void OnOpened()
    {
        if (opened)
            return;
        opened = true;
        App.LocalizationService.LanguageChanged += OnLanguageChanged;
    }
    private void OnLanguageChanged()
    {
        foreach (var row in Rows)
            row.Refresh();
        foreach (var choice in DecoderChoices)
            choice.Refresh();
        foreach (var choice in SizeChoices)
            choice.Refresh();
        if (Rows.All(row => row.Result is not null))
            DiagnosisText = DescribeConclusion(Rows.Select(row => row.Result!).ToArray());
    }
    public void OnClosed()
    {
        closed = true;
        ++generation;
        cancellation?.Cancel();
        if (opened)
            App.LocalizationService.LanguageChanged -= OnLanguageChanged;
        ClearPreview();
        BeforeTestAsync = null;
        AfterTest = null;
    }
}
