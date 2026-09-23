using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.FileSystem;
using HanumanInstitute.MvvmDialogs.FrameworkDialogs;
using TSCutter.GUI.Models;
using TSCutter.GUI.Rendering;
using TSCutter.GUI.Services;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.ViewModels;

/// <summary>
/// 缩略图总览窗口。参数变更后重新取帧并合成一张预览图，
/// 用户确认后可按 PNG / JPG 保存。
/// </summary>
public partial class ThumbnailSheetWindowViewModel : ViewModelBase, IModalDialogViewModel
{
    /// <summary>格数上限，避免一次请求过多帧点把解码器压满。</summary>
    private const int MaximumCellCount = 240;
    internal const double DefaultOutputWidth = 4000;

    /// <summary>参数变更后的去抖延迟，避免拖动滑块时反复重算。</summary>
    private static readonly TimeSpan RebuildDebounce = TimeSpan.FromMilliseconds(320);

    private readonly ThumbnailSheetService service = new();
    private readonly SemaphoreSlim rebuildLock = new(1, 1);
    private readonly TaskCompletionSource initializationCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CancellationTokenSource? buildCancellation;
    private CancellationTokenSource? debounceCancellation;
    private CancellationTokenSource? initializationCancellation;
    private ThumbnailSheetInfo info = ThumbnailSheetInfo.Empty;
    private string filePath = string.Empty;
    private Bitmap? previewImage;
    private double previewZoom = 1;
    private int requestedColumns = 4;
    private int requestedRows = 3;
    private double outputWidth = DefaultOutputWidth;
    private ThumbnailSheetSampling sampling = ThumbnailSheetSampling.Uniform;
    private SamplingOption selectedSampling = null!;
    private double intervalSeconds = 10;
    private bool showHeader = true;
    private bool showCaption = true;
    private bool showIndex = true;
    private bool isPngFormat = true;
    private double jpegQuality = 90;
    private bool isBusy;
    private double progressValue;
    private string statusText = string.Empty;
    private bool isInitialized;
    private bool isClosed;
    private bool previewDirty = true;

    public ThumbnailSheetWindowViewModel()
    {
        Cells = [];
        SamplingOptions =
        [
            new(ThumbnailSheetSampling.Uniform, LocalizationManager.Instance.String_ThumbnailSheet_Sampling_Uniform),
            new(ThumbnailSheetSampling.KeyFrame, LocalizationManager.Instance.String_ThumbnailSheet_Sampling_KeyFrame),
            new(ThumbnailSheetSampling.Interval, LocalizationManager.Instance.String_ThumbnailSheet_Sampling_Interval)
        ];
        selectedSampling = SamplingOptions[0];
        RefreshLocalizedText();
        App.LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    public ObservableCollection<SamplingOption> SamplingOptions { get; }

    /// <summary>下拉框用的取帧方式选项。</summary>
    public sealed record SamplingOption(ThumbnailSheetSampling Mode, string Label);

    public string WindowTitle => LocalizationManager.Instance.String_ThumbnailSheet_Title;

    public string FilePath
    {
        get => filePath;
        set
        {
            if (!SetProperty(ref filePath, value))
                return;
            OnPropertyChanged(nameof(FileName));
        }
    }

    public string FileName => string.IsNullOrEmpty(filePath)
        ? string.Empty
        : Path.GetFileName(filePath);

    public ObservableCollection<ThumbnailSheetCell> Cells { get; }

    internal static IReadOnlyList<double> SupportedOutputWidths { get; } =
        [1280, 1920, 2560, 3840, 4000, 5000, 6000, 7680, 10000, 12000];

    public IReadOnlyList<double> OutputWidthOptions => SupportedOutputWidths;

    public Task InitializationTask => initializationCompletion.Task;

    /// <summary>参数变更后需要重建；重建完成后可通过 <see cref="RebuildCompletion"/> 等待。</summary>
    public Task RebuildCompletion { get; private set; } = Task.CompletedTask;

    public Bitmap? PreviewImage
    {
        get => previewImage;
        private set
        {
            if (!SetProperty(ref previewImage, value))
                return;
            OnPropertyChanged(nameof(HasPreview));
            OnPropertyChanged(nameof(PreviewZoomText));
            SaveCommand.NotifyCanExecuteChanged();
            CopyToClipboardCommand.NotifyCanExecuteChanged();
        }
    }

    public bool HasPreview => previewImage is not null;

    /// <summary>预览缩放比例。1 表示一个图片像素对应一个物理屏幕像素。</summary>
    public double PreviewZoom
    {
        get => previewZoom;
        set
        {
            if (!SetProperty(ref previewZoom, value))
                return;
            OnPropertyChanged(nameof(PreviewZoomText));
        }
    }

    /// <summary>预览缩放提示，帮助用户判断画面清晰度是否符合预期。</summary>
    public string PreviewZoomText
    {
        get
        {
            if (previewImage is null)
                return string.Empty;
            var pixelInfo = string.Format(
                LocalizationManager.Instance.String_ThumbnailSheet_PreviewPixelInfo,
                previewImage.PixelSize.Width,
                previewImage.PixelSize.Height);
            return $"{previewZoom * 100:0.#}%  |  {pixelInfo}";
        }
    }

    public int Columns
    {
        get => requestedColumns;
        set
        {
            var clamped = Math.Clamp(value, 1, 12);
            if (!SetProperty(ref requestedColumns, clamped))
                return;

            OnPropertyChanged(nameof(MaximumRowsValue));
            if (requestedRows > MaximumRowsValue)
            {
                requestedRows = MaximumRowsValue;
                OnPropertyChanged(nameof(Rows));
                OnPropertyChanged(nameof(EffectiveRows));
            }
            OnPropertyChanged(nameof(CellSize));
            OnPropertyChanged(nameof(SamplingCount));
            OnPropertyChanged(nameof(LayoutSummary));
            OnPropertyChanged(nameof(EstimatedSizeText));
            QueueRebuild();
        }
    }

    public int Rows
    {
        get => requestedRows;
        set
        {
            var clamped = Math.Clamp(value, 1, MaximumRowsValue);
            if (!SetProperty(ref requestedRows, clamped))
                return;
            OnPropertyChanged(nameof(EffectiveRows));
            OnPropertyChanged(nameof(SamplingCount));
            OnPropertyChanged(nameof(LayoutSummary));
            OnPropertyChanged(nameof(EstimatedSizeText));
            QueueRebuild();
        }
    }

    public double OutputWidth
    {
        get => outputWidth;
        set
        {
            if (!IsSupportedOutputWidth(value) || !SetProperty(ref outputWidth, value))
                return;
            OnPropertyChanged(nameof(CellSize));
            OnPropertyChanged(nameof(LayoutSummary));
            OnPropertyChanged(nameof(EstimatedSizeText));
            QueueRebuild();
        }
    }

    internal static bool IsSupportedOutputWidth(double width)
    {
        foreach (var supportedWidth in SupportedOutputWidths)
        {
            if (supportedWidth == width)
                return true;
        }

        return false;
    }

    public int MaximumRowsValue => Math.Max(1, MaximumCellCount / Math.Max(1, requestedColumns));

    /// <summary>单元格像素尺寸。高度按视频真实宽高比换算，退化为 16:9。</summary>
    public PixelSize CellSize
    {
        get
        {
            var width = ThumbnailSheetComposer.CalculateCellWidthForOutputWidth(
                requestedColumns,
                (int)Math.Round(outputWidth));
            var height = Math.Max(9, (int)Math.Round(width * GetHeightToWidthRatio()));
            return new PixelSize(width, height);
        }
    }

    /// <summary>显示画面的高宽比。视频尺寸未知时退化为 16:9。</summary>
    private double GetHeightToWidthRatio()
    {
        var displayAspectRatio = GetDisplayAspectRatio();
        return displayAspectRatio > 0 ? 1d / displayAspectRatio : 9d / 16d;
    }

    private double GetDisplayAspectRatio()
    {
        if (info.VideoDisplayAspectRatio > 0 && double.IsFinite(info.VideoDisplayAspectRatio))
            return info.VideoDisplayAspectRatio;

        var videoWidth = info.VideoWidth;
        var videoHeight = info.VideoHeight;
        return videoWidth > 0 && videoHeight > 0
            ? videoWidth / (double)videoHeight
            : 16d / 9d;
    }

    /// <summary>实际使用的行数。</summary>
    public int EffectiveRows => Math.Max(1, requestedRows);

    /// <summary>需要抽取的格数：列数 × 行数，并受 <see cref="MaximumCellCount"/> 约束。</summary>
    public int SamplingCount => Math.Min(
        Math.Max(1, requestedColumns) * Math.Max(1, EffectiveRows),
        MaximumCellCount);

    public ThumbnailSheetSampling Sampling
    {
        get => sampling;
        set
        {
            if (!SetProperty(ref sampling, value))
                return;
            OnPropertyChanged(nameof(IsIntervalSampling));
            OnPropertyChanged(nameof(LayoutSummary));
            QueueRebuild();
        }
    }

    /// <summary>下拉框选中项。写回时同步 <see cref="Sampling"/>。</summary>
    public SamplingOption SelectedSampling
    {
        get => selectedSampling;
        set
        {
            if (value is null || !SetProperty(ref selectedSampling, value))
                return;
            Sampling = value.Mode;
        }
    }

    public bool IsIntervalSampling => sampling == ThumbnailSheetSampling.Interval;

    public double IntervalSeconds
    {
        get => intervalSeconds;
        set
        {
            var clamped = Math.Clamp(value, 1, 3600);
            if (!SetProperty(ref intervalSeconds, clamped))
                return;
            OnPropertyChanged(nameof(LayoutSummary));
            QueueRebuild();
        }
    }

    public bool ShowHeader
    {
        get => showHeader;
        set
        {
            if (!SetProperty(ref showHeader, value))
                return;
            OnPropertyChanged(nameof(LayoutSummary));
            OnPropertyChanged(nameof(EstimatedSizeText));
            QueueRebuild();
        }
    }

    public bool ShowCaption
    {
        get => showCaption;
        set
        {
            if (!SetProperty(ref showCaption, value))
                return;
            OnPropertyChanged(nameof(LayoutSummary));
            OnPropertyChanged(nameof(EstimatedSizeText));
            QueueRebuild();
        }
    }

    public bool ShowIndex
    {
        get => showIndex;
        set
        {
            if (!SetProperty(ref showIndex, value))
                return;
            QueueRebuild();
        }
    }

    public bool IsPngFormat
    {
        get => isPngFormat;
        set
        {
            if (!SetProperty(ref isPngFormat, value))
                return;
            OnPropertyChanged(nameof(IsJpegFormat));
            OnPropertyChanged(nameof(QualityText));
            OnPropertyChanged(nameof(FormatSummary));
            OnPropertyChanged(nameof(EstimatedSizeText));
        }
    }

    public bool IsJpegFormat
    {
        get => !isPngFormat;
        set => IsPngFormat = !value;
    }

    public double JpegQuality
    {
        get => jpegQuality;
        set
        {
            var clamped = Math.Clamp(value, 10, 100);
            if (!SetProperty(ref jpegQuality, clamped))
                return;
            OnPropertyChanged(nameof(QualityText));
            OnPropertyChanged(nameof(EstimatedSizeText));
        }
    }

    public string QualityText => ((int)Math.Round(jpegQuality)).ToString();

    public string FormatSummary => isPngFormat
        ? LocalizationManager.Instance.String_ThumbnailSheet_Format_Png
        : LocalizationManager.Instance.String_ThumbnailSheet_Format_Jpg;

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!SetProperty(ref isBusy, value))
                return;
            CancelCommand.NotifyCanExecuteChanged();
            SaveCommand.NotifyCanExecuteChanged();
            CopyToClipboardCommand.NotifyCanExecuteChanged();
        }
    }

    public double ProgressValue
    {
        get => progressValue;
        private set => SetProperty(ref progressValue, value);
    }

    public string StatusText
    {
        get => statusText;
        private set => SetProperty(ref statusText, value);
    }

    /// <summary>成图总尺寸与安全状态的摘要，参数一变就刷新（不需要等抽帧）。</summary>
    public string LayoutSummary
    {
        get
        {
            var layout = CurrentLayout;
            var text = string.Format(
                LocalizationManager.Instance.String_ThumbnailSheet_SizeSummary,
                layout.Width,
                layout.Height,
                layout.Columns,
                layout.Rows);
            return IsLayoutSafe ? text : text + "  " + LocalizationManager.Instance.String_ThumbnailSheet_SizeWarning;
        }
    }

    public string EstimatedSizeText
    {
        get
        {
            var layout = CurrentLayout;
            var pixels = (long)layout.Width * layout.Height;
            // 粗略估算：PNG 约 1.2 字节/像素，JPG 在高压缩比下约 0.15 字节/像素。
            var bytes = isPngFormat ? pixels * 1.2 : pixels * (1.2 * jpegQuality / 100d * 0.12);
            return CommonUtil.FormatFileSize(bytes);
        }
    }

    public bool IsLayoutSafe
    {
        get
        {
            var layout = CurrentLayout;
            return layout.Width <= ThumbnailSheetComposer.MaximumDimension
                && layout.Height <= ThumbnailSheetComposer.MaximumDimension
                && (long)layout.Width * layout.Height <= ThumbnailSheetComposer.MaximumPixelCount;
        }
    }

    public bool? DialogResult { get; private set; }

    public event Action? RequestClose;

    private ThumbnailSheetLayout CurrentLayout => ThumbnailSheetComposer.CalculateLayout(
        requestedColumns,
        EffectiveRows,
        CellSize,
        showHeader,
        showCaption,
        BuildHeaderLines().Count);

    public async Task InitializeAsync()
    {
        if (isInitialized)
        {
            await initializationCompletion.Task.ConfigureAwait(true);
            return;
        }
        if (isClosed)
        {
            initializationCompletion.TrySetResult();
            return;
        }
        isInitialized = true;
        var cts = new CancellationTokenSource();
        initializationCancellation = cts;

        try
        {
            if (string.IsNullOrEmpty(filePath))
                throw new InvalidOperationException("No input file specified.");

            StatusText = LocalizationManager.Instance.String_ThumbnailSheet_Status_LoadingInfo;
            info = await Task.Run(
                () => ThumbnailSheetInfoBuilder.Build(filePath),
                cts.Token).ConfigureAwait(true);
            cts.Token.ThrowIfCancellationRequested();

            if (info.VideoWidth > 0 && info.VideoHeight > 0)
            {
                // 画面比例已知后立即刷新格高与布局摘要。
                OnPropertyChanged(nameof(CellSize));
                OnPropertyChanged(nameof(LayoutSummary));
                OnPropertyChanged(nameof(EstimatedSizeText));
            }

            StatusText = LocalizationManager.Instance.String_ThumbnailSheet_Status_Opening;
            await service.OpenAsync(filePath, cts.Token).ConfigureAwait(true);
            cts.Token.ThrowIfCancellationRequested();

            await RebuildAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusText = LocalizationManager.Instance.String_ThumbnailSheet_Status_Cancelled;
        }
        catch (Exception ex)
        {
            StatusText = string.Format(
                LocalizationManager.Instance.String_ThumbnailSheet_Status_Failed,
                ex.Message);
        }
        finally
        {
            if (ReferenceEquals(initializationCancellation, cts))
                initializationCancellation = null;
            cts.Dispose();
            initializationCompletion.TrySetResult();
        }
    }

    private void QueueRebuild()
    {
        if (!isInitialized || isClosed)
            return;

        SetPreviewDirty(true);
        CancelSafely(buildCancellation);
        debounceCancellation?.Cancel();
        debounceCancellation?.Dispose();
        var cts = new CancellationTokenSource();
        debounceCancellation = cts;

        _ = DebounceAsync(cts.Token);
    }

    private async Task DebounceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(RebuildDebounce, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            await RebuildAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 参数又变了，交给后一次重建处理。
        }
    }

    /// <summary>
    /// 按当前参数重新抽帧并合成预览图。同一时刻只允许一次重建，
    /// 后续请求会取消前一次。
    /// </summary>
    public async Task RebuildAsync()
    {
        if (!isInitialized || isClosed)
            return;

        SetPreviewDirty(true);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RebuildCompletion = completion.Task;

        CancelSafely(buildCancellation);
        var cts = new CancellationTokenSource();
        buildCancellation = cts;

        var lockTaken = false;
        try
        {
            await rebuildLock.WaitAsync(cts.Token).ConfigureAwait(true);
            lockTaken = true;
            await BuildCoreAsync(cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusText = LocalizationManager.Instance.String_ThumbnailSheet_Status_Cancelled;
        }
        catch (Exception ex)
        {
            StatusText = string.Format(
                LocalizationManager.Instance.String_ThumbnailSheet_Status_Failed,
                ex.Message);
        }
        finally
        {
            if (lockTaken)
                rebuildLock.Release();
            if (ReferenceEquals(buildCancellation, cts))
                buildCancellation = null;
            cts.Dispose();
            completion.TrySetResult();
        }
    }

    private async Task BuildCoreAsync(CancellationToken cancellationToken)
    {
        var layout = CurrentLayout;
        if (!IsLayoutSafe)
        {
            // 不静默篡改用户参数，但给出可直接照做的建议值。
            var suggestedCellWidth = ThumbnailSheetComposer.CalculateMaximumCellWidth(
                requestedColumns,
                EffectiveRows,
                GetHeightToWidthRatio(),
                showHeader,
                showCaption,
                BuildHeaderLines().Count);
            var suggestedOutputWidth = suggestedCellWidth > 0
                ? ThumbnailSheetComposer.CalculateOutputWidth(requestedColumns, suggestedCellWidth)
                : 0;
            var recommendedOutputWidth = FindLargestSupportedOutputWidth(suggestedOutputWidth);

            StatusText = recommendedOutputWidth > 0
                ? string.Format(
                    LocalizationManager.Instance.String_ThumbnailSheet_Status_TooLargeHint,
                    recommendedOutputWidth)
                : LocalizationManager.Instance.String_ThumbnailSheet_Status_TooLarge;
            SetPreview(null);
            return;
        }

        IsBusy = true;
        ProgressValue = 0;
        try
        {
            await BuildCellsAsync(layout, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal static int FindLargestSupportedOutputWidth(int maximumWidth)
    {
        for (var i = SupportedOutputWidths.Count - 1; i >= 0; i--)
        {
            var width = (int)SupportedOutputWidths[i];
            if (width <= maximumWidth)
                return width;
        }

        return 0;
    }

    private async Task BuildCellsAsync(ThumbnailSheetLayout layout, CancellationToken cancellationToken)
    {
        var cells = new List<ThumbnailSheetCell>();
        var total = layout.Columns * layout.Rows;
        var points = service.BuildSamplePoints(sampling, total, intervalSeconds);

        // 刷新格集合：先把全部格子建出来（未就绪画占位），再逐格填充。
        DisposeCells();
        Cells.Clear();
        for (var i = 0; i < points.Count; i++)
        {
            var cell = new ThumbnailSheetCell(i, points[i])
            {
                TimestampText = CommonUtil.FormatSeconds(points[i].TotalSeconds, true)
            };
            Cells.Add(cell);
            cells.Add(cell);
        }

        Bitmap? composed = null;
        try
        {
            var cellSize = CellSize;
            for (var i = 0; i < cells.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var cell = cells[i];
                try
                {
                    cell.State = ThumbnailSheetCellState.Loading;
                    var bitmap = await service.DecodeCellAsync(
                            cell.Timestamp,
                            cellSize,
                            GetDisplayAspectRatio(),
                            cancellationToken)
                        .ConfigureAwait(true);
                    cell.Thumbnail = bitmap;
                    cell.State = bitmap is null
                        ? ThumbnailSheetCellState.Failed
                        : ThumbnailSheetCellState.Ready;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // 单格失败不影响整张图，保留占位底。
                    cell.State = ThumbnailSheetCellState.Failed;
                }

                ProgressValue = (i + 1) * 100d / cells.Count;
            }

            StatusText = string.Format(
                LocalizationManager.Instance.String_ThumbnailSheet_Status_Composing,
                layout.Width,
                layout.Height);

            var headerLines = BuildHeaderLines();
            composed = ThumbnailSheetComposer.Compose(
                layout,
                cells,
                headerLines,
                showCaption,
                showIndex);

            cancellationToken.ThrowIfCancellationRequested();
            SetPreview(composed);
            composed = null;
            SetPreviewDirty(false);

            StatusText = string.Format(
                LocalizationManager.Instance.String_ThumbnailSheet_Status_Ready,
                cells.Count,
                layout.Width,
                layout.Height);
        }
        finally
        {
            composed?.Dispose();
            // 合成后整图已持有所有像素，及时释放各格位图以控制超大图峰值内存。
            DisposeCells();
            Cells.Clear();
        }
    }

    /// <summary>
    /// 表头按文件名、基本信息、视频、音频和字幕轨道分行。
    /// 缺失字段直接省略，避免出现 "Unknown" 之类的噪声。
    /// </summary>
    private IReadOnlyList<string> BuildHeaderLines()
    {
        var localization = LocalizationManager.Instance;
        return BuildHeaderLines(
            info,
            FileName,
            localization.String_ThumbnailSheet_Header_FileName,
            localization.String_ThumbnailSheet_Header_FileInfo,
            localization.String_ThumbnailSheet_Header_VideoTracks,
            localization.String_ThumbnailSheet_Header_AudioTracks,
            localization.String_ThumbnailSheet_Header_SubtitleTracks);
    }

    internal static IReadOnlyList<string> BuildHeaderLines(
        ThumbnailSheetInfo info,
        string fallbackFileName,
        string fileNameLabel,
        string fileInfoLabel,
        string videoTracksLabel,
        string audioTracksLabel,
        string subtitleTracksLabel)
    {
        var lines = new List<string>(5);

        var name = string.IsNullOrEmpty(info.FileName) ? fallbackFileName : info.FileName;
        if (!string.IsNullOrEmpty(name))
            lines.Add(fileNameLabel + name);

        var summaryParts = new List<string>();
        if (info.OverallBitRate > 0)
            summaryParts.Add(CommonUtil.FormatBitrate(info.OverallBitRate));
        if (info.Duration > TimeSpan.Zero)
            summaryParts.Add(CommonUtil.FormatSeconds(info.Duration.TotalSeconds));
        if (info.FileSize > 0)
            summaryParts.Add(CommonUtil.FormatFileSize(info.FileSize));
        if (summaryParts.Count > 0)
            lines.Add(fileInfoLabel + string.Join("  |  ", summaryParts));

        var videoTracks = new List<string>();
        if (info.HasVideo)
        {
            var video = info.VideoCodec!;
            if (info.VideoWidth > 0 && info.VideoHeight > 0)
            {
                var scanSuffix = info.VideoScanMode switch
                {
                    VideoScanMode.Progressive => "p",
                    VideoScanMode.Interlaced => "i",
                    _ => string.Empty
                };
                video += $" {info.VideoWidth}x{info.VideoHeight}{scanSuffix}";
            }
            if (info.VideoFrameRate > 0)
                video += $" {info.VideoFrameRate:0.###}fps";
            videoTracks.Add(video);
        }
        videoTracks.AddRange(info.AdditionalVideoCodecs);
        if (videoTracks.Count > 0)
            lines.Add(videoTracksLabel + string.Join("  |  ", videoTracks));

        var audioTracks = new List<string>();
        if (info.AudioCodec is { Length: > 0 } audio)
        {
            if (info.AudioChannels > 0)
                audio += $" {info.AudioChannels}ch";
            if (info.AudioSampleRate > 0)
                audio += $" {info.AudioSampleRate / 1000.0:0.##}kHz";
            audioTracks.Add(audio);
        }
        audioTracks.AddRange(info.AdditionalAudioCodecs);
        if (audioTracks.Count > 0)
            lines.Add(audioTracksLabel + string.Join("  |  ", audioTracks));

        if (info.SubtitleCodecs.Count > 0)
            lines.Add(subtitleTracksLabel + string.Join("  |  ", info.SubtitleCodecs));

        return lines;
    }

    private void SetPreview(Bitmap? bitmap)
    {
        if (ReferenceEquals(previewImage, bitmap))
            return;

        var previous = previewImage;
        PreviewImage = bitmap;
        previous?.Dispose();
    }

    private void DisposeCells()
    {
        foreach (var cell in Cells)
            cell.Dispose();
    }

    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        CancelSafely(buildCancellation);
        StatusText = LocalizationManager.Instance.String_ThumbnailSheet_Status_Cancelled;
    }

    internal static bool IsPreviewReadyForExport(
        bool hasPreview,
        bool isDirty,
        bool isBusy) =>
        hasPreview && !isDirty && !isBusy;

    private bool CanSave() =>
        IsPreviewReadyForExport(PreviewImage is not null, previewDirty, IsBusy)
        && IsLayoutSafe;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var isPng = isPngFormat;
        var quality = (int)Math.Round(jpegQuality);
        var extension = isPng ? "png" : "jpg";
        var filterName = isPng
            ? LocalizationManager.Instance.String_PngImages
            : LocalizationManager.Instance.String_JpgImages;

        var baseName = string.IsNullOrEmpty(filePath)
            ? "thumbnail_sheet"
            : Path.GetFileNameWithoutExtension(filePath) + "_sheet";

        var settings = new SaveFileDialogSettings
        {
            Title = LocalizationManager.Instance.String_ThumbnailSheet_SaveDialogTitle,
            SuggestedStartLocation = string.IsNullOrEmpty(filePath)
                ? null
                : new DesktopDialogStorageFolder(Path.GetDirectoryName(filePath)!),
            SuggestedFileName = baseName + "." + extension,
            Filters = [new FileFilter(filterName, [extension])],
            DefaultExtension = extension
        };

        var result = await App.DialogService.ShowSaveFileDialogAsync(this, settings).ConfigureAwait(true);
        if (result is null)
            return;

        var targetPath = result.Path!.LocalPath;
        var lockTaken = false;
        try
        {
            await rebuildLock.WaitAsync().ConfigureAwait(true);
            lockTaken = true;
            var bitmap = PreviewImage;
            if (bitmap is null || previewDirty)
                return;

            await Task.Run(() =>
            {
                using var stream = File.Create(targetPath);
                if (isPng)
                    bitmap.Save(stream);
                else
                    ImageUtil.SaveAsJpeg(bitmap, stream, quality);
            }).ConfigureAwait(true);

            StatusText = string.Format(
                LocalizationManager.Instance.String_ThumbnailSheet_Status_Saved,
                Path.GetFileName(targetPath));
        }
        catch (Exception ex)
        {
            StatusText = string.Format(
                LocalizationManager.Instance.String_ThumbnailSheet_Status_Failed,
                ex.Message);
        }
        finally
        {
            if (lockTaken)
                rebuildLock.Release();
        }
    }

    private bool CanCopyToClipboard() =>
        IsPreviewReadyForExport(PreviewImage is not null, previewDirty, IsBusy);

    [RelayCommand(CanExecute = nameof(CanCopyToClipboard))]
    private async Task CopyToClipboardAsync()
    {
        var lockTaken = false;
        try
        {
            await rebuildLock.WaitAsync().ConfigureAwait(true);
            lockTaken = true;
            var bitmap = PreviewImage;
            if (bitmap is null || previewDirty)
                return;
            await ImageUtil.CopyBitmapToClipboardAsync(bitmap, isPngFormat);
        }
        finally
        {
            if (lockTaken)
                rebuildLock.Release();
        }
    }

    [RelayCommand]
    private void Close()
    {
        DialogResult = false;
        RequestClose?.Invoke();
    }

    /// <summary>
    /// 窗口即将关闭时同步取消在飞任务（不做资源释放，释放交给 <see cref="OnClosedAsync"/>）。
    /// </summary>
    public void CancelWork()
    {
        isClosed = true;
        CancelSafely(initializationCancellation);
        CancelSafely(debounceCancellation);
        CancelSafely(buildCancellation);
    }

    /// <summary>
    /// 窗口关闭时统一取消在飞的重建任务并释放解码器与位图。
    /// </summary>
    public async Task OnClosedAsync()
    {
        isClosed = true;
        App.LocalizationService.LanguageChanged -= OnLanguageChanged;

        if (!isInitialized)
            initializationCompletion.TrySetResult();
        CancelSafely(initializationCancellation);

        var debounce = debounceCancellation;
        debounceCancellation = null;
        CancelSafely(debounce);
        debounce?.Dispose();

        CancelSafely(buildCancellation);

        // 初始化可能仍在读取媒体信息或打开解码器。必须等它退出后再释放 service。
        await initializationCompletion.Task.ConfigureAwait(true);

        // 等在飞的 native 解码退出后，才能释放 VideoInstance 及其帧资源。
        await rebuildLock.WaitAsync().ConfigureAwait(true);
        rebuildLock.Release();

        await service.DisposeAsync().ConfigureAwait(true);

        DisposeCells();
        Cells.Clear();
        SetPreview(null);
    }

    private static void CancelSafely(CancellationTokenSource? source)
    {
        if (source is null)
            return;
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经释放，忽略。
        }
    }

    private void SetPreviewDirty(bool value)
    {
        if (previewDirty == value)
            return;
        previewDirty = value;
        SaveCommand.NotifyCanExecuteChanged();
        CopyToClipboardCommand.NotifyCanExecuteChanged();
    }

    private void OnLanguageChanged() => RefreshLocalizedText();

    private void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(FormatSummary));
        OnPropertyChanged(nameof(LayoutSummary));
        OnPropertyChanged(nameof(EstimatedSizeText));
        QueueRebuild();
    }
}
