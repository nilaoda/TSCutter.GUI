using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using TSCutter.GUI.Models;
using TSCutter.GUI.Services;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.ViewModels;

public sealed partial class FrameSearchWindowViewModel : ViewModelBase, IModalDialogViewModel
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly FrameSearchService service = new();
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task searchTask = Task.CompletedTask;
    private Task? closeTask;
    private TaskCompletionSource? resumeSearch;
    private bool started;

    public event Action? RequestClose;
    public bool? DialogResult { get; private set; }
    public TimeSpan? SelectedTime { get; private set; }
    public string FilePath { get; set; } = string.Empty;
    public SavedFrameTemplate? Template { get; set; }
    public TimeSpan StartTime { get; set; }
    public Task ClosedTask => closed.Task;
    public string WindowTitle => string.Format(
        LocalizationManager.Instance.String_FrameSearch_SearchTitle,
        Template?.Name ?? string.Empty);
    public ObservableCollection<FrameSearchMatch> Matches { get; } = [];

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    private double progressPercent;

    [ObservableProperty]
    private bool isSearching;

    [ObservableProperty]
    private bool isPaused;

    [ObservableProperty]
    private Bitmap? referenceImage;

    public void Start()
    {
        if (started || Template is null || string.IsNullOrEmpty(FilePath)) return;
        started = true;
        searchTask = SearchAsync();
    }

    private async Task SearchAsync()
    {
        IsSearching = true;
        StatusText = LocalizationManager.Instance.String_FrameSearch_Searching;
        var progress = new Progress<double>(fraction =>
        {
            if (cancellation.IsCancellationRequested || !IsSearching) return;
            ProgressPercent = fraction * 100;
            if (!IsPaused) UpdateSearchingStatus();
        });
        try
        {
            using var stream = new MemoryStream(Template!.Jpeg, writable: false);
            ReferenceImage = new Bitmap(stream);
            await service.SearchAsync(FilePath, Template!.Jpeg, StartTime, ReceiveMatchAsync,
                PauseAfterMatchAsync, progress, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            StatusText = Matches.Count == 0
                ? LocalizationManager.Instance.String_FrameSearch_NoResults
                : Matches.All(match => !match.IsLikelyMatch)
                ? LocalizationManager.Instance.String_FrameSearch_WeakResults
                : string.Format(LocalizationManager.Instance.String_FrameSearch_Found,
                    Matches.Count);
        }
        catch (OperationCanceledException)
        {
            if (!cancellation.IsCancellationRequested)
                StatusText = LocalizationManager.Instance.String_FrameSearch_Cancelled;
        }
        catch (Exception error)
        {
            if (!cancellation.IsCancellationRequested)
                StatusText = string.Format(LocalizationManager.Instance.String_FrameSearch_Failed,
                    error.Message);
        }
        finally
        {
            if (!cancellation.IsCancellationRequested) IsSearching = false;
        }
    }

    private async Task ReceiveMatchAsync(
        FrameSearchMatch match, bool replacesPrevious, TimeSpan? evictStart)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (cancellation.IsCancellationRequested)
            {
                match.Dispose();
                return;
            }
            if (evictStart is { } removedStart)
            {
                var removed = Matches.FirstOrDefault(item => item.Start == removedStart);
                if (removed is not null)
                {
                    Matches.Remove(removed);
                    removed.Dispose();
                }
            }
            var index = -1;
            if (replacesPrevious)
            {
                for (var i = 0; i < Matches.Count; i++)
                {
                    if (Matches[i].Start != match.Start) continue;
                    index = i;
                    break;
                }
            }
            if (index >= 0)
            {
                var old = Matches[index];
                Matches[index] = match;
                old.Dispose();
            }
            else
                Matches.Add(match);

            if (!IsPaused) UpdateSearchingStatus();
        });
    }

    private async Task PauseAfterMatchAsync(CancellationToken token)
    {
        Task? waitForResume = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (cancellation.IsCancellationRequested) return;
            resumeSearch = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            waitForResume = resumeSearch.Task;
            IsPaused = true;
            StatusText = LocalizationManager.Instance.String_FrameSearch_Paused;
        });
        if (waitForResume is not null)
            await waitForResume.WaitAsync(token);
    }

    private void UpdateSearchingStatus()
    {
        StatusText = Matches.Count == 0
            ? string.Format(LocalizationManager.Instance.String_FrameSearch_Progress, ProgressPercent)
            : string.Format(LocalizationManager.Instance.String_FrameSearch_FoundWhileSearching,
                Matches.Count, ProgressPercent);
    }

    public void SelectFrame(FrameSearchPreviewFrame frame)
    {
        if (cancellation.IsCancellationRequested) return;
        SelectedTime = frame.Time;
        cancellation.Cancel();
        DialogResult = true;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void ContinueSearch()
    {
        if (!IsPaused || cancellation.IsCancellationRequested) return;
        IsPaused = false;
        UpdateSearchingStatus();
        resumeSearch?.TrySetResult();
        resumeSearch = null;
    }

    [RelayCommand]
    private void Cancel()
    {
        cancellation.Cancel();
        DialogResult = false;
        RequestClose?.Invoke();
    }

    public void CancelWork() => cancellation.Cancel();

    public Task OnClosedAsync() => closeTask ??= CloseCoreAsync();

    private async Task CloseCoreAsync()
    {
        try
        {
            cancellation.Cancel();
            await searchTask;
            foreach (var match in Matches) match.Dispose();
            Matches.Clear();
            ReferenceImage?.Dispose();
            ReferenceImage = null;
            cancellation.Dispose();
        }
        finally
        {
            closed.TrySetResult();
        }
    }
}
