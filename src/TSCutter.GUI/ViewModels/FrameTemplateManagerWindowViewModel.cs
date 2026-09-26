using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using TSCutter.GUI.Models;
using TSCutter.GUI.Services;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.ViewModels;

public sealed partial class FrameTemplateManagerWindowViewModel : ViewModelBase, IModalDialogViewModel
{
    private readonly SavedFrameStore store = new();
    private readonly CancellationTokenSource cancellation = new();
    private SavedFrameLibrary library = new();
    private bool initialized;
    private bool closed;

    public event Action? RequestClose;
    public bool? DialogResult { get; private set; }
    public SavedFrameTemplate? ChosenTemplate { get; private set; }
    public string FilePath { get; set; } = string.Empty;
    public double CurrentTimeSeconds { get; set; }
    public bool CanSearchFromCurrent => CurrentTimeSeconds > 0;
    public string SearchFromCurrentText => string.Format(
        LocalizationManager.Instance.String_FrameSearch_SearchFromCurrent,
        CommonUtil.FormatSeconds(CurrentTimeSeconds, true));
    public ObservableCollection<SavedFrameTile> Templates { get; } = [];

    [ObservableProperty]
    private bool searchFromCurrent;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCurrentCommand), nameof(RenameCommand),
        nameof(DeleteCommand), nameof(SearchCommand))]
    private bool isBusy;

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    private string newNameInput = string.Empty;

    [ObservableProperty]
    private string renameInput = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedTemplate))]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand), nameof(DeleteCommand), nameof(SearchCommand))]
    private SavedFrameTile? selectedTemplate;

    public bool HasSelectedTemplate => SelectedTemplate is not null;

    partial void OnSelectedTemplateChanged(SavedFrameTile? value)
    {
        RenameInput = value?.Template.Name ?? string.Empty;
    }

    private string SuggestedNewName()
    {
        var name = Path.GetFileNameWithoutExtension(FilePath) + " " +
                   CommonUtil.FormatSeconds(CurrentTimeSeconds, true);
        return name[..Math.Min(name.Length, 100)];
    }

    public async Task InitializeAsync()
    {
        if (initialized) return;
        IsBusy = true;
        NewNameInput = SuggestedNewName();
        try
        {
            var loaded = await store.LoadAsync(cancellation.Token);
            var tiles = new System.Collections.Generic.List<SavedFrameTile>();
            try
            {
                foreach (var item in loaded.Templates) tiles.Add(new SavedFrameTile(item));
            }
            catch
            {
                foreach (var tile in tiles) tile.Dispose();
                throw;
            }
            if (closed)
            {
                foreach (var tile in tiles) tile.Dispose();
                return;
            }
            library = loaded;
            foreach (var tile in tiles) Templates.Add(tile);
            initialized = true;
            SelectedTemplate = null;
            StatusText = string.Format(LocalizationManager.Instance.String_FrameSearch_SavedCount,
                Templates.Count);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            foreach (var tile in Templates) tile.Dispose();
            Templates.Clear();
            StatusText = string.Format(LocalizationManager.Instance.String_FrameSearch_StoreError,
                error.Message);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            IsBusy = false;
            AddCurrentCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanAddCurrent => initialized && !closed && !IsBusy &&
                                  !string.IsNullOrWhiteSpace(FilePath);
    private bool CanEditSelection => initialized && !closed && !IsBusy &&
                                     SelectedTemplate is not null;

    [RelayCommand(CanExecute = nameof(CanAddCurrent))]
    private async Task AddCurrentAsync()
    {
        var name = NewNameInput?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 100)
        {
            StatusText = LocalizationManager.Instance.String_FrameSearch_InvalidName;
            return;
        }
        IsBusy = true;
        try
        {
            await using var decoder = new KeyFrameOverviewService();
            await decoder.OpenAsync(FilePath, cancellation.Token);
            using var bitmap = await decoder.DecodeThumbnailAsync(
                new KeyFrameIndexEntry(0, 0, TimeSpan.FromSeconds(CurrentTimeSeconds), -1),
                320, 180, cancellation.Token);
            if (bitmap is null)
                throw new InvalidDataException("Unable to decode the current frame.");
            using var image = new MemoryStream();
            ImageUtil.SaveAsJpeg(bitmap, image, 88);
            var item = new SavedFrameTemplate { Name = name, Jpeg = image.ToArray() };
            var next = new SavedFrameLibrary { Templates = [.. library.Templates, item] };
            await store.SaveAsync(next, cancellation.Token);
            library = next;
            var tile = new SavedFrameTile(item);
            Templates.Add(tile);
            SelectedTemplate = tile;
            NewNameInput = string.Empty;
            StatusText = string.Format(LocalizationManager.Instance.String_FrameSearch_SavedCount,
                Templates.Count);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            StatusText = error.Message;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    private async Task RenameAsync()
    {
        var selected = SelectedTemplate!;
        var name = RenameInput?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 100)
        {
            StatusText = LocalizationManager.Instance.String_FrameSearch_InvalidName;
            return;
        }
        IsBusy = true;
        var previousName = selected.Template.Name;
        try
        {
            selected.Template.Name = name;
            await store.SaveAsync(library, cancellation.Token);
            selected.RefreshName();
            StatusText = LocalizationManager.Instance.String_FrameSearch_Renamed;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            selected.Template.Name = previousName;
            StatusText = error.Message;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            selected.Template.Name = previousName;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    private async Task DeleteAsync()
    {
        var selected = SelectedTemplate!;
        IsBusy = true;
        try
        {
            var next = new SavedFrameLibrary
            {
                Templates = library.Templates.Where(item => item.Id != selected.Template.Id).ToList()
            };
            await store.SaveAsync(next, cancellation.Token);
            library = next;
            Templates.Remove(selected);
            selected.Dispose();
            SelectedTemplate = null;
            RenameInput = string.Empty;
            StatusText = string.Format(LocalizationManager.Instance.String_FrameSearch_SavedCount,
                Templates.Count);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            StatusText = error.Message;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    private void Search()
    {
        ChosenTemplate = SelectedTemplate!.Template;
        DialogResult = true;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        DialogResult = false;
        RequestClose?.Invoke();
    }

    public void OnClosed()
    {
        if (closed) return;
        closed = true;
        cancellation.Cancel();
        foreach (var item in Templates) item.Dispose();
        Templates.Clear();
    }

}
