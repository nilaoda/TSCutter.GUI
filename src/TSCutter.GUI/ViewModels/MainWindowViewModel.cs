using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Classic.Avalonia.Theme;
using Classic.CommonControls.Dialogs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.FileSystem;
using HanumanInstitute.MvvmDialogs.FrameworkDialogs;
using TSCutter.GUI.Models;
using TSCutter.GUI.Themes;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    // 初始化主题列表
    private static ThemeModel DarkTheme = new("Standard (Dark)", CustomTheme.DarkStandard);
    private static List<ThemeModel> Themes = [..ClassicTheme.AllVariants.Select(x => new ThemeModel(x)), DarkTheme];

    private const string PleaseLoadTip = "Please Load Video ";
    public string WindowTitle
    {
        get
        {
            var defaultTitle = "TSCutter.GUI - Alpha.251120";
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
        var actualThemeVariant = Application.Current?.ActualThemeVariant;
        var isDarkMode = actualThemeVariant == ThemeVariant.Dark
                         || actualThemeVariant?.InheritVariant == ThemeVariant.Dark;
        SelectedTheme = isDarkMode ? DarkTheme : Themes.First();
        // 构造菜单项
        BuildThemeMenuItems();
    }

    private long PositionInFile => _videoInstance!.PositionInFile;
    private long CurrentPts => _videoInstance!.CurrentPts;

    [ObservableProperty]
    private ThemeModel _selectedTheme;

    partial void OnSelectedThemeChanged(ThemeModel value)
    {
        if (value != null)
        {
            // 切换主题
            Application.Current!.RequestedThemeVariant = value.Variant;
        }
    }
    
    [ObservableProperty]
    private ObservableCollection<MenuItem> _themeMenuItems = new();

    [ObservableProperty]
    public partial ObservableCollection<PickedClip> Clips { get; set; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(
        nameof(AddClipCommand), nameof(RemoveClipCommand), nameof(MarkClipStartCommand),
        nameof(MarkClipEndCommand), nameof(SaveVideoClickCommand), nameof(CloseVideoClickCommand),
        nameof(SaveFrameClickCommand), nameof(ShowMediaInfoClickCommand)
    )]
    public partial PickedClip? SelectedClip { get; set; }
    
    [RelayCommand]
    private async Task JumpToStartTimeAsync(object? time)
    {
        if (time is PickedClip { StartTime: > -1 } pickedClip)
        {
            await JumpToTimeAsync(pickedClip.StartPts);
        }
    }
    
    [RelayCommand]
    private async Task JumpToEndTimeAsync(object? time)
    {
        if (time is PickedClip { EndTime: > -1 } pickedClip)
        {
            await JumpToTimeAsync(pickedClip.EndPts);
        }
    }

    private async Task JumpToTimeAsync(long time)
    {
        await SeekFileAsync(time);
        // 解码至目标关键帧
        await DrawNextFrameAsync(1);
        while (CurrentPts < time)
        {
            await DrawNextFrameAsync(1);
        }
    }
    
    [RelayCommand]
    private void FindFile(object? fileInfo)
    {
        if (fileInfo is FileInfo file)
        {
            CommonUtil.OpenFileLocation(file.FullName);
        }
    }

    [RelayCommand]
    private async Task JumpToClickAsync()
    {
        if (!IsVideoInitialized)
        {
            await ShowMessageAsync("Please load a video", "Video Not Initialized", MessageBoxIcon.Information);
            return;
        }
        
        var vm = _dialogService.CreateViewModel<JumpTimeViewModel>();
        var currentTimeStr = CommonUtil.FormatSeconds(CurrentTime);
        vm.InputText = currentTimeStr;
        var result = await _dialogService.ShowDialogAsync(this, vm);
        
        if (result is false || result is null || currentTimeStr == vm.InputText) return;
        
        if (CommonUtil.TryParseFormattedTime(vm.InputText, out var newTime) && newTime < DurationMax)
        {
            await SeekToTimeAsync(TimeSpan.FromSeconds(Math.Max(0, newTime - 1)));
            await DrawNextFrameAsync(1);
            return;
        }

        await ShowMessageAsync("Please input a valid time", "Wrong Format", MessageBoxIcon.Error);
    }

    [RelayCommand]
    private async Task ProcessCommandLineAsync()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1]) && Path.GetExtension(args[1]).ToLower() is ".ts")
        {
            VideoPath = args[1];
            await LoadVideoAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(IsVideoInitialized))]
    private void AddClip()
    {
        var newClip = new PickedClip()
        {
            InFileInfo = new FileInfo(VideoPath),
            StartTime = CurrentTime,
            StartPosition = PositionInFile,
            StartPts = CurrentPts,
            EndTime = DurationMax,
        };
        Clips.Add(newClip);
        SelectedClip = newClip;
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
        SelectedClip!.StartPts = CurrentPts;
        SelectedClip!.StartPosition = PositionInFile;
        if (!(SelectedClip!.EndTime <= CurrentTime)) return;
        
        SelectedClip!.EndTime = DurationMax;
        SelectedClip!.EndPosition = -1;
    }

    [RelayCommand(CanExecute = nameof(HasSelectedClip))]
    private void MarkClipEnd()
    {
        SelectedClip!.EndTime = CurrentTime;
        SelectedClip!.EndPts = CurrentPts;
        SelectedClip!.EndPosition = PositionInFile;
        if (!(SelectedClip!.StartTime >= CurrentTime)) return;
        
        SelectedClip!.StartTime = 0;
        SelectedClip!.StartPosition = 0;
    }

    [RelayCommand(CanExecute = nameof(HasSelectedClip))]
    private async Task SaveVideoClickAsync() => await SaveVideoAsync();

    [RelayCommand(CanExecute = nameof(IsVideoInitialized))]
    private void CloseVideoClick()
    {
        Close();
        ClearVars();
    }

    [RelayCommand(CanExecute = nameof(IsVideoInitialized))]
    private async Task ShowMediaInfoClickAsync()
    {
        var dialogViewModel = _dialogService.CreateViewModel<MediainfoWindowViewModel>();
        dialogViewModel.FilePath = VideoPath;
        await _dialogService.ShowDialogAsync(this, dialogViewModel);
    }
    
    [RelayCommand(CanExecute = nameof(IsVideoInitialized))]
    private async Task SaveFrameClickAsync()
    {
        var defaultName = Path.GetFileNameWithoutExtension(VideoPath) + $"_{CommonUtil.FormatSeconds(CurrentTime, true)}.png";
        var settings = new SaveFileDialogSettings
        {
            Title = "Save a frame",
            SuggestedStartLocation =  new DesktopDialogStorageFolder(Path.GetDirectoryName(VideoPath)!),
            SuggestedFileName = defaultName,
            Filters = [new("PNG Images", ["png"])],
            DefaultExtension = "png"
        };
        var result = await _dialogService.ShowSaveFileDialogAsync(this, settings);
        if (result is null) return;

        using var stream = File.Create(result!.Path!.LocalPath);
        DecodedBitmap?.Save(stream);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    public partial string VideoPath { get; set; }

    public Bitmap? DecodedBitmap
    {
        get => field;
        set
        {
            if (!EqualityComparer<Bitmap?>.Default.Equals(field, value))
            {
                field?.Dispose();
                field = value;
                OnPropertyChanged();
                SaveFrameClickCommand.NotifyCanExecuteChanged();
                ShowMediaInfoClickCommand.NotifyCanExecuteChanged();
            }
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomFactorStr))]
    public partial double ZoomFactor { get; set; } = 1.0;
    [ObservableProperty]
    public partial double OffsetX { get; set; } = 0;
    [ObservableProperty]
    public partial double OffsetY { get; set; } = 0;

    public double MaxZoomFactor => 3.0;
    public double MinZoomFactor => 0.1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusInfoText))]
    public partial string VideoInfoText { get; set; } = PleaseLoadTip;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddClipCommand))]
    public partial double DurationMax { get; set; } = 0.0;

    [ObservableProperty]
    public partial double CurrentTime { get; set; } = 0.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusInfoText))]
    public partial long DecodeCost { get; set; } = 0L;

    public string StatusInfoText => IsVideoInitialized ? $"{VideoInfoText} | {DecodeCost,3}ms" : PleaseLoadTip;
    public string ZoomFactorStr => $"Scale: {ZoomFactor * 100.0:0}%";

    [RelayCommand]
    private void OpenFileInExplorer()
    {
        CommonUtil.OpenFileLocation(VideoPath);
    }

    [RelayCommand]
    private void ExitApplication()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopApp)
        {
            desktopApp.Shutdown();
        }
    }
    
    [RelayCommand]
    private void CheckUpdate()
    {
        Process.Start(new ProcessStartInfo("https://github.com/nilaoda/TSCutter.GUI/releases") { UseShellExecute = true });
    }
    
    [RelayCommand]
    private async Task ShowAboutDialogAsync()
    {
        var dialogViewModel = _dialogService.CreateViewModel<AboutWindowViewModel>();
        await _dialogService.ShowDialogAsync(this, dialogViewModel);
    }
    
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
    private async Task Prev20GopClickAsync() => await DrawNextFrameAsync(-20);

    [RelayCommand]
    private async Task Next20GopClickAsync() => await DrawNextFrameAsync(20);

    [RelayCommand]
    private void ZoomIn() => ZoomFactor = Math.Min(MaxZoomFactor, ZoomFactor + 0.1);
    
    [RelayCommand]
    private void ZoomOut() => ZoomFactor = Math.Max(MinZoomFactor, ZoomFactor - 0.1);

    [RelayCommand]
    private void ZoomNone()
    {
        ZoomFactor = 1.0;
        OffsetX = 0;
        OffsetY = 0;
    }

    [RelayCommand]
    private void ToggleThemeClick()
    {
        var currentTheme = Application.Current!.RequestedThemeVariant;
        // var fluentAvaloniaTheme = App.Current.Styles[0] as FluentAvaloniaTheme;
        if (currentTheme == ThemeVariant.Dark)
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        else if (currentTheme == ThemeVariant.Light)
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
    }

    [RelayCommand]
    private async Task SettingsClickAsync()
    {
        await ShowMessageAsync("The settings page is not yet fully developed", "Settings", MessageBoxIcon.Warning);
    }

    public void SwitchToDarkMode()
    {
        SelectedTheme = DarkTheme;
    }

    public async Task SeekToTimeAsync(TimeSpan timeSpan) => await _videoInstance!.SeekToTimeAsync(timeSpan);
    public async Task SeekFileAsync(long pts) => await _videoInstance!.SeekFileAsync(pts);
    
    public async Task DrawNextFrameAsync(int count = 1)
    {
        if (!IsVideoInitialized) return;

        try
        {
            DecodeCost = 0;
            var stopwatch = Stopwatch.StartNew();
            var decodeResult = await _videoInstance!.DecodeNextFrameAsync(count);
            DecodeCost = stopwatch.ElapsedMilliseconds;
            Console.WriteLine($"PositionInFile: {_videoInstance.PositionInFile}");
            stopwatch.Stop();
            DecodedBitmap = decodeResult.Bitmap;
            CurrentTime = decodeResult.FrameTimestamp.TotalSeconds;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            await ShowMessageAsync(e.Message, "Failed to decode", MessageBoxIcon.Error);
        }
    }

    private void ClearVars()
    {
        VideoInfoText = "Please Load Video";
        DecodedBitmap = null;
        Clips.Clear();
        SelectedClip = null;
        DurationMax = 0.0;
        CurrentTime = 0.0;
        DecodeCost = 0L;
    }

    private async Task LoadVideoAsync()
    {
        try
        {
            ClearVars();
            _videoInstance?.Close();
        
            _videoInstance = new VideoInstance(VideoPath);
            await _videoInstance.InitVideoAsync();
            VideoInfoText = _videoInstance.GetVideoInfoText();
            DurationMax = _videoInstance.GetVideoDurationInSeconds();
        
            CloseVideoClickCommand.NotifyCanExecuteChanged();
            SaveFrameClickCommand.NotifyCanExecuteChanged();
            ShowMediaInfoClickCommand.NotifyCanExecuteChanged();

            // decode
            await DrawNextFrameAsync(1);
            // 发送消息通知 View 执行 FitCommand
            WeakReferenceMessenger.Default.Send(new FitMessage());
        }
        catch (Exception e)
        {
            VideoPath = string.Empty;
            ClearVars();
            _videoInstance?.Close();
            Console.WriteLine($"Failed to load video: {e}");
            await ShowMessageAsync(e.Message, "Failed to load video", MessageBoxIcon.Error);
        }
    }

    private async Task ShowMessageAsync(string message, string title = "TSCutter", MessageBoxIcon icon = MessageBoxIcon.None)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopApp)
        {
            await MessageBox.ShowDialog(desktopApp.MainWindow!, message, title, MessageBoxButtons.Ok, icon);
        }
    }
    
    [RelayCommand]
    private void DragOver(DragEventArgs? e)
    {
        var filePath = CheckDropDataIsTsFile(e);
        e!.DragEffects = filePath is not null ? DragDropEffects.Copy : DragDropEffects.None;
    }

    [RelayCommand]
    private async Task Drop(DragEventArgs? e)
    {
        var filePath = CheckDropDataIsTsFile(e);
        if (filePath is not null)
        {
            VideoPath = filePath;
            await LoadVideoAsync();
        }
    }

    private string? CheckDropDataIsTsFile(DragEventArgs? e)
    {
        if (e is null) return null;
        if (!e.Data.Contains(DataFormats.Files)) return null;
        
        var fileNames = e.Data.GetFiles()?.ToArray();
        if (fileNames is not { Length: > 0 }) return null;
        var filePath = fileNames[0].Path.LocalPath;
        var ext = Path.GetExtension(filePath);
        if (File.Exists(filePath) && ext.ToLower() is ".ts" && VideoPath != filePath)
        {
            return filePath;
        }
        return null;
    }

    public void Close()
    {
        _videoInstance?.Close();
        _videoInstance?.Dispose();
        _videoInstance = null;
        VideoPath = string.Empty;
    }

    private bool HasSelectedClip => SelectedClip is not null;
    public bool IsVideoInitialized => _videoInstance is { Inited: true };
    
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
        await _dialogService.ShowDialogAsync(this, dialogViewModel).ConfigureAwait(true);
        if (dialogViewModel.Exception is not null)
        {
            await ShowMessageAsync(dialogViewModel.Exception.Message, "Error", MessageBoxIcon.Error);
            Console.WriteLine(dialogViewModel.Exception);
            return;
        }
        SelectedClip!.OutputFileInfo = new FileInfo(dialogViewModel.OutputPath);
    }
    
    private void BuildThemeMenuItems()
    {
        ThemeMenuItems.Clear();
        foreach (var theme in Themes)
        {
            var menuItem = new MenuItem()
            {
                Header = theme.Name,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = SelectedTheme == theme
            };
            menuItem.Click += (r, s) => SelectedTheme = theme;;
            ThemeMenuItems.Add(menuItem);
        }
    }
}