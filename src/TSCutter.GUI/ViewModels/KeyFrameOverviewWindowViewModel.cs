using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TSCutter.GUI.Models;
using TSCutter.GUI.Services;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.ViewModels;

public sealed partial class KeyFrameOverviewWindowViewModel : ViewModelBase,
    HanumanInstitute.MvvmDialogs.IModalDialogViewModel,
    HanumanInstitute.MvvmDialogs.ICloseable
{
    private const int AutomaticTileLimit = 500;
    private const int MaximumCacheEntries = 96;
    private const int MaximumQueuedRequests = 64;
    private readonly KeyFrameOverviewService service = new();
    private readonly object queueSync = new();
    private readonly Queue<int> queue = new();
    private readonly HashSet<int> queued = [];
    private readonly LinkedList<int> cacheOrder = [];
    private readonly SemaphoreSlim queueSignal = new(0);
    private readonly TaskCompletionSource initializationCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource closedCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? cancellation;
    private CancellationTokenSource? rangeCancellation;
    private Task? worker;
    private IReadOnlyList<KeyFrameIndexEntry> keyFrames = [];
    private int requestedStart = -1;
    private int requestedEnd = -1;
    private bool initialized;
    private bool closing;
    private bool selecting;

    public KeyFrameOverviewWindowViewModel()
    {
        SamplingOptions =
        [
            new(KeyFrameOverviewSampling.Automatic, LocalizationManager.Instance.String_KeyFrameOverview_Sampling_Automatic),
            new(KeyFrameOverviewSampling.EveryKeyFrame, LocalizationManager.Instance.String_KeyFrameOverview_Sampling_All),
            new(KeyFrameOverviewSampling.Every2, LocalizationManager.Instance.String_KeyFrameOverview_Sampling_Every2),
            new(KeyFrameOverviewSampling.Every5, LocalizationManager.Instance.String_KeyFrameOverview_Sampling_Every5),
            new(KeyFrameOverviewSampling.Every10, LocalizationManager.Instance.String_KeyFrameOverview_Sampling_Every10)
        ];
        SelectedSampling = SamplingOptions[0];
        ThumbnailWidth = 220;
        App.LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    public event EventHandler? RequestClose;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private string filePath = string.Empty;

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    private int thumbnailWidth;

    [ObservableProperty]
    private SamplingOption selectedSampling;

    public string WindowTitle => string.IsNullOrEmpty(FilePath)
        ? LocalizationManager.Instance.String_KeyFrameOverview_Title
        : $"{LocalizationManager.Instance.String_KeyFrameOverview_Title} - {Path.GetFileName(FilePath)}";

    public ObservableCollection<SamplingOption> SamplingOptions { get; }
    public IReadOnlyList<KeyFrameOverviewTile> Tiles { get; private set; } = [];
    public bool HasTiles => Tiles.Count > 0;
    public int ThumbnailHeight => Math.Max(90, (int)Math.Round(ThumbnailWidth * 9d / 16d));
    public int CachedTileCount => cacheOrder.Count;
    public TimeSpan? SelectedTime { get; private set; }
    public bool? DialogResult { get; private set; }
    public Task ClosedTask => closedCompletion.Task;

    public async Task InitializeAsync()
    {
        if (initialized || closing || string.IsNullOrWhiteSpace(FilePath))
        {
            initializationCompletion.TrySetResult();
            return;
        }

        initialized = true;
        cancellation = new CancellationTokenSource();
        StatusText = LocalizationManager.Instance.String_KeyFrameOverview_Status_Scanning;
        try
        {
            keyFrames = await service.OpenAsync(
                FilePath,
                cancellation.Token).ConfigureAwait(true);
            RebuildTiles();
            worker = ProcessQueueAsync(cancellation.Token);
            StatusText = string.Format(
                LocalizationManager.Instance.String_KeyFrameOverview_Status_Ready,
                keyFrames.Count,
                Tiles.Count);
        }
        catch (OperationCanceledException)
        {
            StatusText = LocalizationManager.Instance.String_KeyFrameOverview_Status_Cancelled;
        }
        catch (Exception exception)
        {
            StatusText = string.Format(LocalizationManager.Instance.String_KeyFrameOverview_Status_Failed, exception.Message);
        }
        finally
        {
            initializationCompletion.TrySetResult();
        }
    }

    public void RequestVisibleRange(int start, int end)
    {
        if (!initialized || closing || cancellation?.IsCancellationRequested == true)
            return;

        start = Math.Clamp(start, 0, Tiles.Count);
        end = Math.Clamp(end, start, Tiles.Count);
        var rangeChanged = start != requestedStart || end != requestedEnd;
        requestedStart = start;
        requestedEnd = end;
        lock (queueSync)
        {
            if (rangeChanged || rangeCancellation?.IsCancellationRequested == true)
            {
                var previousRangeCancellation = rangeCancellation;
                rangeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation!.Token);
                CancelSafely(previousRangeCancellation);
                previousRangeCancellation?.Dispose();
            }

            if (queue.Count > 0)
            {
                var retained = new Queue<int>(queue.Count);
                while (queue.TryDequeue(out var pendingIndex))
                {
                    if (pendingIndex >= start && pendingIndex < end)
                        retained.Enqueue(pendingIndex);
                    else
                    {
                        queued.Remove(pendingIndex);
                        if (pendingIndex < Tiles.Count)
                            Tiles[pendingIndex].SetLoading(false);
                    }
                }

                while (queueSignal.Wait(0))
                {
                }
                foreach (var pendingIndex in retained)
                {
                    queue.Enqueue(pendingIndex);
                    queueSignal.Release();
                }
            }

            for (var index = start; index < end; index++)
            {
                if (Tiles[index].Thumbnail is not null || Tiles[index].IsLoading || !queued.Add(index))
                    continue;
                if (queue.Count >= MaximumQueuedRequests)
                {
                    queued.Remove(index);
                    break;
                }
                Tiles[index].SetLoading(true);
                queue.Enqueue(index);
                queueSignal.Release();
            }
        }
    }

    public void SelectTile(KeyFrameOverviewTile tile)
    {
        if (closing || selecting)
            return;

        selecting = true;
        SelectedTime = tile.Timestamp;
        DialogResult = true;
        // 双击释放事件结束后再关闭窗口，避免原生窗口把后续输入当作激活操作。
        Dispatcher.UIThread.Post(CloseWindow, DispatcherPriority.Background);
    }

    [RelayCommand]
    private void Cancel()
    {
        DialogResult = false;
        CloseWindow();
    }

    public void CloseWindow()
    {
        if (closing)
            return;
        CancelSafely(cancellation);
        CancelSafely(rangeCancellation);
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    public void CancelWork()
    {
        // Avalonia/DialogManager 可能在 Closed 之后再次触发 Closing，取消必须允许重复调用。
        if (!closing)
            CancelSafely(cancellation);
    }

    public async Task OnClosedAsync()
    {
        if (closing)
            return;
        closing = true;
        if (!initialized)
            initializationCompletion.TrySetResult();
        CancelSafely(cancellation);
        CancelSafely(rangeCancellation);
        await initializationCompletion.Task.ConfigureAwait(true);
        try
        {
            if (worker is not null)
                await worker.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }

        foreach (var tile in Tiles)
            tile.Dispose();
        Tiles = [];
        await service.DisposeAsync();
        CancellationTokenSource? rangeSource;
        lock (queueSync)
        {
            rangeSource = rangeCancellation;
            rangeCancellation = null;
        }
        rangeSource?.Dispose();
        queueSignal.Dispose();
        var workSource = cancellation;
        cancellation = null;
        workSource?.Dispose();
        App.LocalizationService.LanguageChanged -= OnLanguageChanged;
        closedCompletion.TrySetResult();
    }

    partial void OnSelectedSamplingChanged(SamplingOption value)
    {
        if (initialized && !closing)
            RebuildTiles();
    }

    partial void OnThumbnailWidthChanged(int value)
    {
        OnPropertyChanged(nameof(ThumbnailHeight));
        ClearCachedThumbnails();
    }

    private void RebuildTiles()
    {
        CancelRangeRequests();
        ClearQueue();
        foreach (var tile in Tiles)
            tile.Dispose();

        var step = SelectedSampling.Mode switch
        {
            KeyFrameOverviewSampling.EveryKeyFrame => 1,
            KeyFrameOverviewSampling.Every2 => 2,
            KeyFrameOverviewSampling.Every5 => 5,
            KeyFrameOverviewSampling.Every10 => 10,
            _ => Math.Max(1, (int)Math.Ceiling(keyFrames.Count / (double)AutomaticTileLimit))
        };
        var selected = new List<KeyFrameOverviewTile>((keyFrames.Count + step - 1) / step);
        for (var index = 0; index < keyFrames.Count; index += step)
            selected.Add(new KeyFrameOverviewTile(keyFrames[index]));
        Tiles = selected;
        requestedStart = -1;
        requestedEnd = -1;
        OnPropertyChanged(nameof(Tiles));
        OnPropertyChanged(nameof(HasTiles));
    }

    private async Task ProcessQueueAsync(CancellationToken token)
    {
        while (true)
        {
            await queueSignal.WaitAsync(token).ConfigureAwait(false);
            int index;
            CancellationToken requestToken;
            lock (queueSync)
            {
                if (!queue.TryDequeue(out index))
                    continue;
                queued.Remove(index);
                requestToken = rangeCancellation?.Token ?? token;
            }

            if (index < 0 || index >= Tiles.Count)
                continue;
            var tile = Tiles[index];
            if (!tile.IsLoading)
                continue;
            var entry = GetEntry(index);

            Bitmap? bitmap = null;
            try
            {
                bitmap = await service.DecodeThumbnailAsync(
                    entry, ThumbnailWidth, ThumbnailHeight, requestToken).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (closing || token.IsCancellationRequested || index >= Tiles.Count || !ReferenceEquals(Tiles[index], tile))
                    {
                        bitmap?.Dispose();
                        bitmap = null;
                        return;
                    }
                    tile.SetThumbnail(bitmap);
                    bitmap = null;
                    TouchCache(index);
                });
            }
            catch (OperationCanceledException)
            {
                bitmap?.Dispose();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (index >= 0 && index < Tiles.Count && ReferenceEquals(Tiles[index], tile))
                        tile.SetLoading(false);
                });
                if (token.IsCancellationRequested)
                    throw;
            }
            catch (Exception exception)
            {
                bitmap?.Dispose();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (index >= 0 && index < Tiles.Count && ReferenceEquals(Tiles[index], tile))
                        tile.SetThumbnail(null, exception.Message);
                });
            }
        }
    }

    private KeyFrameIndexEntry GetEntry(int tileIndex)
    {
        var step = SelectedSampling.Mode switch
        {
            KeyFrameOverviewSampling.EveryKeyFrame => 1,
            KeyFrameOverviewSampling.Every2 => 2,
            KeyFrameOverviewSampling.Every5 => 5,
            KeyFrameOverviewSampling.Every10 => 10,
            _ => Math.Max(1, (int)Math.Ceiling(keyFrames.Count / (double)AutomaticTileLimit))
        };
        return keyFrames[Math.Min(keyFrames.Count - 1, tileIndex * step)];
    }

    private void TouchCache(int index)
    {
        cacheOrder.Remove(index);
        cacheOrder.AddFirst(index);
        while (cacheOrder.Count > MaximumCacheEntries)
        {
            var evicted = cacheOrder.Last!.Value;
            cacheOrder.RemoveLast();
            if (evicted >= 0 && evicted < Tiles.Count)
                Tiles[evicted].SetThumbnail(null);
        }
    }

    private void ClearCachedThumbnails()
    {
        CancelRangeRequests();
        cacheOrder.Clear();
        foreach (var tile in Tiles)
            tile.SetThumbnail(null);
        ClearQueue();
    }

    private void CancelRangeRequests()
    {
        CancellationTokenSource? source;
        lock (queueSync)
        {
            source = rangeCancellation;
            rangeCancellation = null;
            requestedStart = -1;
            requestedEnd = -1;
        }
        CancelSafely(source);
        source?.Dispose();
    }

    private static void CancelSafely(CancellationTokenSource? source)
    {
        try
        {
            source?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 关闭流程可能与窗口事件并发，已释放的取消源无需再次处理。
        }
    }

    private void ClearQueue()
    {
        lock (queueSync)
        {
            queue.Clear();
            queued.Clear();
            while (queueSignal.Wait(0))
            {
            }
            foreach (var tile in Tiles)
                tile.SetLoading(false);
        }
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(WindowTitle));
        StatusText = string.Empty;
    }

    public sealed record SamplingOption(KeyFrameOverviewSampling Mode, string Label);
}
