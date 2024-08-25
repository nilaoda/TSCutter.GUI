using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.FileSystem;
using HanumanInstitute.MvvmDialogs.FrameworkDialogs;
using TSCutter.GUI.Models;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public IRelayCommand<DragEventArgs> DragOverCommand { get; }
    public IAsyncRelayCommand<DragEventArgs> DropCommand { get; }

    public string WindowTitle
    {
        get
        {
            var defaultTitle = "TSCutter.GUI - Alpha.0826";
            if (!string.IsNullOrEmpty(VideoPath))
                return $"{defaultTitle} - {Path.GetFileName(VideoPath)}";
            return defaultTitle;
        }
    }
    private VideoInstance? _videoInstance;
    private readonly IDialogService _dialogService;
    
    public MainWindowViewModel(IDialogService dialogService)
    {
        _dialogService = dialogService;
        DragOverCommand = new RelayCommand<DragEventArgs>(DragOverHandler);
        DropCommand = new AsyncRelayCommand<DragEventArgs>(DropHandler);
    }

    private long PositionInFile => _videoInstance!.PositionInFile;
    
    [ObservableProperty]
    private ObservableCollection<PickedClip> _clips = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddClipCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveClipCommand))]
    [NotifyCanExecuteChangedFor(nameof(MarkClipStartCommand))]
    [NotifyCanExecuteChangedFor(nameof(MarkClipEndCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveVideoClickCommand))]
    private PickedClip? _selectedClip;

    [RelayCommand(CanExecute = nameof(HasInitialized))]
    private void AddClip()
    {
        SelectedClip = new PickedClip()
        {
            InFileInfo = new FileInfo(VideoPath),
            StartTime = CurrentTime,
            StartPosition = PositionInFile,
            EndTime = DurationMax,
        };
        Clips.Add(SelectedClip);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedClip))]
    private void RemoveClip()
    {
        var index = Clips.ToList().FindIndex(x => x.ClipID == SelectedClip?.ClipID);
        if (index <= -1) return;
        
        Clips.RemoveAt(index);   
        SelectedClip = null;
    }

    [RelayCommand(CanExecute = nameof(HasSelectedClip))]
    private void MarkClipStart()
    {
        SelectedClip!.StartTime = CurrentTime;
        SelectedClip!.StartPosition = PositionInFile;
        if (!(SelectedClip!.EndTime <= CurrentTime)) return;
        
        SelectedClip!.EndTime = DurationMax;
        SelectedClip!.EndPosition = -1;
    }

    [RelayCommand(CanExecute = nameof(HasSelectedClip))]
    private void MarkClipEnd()
    {
        SelectedClip!.EndTime = CurrentTime;
        SelectedClip!.EndPosition = PositionInFile;
        if (!(SelectedClip!.StartTime >= CurrentTime)) return;
        
        SelectedClip!.StartTime = 0;
        SelectedClip!.StartPosition = 0;
    }

    [RelayCommand(CanExecute = nameof(HasSelectedClip))]
    private async Task SaveVideoClickAsync() => await SaveVideoAsync();
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private string _videoPath;

    [ObservableProperty]
    private Bitmap? _decodedBitmap;
    [ObservableProperty]
    private double _imageTranslateX;
    [ObservableProperty]
    private double _imageTranslateY;

    [ObservableProperty]
    private double _zoomFactor = 1.0;
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusInfoText))]
    private string _videoInfoText = "Please Load Video";
    
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddClipCommand))]
    private double _durationMax = 0.0;
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusInfoText))]
    private double _currentTime = 0.0;
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusInfoText))]
    private long _decodeCost = 0L;

    public string StatusInfoText => $"{VideoInfoText} | " +
                                    $"Position: {CommonUtil.FormatSeconds(CurrentTime)} | " +
                                    $"DecodeCost: {DecodeCost}ms";

    [RelayCommand]
    private async Task LoadVideoClickAsync()
    {
        var settings = new OpenFileDialogSettings()
        {
            Title = "Open TS file",
            Filters = new List<FileFilter>()
            {
                new("MPEG2-TS Video", ["ts"]),
            }
        };
        var result = await _dialogService.ShowOpenFilesDialogAsync(this, settings);
        if (!result.Any()) return;

        VideoPath = result[0].LocalPath;
        await LoadVideoAsync();
    }

    [RelayCommand]
    private async Task PrevGopClickAsync() => await DrawNextFrameAsync(-1);

    [RelayCommand]
    private async Task NextGopClickAsync() => await DrawNextFrameAsync(1);

    [RelayCommand]
    private async Task Prev10GopClickAsync() => await DrawNextFrameAsync(-10);

    [RelayCommand]
    private async Task Next10GopClickAsync() => await DrawNextFrameAsync(10);

    [RelayCommand]
    private void ZoomIn() => ZoomFactor = Math.Min(1, ZoomFactor + 0.1);
    
    [RelayCommand]
    private void ZoomOut() => ZoomFactor = Math.Max(0.1, ZoomFactor - 0.1);
    
    [RelayCommand]
    private void ZoomNone() => ZoomFactor = 1.0;
    
    [RelayCommand]
    private void ZoomFill() => ResetDecodeBitmap();

    public async Task SeekToTimeAsync(TimeSpan timeSpan) => await _videoInstance!.SeekToTimeAsync(timeSpan);
    
    public async Task DrawNextFrameAsync(int count = 1)
    {
        if (_videoInstance is not { Inited: true }) return;
        
        DecodeCost = 0;
        var stopwatch = Stopwatch.StartNew();
        var decodeResult = await _videoInstance!.DecodeNextFrameAsync(count);
        DecodeCost = stopwatch.ElapsedMilliseconds;
        Console.WriteLine($"PositionInFile: {_videoInstance.PositionInFile}");
        stopwatch.Stop();
        DecodedBitmap = decodeResult.Bitmap;
        CurrentTime = decodeResult.FrameTimestamp.TotalSeconds;
    }

    private void ClearVars()
    {
        SelectedClip = null;
        Clips.Clear();
    }

    private async Task LoadVideoAsync()
    {
        ClearVars();
        _videoInstance?.Close();
        
        _videoInstance = new VideoInstance(VideoPath);
        await _videoInstance.InitVideoAsync();
        VideoInfoText = _videoInstance.GetVideoInfoText();
        DurationMax = _videoInstance.GetVideoDurationInSeconds();

        // decode
        await DrawNextFrameAsync(1);
    }
    
    private void DragOverHandler(DragEventArgs? e)
    {
        if (e is null) return;
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async Task DropHandler(DragEventArgs? e)
    {
        if (e is null) return;
        if (!e.Data.Contains(DataFormats.Files)) return;
        
        var fileNames = e.Data.GetFiles()?.ToArray();
        if (fileNames is not { Length: > 0 }) return;
        
        var filePath = fileNames[0].Path.LocalPath;
        var ext = Path.GetExtension(filePath);
        if (File.Exists(filePath) && ext is ".ts" && VideoPath != filePath)
        {
            VideoPath = filePath;
            await LoadVideoAsync();
        }
    }

    private void ResetDecodeBitmap()
    {
        ImageTranslateX = 0;
        ImageTranslateY = 0;
        if (DecodedBitmap is null) return;

        using var stream = new MemoryStream();
        DecodedBitmap.Save(stream);
        stream.Position = 0;
        DecodedBitmap = new Bitmap(stream);
    }

    public void Close()
    {
        _videoInstance?.Close();
        _videoInstance?.Dispose();
    }

    private bool HasSelectedClip() => SelectedClip is not null;
    private bool HasInitialized() => _videoInstance is not null;

    private async Task SaveVideoAsync()
    {
        var defaultName = Path.GetFileNameWithoutExtension(SelectedClip!.InFileInfo.FullName)
            + $"_({CommonUtil.FormatSeconds(SelectedClip!.StartTime, true)}-{CommonUtil.FormatSeconds(SelectedClip!.EndTime, true)}).ts";
        var settings = new SaveFileDialogSettings
        {
            Title = "Save Your Clip",
            SuggestedStartLocation = new DesktopDialogStorageFolder(Path.GetDirectoryName(SelectedClip!.InFileInfo.FullName)!),
            SuggestedFileName = defaultName,
            Filters = new List<FileFilter>()
            {
                new("MPEG2-TS", new[] { "ts" }),
                new("All Files", "*")
            },
            DefaultExtension = "ts"
        };
        var result = await _dialogService.ShowSaveFileDialogAsync(this, settings);
        if (result is null) return;
        
        var dialogViewModel = _dialogService.CreateViewModel<OutputWindowViewModel>();
        dialogViewModel.SelectedClip = SelectedClip;
        dialogViewModel.OutputPath = result!.Path!.LocalPath;
        var success = await _dialogService.ShowDialogAsync(this, dialogViewModel).ConfigureAwait(true);
        if (success == true)
        {
            SelectedClip!.OutputFileInfo = new FileInfo(dialogViewModel.OutputPath);
        }
    }
}