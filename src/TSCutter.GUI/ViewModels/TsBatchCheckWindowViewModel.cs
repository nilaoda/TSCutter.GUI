using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.FileSystem;
using HanumanInstitute.MvvmDialogs.FrameworkDialogs;
using TSCutter.GUI.Models;
using TSCutter.GUI.Services;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.ViewModels;

public partial class TsBatchCheckWindowViewModel : ViewModelBase, IModalDialogViewModel
{
    private readonly IDialogService _dialogService;
    private readonly ILocalizationService _localization;
    private readonly IConfigurationService _configuration;
    private readonly List<TsBatchCheckFileItem> _selectedFiles = [];
    private readonly SemaphoreSlim _addLock = new(1);
    private CancellationTokenSource? _cancellation;
    private bool _isClosed;
    private bool _isRefreshingVisibleFiles;
    private int _generation;

    public TsBatchCheckWindowViewModel(IDialogService dialogService, ILocalizationService localization,
        IConfigurationService configuration)
    {
        _dialogService = dialogService;
        _localization = localization;
        _configuration = configuration;
        _saveLogs = configuration.CurrentConfig.TsBatchCheckSaveLogs;
        _localization.LanguageChanged += RefreshLocalizedText;
    }

    public bool? DialogResult { get; }
    public string WindowTitle => LocalizationManager.Instance.String_TsBatchCheck_Title;
    public BulkObservableCollection<TsBatchCheckFileItem> Files { get; } = [];
    public BulkObservableCollection<TsBatchCheckFileItem> VisibleFiles { get; } = [];
    public bool IsEmpty => Files.Count == 0;
    public bool HasNoVisibleIssues => !IsEmpty && OnlyIssues && VisibleFiles.Count == 0;
    public bool CanModifyFiles => !IsScanning && !IsAddingFiles;
    public bool CanAddFiles => !IsAddingFiles;
    public bool CanRemove => CanModifyFiles && _selectedFiles.Count > 0;
    public bool CanRecheck => CanRemove && HasValidLogDestination;
    public bool CanClear => CanModifyFiles && !IsEmpty;
    public bool CanStart => !IsScanning && !IsAddingFiles && Files.Any(IsPending) &&
        HasValidLogDestination;
    private bool HasValidLogDestination => !SaveLogs || SaveBesideSource || Directory.Exists(OutputFolder);
    public bool CanStop => IsScanning && _cancellation?.IsCancellationRequested == false;
    public bool CanViewResult => SelectedFile?.Result is not null;
    public bool CanExport => !IsEmpty;
    public bool CanBrowseFolder => !IsScanning && SaveLogs && !SaveBesideSource;
    public bool UseCustomFolder => SaveLogs && !SaveBesideSource;
    public double Percent
    {
        get
        {
            var total = Files.Sum(item => (double)item.FileSize);
            if (total == 0)
                return !IsEmpty && Files.All(item => item.State is TsBatchCheckState.Completed or TsBatchCheckState.Failed)
                    ? 100 : 0;
            var processed = Files.Sum(item => item.State == TsBatchCheckState.Failed
                ? (double)item.FileSize : item.BytesScanned);
            return Math.Min(100, processed * 100 / total);
        }
    }
    public string SummaryText => string.Format(LocalizationManager.Instance.String_TsBatchCheck_Summary,
        Files.Count(item => item.State is TsBatchCheckState.Completed or TsBatchCheckState.Failed), Files.Count,
        Files.Count(item => item.State == TsBatchCheckState.Completed && item.IsPass),
        Files.Count(item => item.State == TsBatchCheckState.Completed && (item.IsWarning || item.IsError)),
        Files.Count(item => item.State == TsBatchCheckState.Failed));
    public string SelectedPath => SelectedFile?.FilePath ?? string.Empty;
    public string SelectedDetail => SelectedFile?.DetailText ?? string.Empty;
    public bool HasSelectedFile => SelectedFile is not null;
    public bool HasSelectedDetail => !string.IsNullOrWhiteSpace(SelectedDetail);
    public bool HasOperationMessage => !string.IsNullOrWhiteSpace(OperationMessage);
    public bool ShowDetails => HasSelectedFile || HasOperationMessage;
    public string StatusText
    {
        get
        {
            var strings = LocalizationManager.Instance;
            if (IsScanning)
                return CanStop ? strings.String_TsBatchCheck_Status_Scanning : strings.String_TsBatchCheck_Status_Stopping;
            if (IsAddingFiles) return strings.String_TsBinaryMerge_Status_ReadingFiles;
            if (UseCustomFolder && !Directory.Exists(OutputFolder)) return strings.String_TsBatchCheck_SelectFolder;
            if (Files.Any(item => item.State == TsBatchCheckState.Cancelled)) return strings.String_TsBatchCheck_Status_Stopped;
            if (!IsEmpty && !Files.Any(IsPending)) return strings.String_TsBatchCheck_Status_Completed;
            return strings.String_TsBatchCheck_Status_Ready;
        }
    }

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _isAddingFiles;

    [ObservableProperty]
    private bool _saveLogs;

    [ObservableProperty]
    private bool _onlyIssues;

    [ObservableProperty]
    private bool _saveBesideSource = true;

    [ObservableProperty]
    private string _outputFolder = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOperationMessage), nameof(ShowDetails))]
    private string _operationMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPath), nameof(SelectedDetail), nameof(CanViewResult))]
    [NotifyPropertyChangedFor(nameof(HasSelectedFile), nameof(HasSelectedDetail), nameof(ShowDetails))]
    [NotifyCanExecuteChangedFor(nameof(ViewResultCommand))]
    private TsBatchCheckFileItem? _selectedFile;

    private static bool IsPending(TsBatchCheckFileItem item) =>
        item.State is TsBatchCheckState.Waiting or TsBatchCheckState.Cancelled;

    private static bool HasIssues(TsBatchCheckFileItem item) =>
        item.IsWarning || item.IsError || item.ErrorCount > 0 || item.WarningCount > 0 ||
        !string.IsNullOrEmpty(item.LogError);

    partial void OnOnlyIssuesChanged(bool value) => NotifyState();
    partial void OnIsScanningChanged(bool value) => NotifyState();
    partial void OnIsAddingFilesChanged(bool value) => NotifyState();
    partial void OnSaveLogsChanged(bool value)
    {
        _configuration.CurrentConfig.TsBatchCheckSaveLogs = value;
        _configuration.Save();
        NotifyState();
    }
    partial void OnSaveBesideSourceChanged(bool value) => NotifyState();
    partial void OnOutputFolderChanged(string value) => NotifyState();

    public void SetSelectedFiles(IEnumerable<TsBatchCheckFileItem> items)
    {
        _selectedFiles.Clear();
        _selectedFiles.AddRange(items.Where(VisibleFiles.Contains));
        SelectedFile = _selectedFiles.FirstOrDefault();
        NotifyState();
    }

    [RelayCommand(CanExecute = nameof(CanAddFiles))]
    private async Task AddFilesAsync()
    {
        var selected = await _dialogService.ShowOpenFilesDialogAsync(this, new OpenFileDialogSettings
        {
            Title = LocalizationManager.Instance.String_TsBatchCheck_Title,
            AllowMultiple = true,
            Filters = [new(LocalizationManager.Instance.String_TsFiles, ["ts"])]
        });
        await AddFilesAsync(selected.Select(item => item.LocalPath));
    }

    public async Task AddFilesAsync(IEnumerable<string> paths, bool expandFolders = false)
    {
        var input = paths.ToArray();
        await _addLock.WaitAsync();
        try
        {
            if (_isClosed) return;
            IsAddingFiles = true;
            var existing = Files.Select(item => item.FilePath).ToHashSet(TsFileDropHelper.PathComparer);
            var loaded = await Task.Run(() =>
            {
                var items = new List<TsBatchCheckFileItem>();
                var files = expandFolders ? TsFileDropHelper.ExpandPaths(input) : input;
                foreach (var path in files)
                {
                    try
                    {
                        if (!TsFileDropHelper.HasTsExtension(path)) continue;
                        var info = new FileInfo(path);
                        if (info.Exists && existing.Add(info.FullName))
                            items.Add(new TsBatchCheckFileItem(info.FullName, info.Length));
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (ArgumentException) { }
                }
                return items;
            });
            if (_isClosed) return;
            Files.AddRange(loaded);
            OperationMessage = string.Format(_localization.GetString("String.TsBinaryMerge.Status.FilesAdded"), loaded.Count);
        }
        finally
        {
            IsAddingFiles = false;
            _addLock.Release();
            NotifyState();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void RemoveFiles()
    {
        foreach (var item in _selectedFiles.ToArray()) Files.Remove(item);
        SetSelectedFiles([]);
    }

    [RelayCommand(CanExecute = nameof(CanClear))]
    private void Clear()
    {
        Files.Clear();
        OperationMessage = string.Empty;
        SetSelectedFiles([]);
    }

    [RelayCommand(CanExecute = nameof(CanRecheck))]
    private async Task RecheckSelectedAsync()
    {
        if (!CanRecheck || _isClosed) return;
        // 固定本次选中项；检测过程中改变选择或追加文件不会扩大重新检查的范围。
        var selected = _selectedFiles.ToHashSet();
        foreach (var item in selected) item.Reset();
        await ScanQueueAsync(selected);
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (!CanStart || _isClosed) return;
        await ScanQueueAsync();
    }

    private async Task ScanQueueAsync(IReadOnlySet<TsBatchCheckFileItem>? selected = null)
    {
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        var saveLogs = SaveLogs;
        var folder = SaveBesideSource ? null : OutputFolder;
        OperationMessage = string.Empty;
        IsScanning = true;
        try
        {
            while (!_isClosed && !cancellation.IsCancellationRequested)
            {
                var item = Files.FirstOrDefault(item => IsPending(item) && (selected is null || selected.Contains(item)));
                if (item is null)
                {
                    if (selected is not null || !IsAddingFiles) break;
                    await _addLock.WaitAsync(cancellation.Token);
                    _addLock.Release();
                    continue;
                }
                item.Reset();
                item.State = TsBatchCheckState.Scanning;
                var generation = ++_generation;
                NotifyState();
                var progress = new Progress<TsCheckProgress>(value =>
                {
                    if (_isClosed || generation != _generation) return;
                    var hadIssues = HasIssues(item);
                    item.UpdateProgress(value);
                    if (OnlyIssues && hadIssues != HasIssues(item)) NotifyState();
                    OnPropertyChanged(nameof(Percent));
                    SpeedText = $"{CommonUtil.FormatFileSize(value.BytesPerSecond)}/s";
                });
                try
                {
                    var result = await Task.Run(() => new TsStreamAnalyzer().AnalyzeAsync(
                        item.FilePath, progress, cancellation.Token));
                    // 丢弃已排队的进度回调，防止覆盖最终结果或下一次检测。
                    _generation++;
                    if (_isClosed) break;
                    item.Complete(result);
                    NotifyState();
                    if (saveLogs && !result.WasCancelled)
                    {
                        try
                        {
                            var report = new TsCheckReportBuilder(new TsCheckTextFormatter()).Build(result);
                            item.LogPath = await TsBatchCheckReportWriter.SaveLogAsync(item.FilePath, folder, report);
                        }
                        catch (Exception exception) { item.LogError = exception.Message; }
                        OnPropertyChanged(nameof(SelectedDetail));
                    }
                }
                catch (Exception exception)
                {
                    _generation++;
                    if (_isClosed) break;
                    item.Failure = exception.Message;
                    item.State = cancellation.IsCancellationRequested
                        ? TsBatchCheckState.Cancelled : TsBatchCheckState.Failed;
                    item.RefreshLocalizedText();
                }
                NotifyState();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            _generation++;
            _cancellation = null;
            IsScanning = false;
            SpeedText = "—";
            NotifyState();
        }
    }

    [ObservableProperty]
    private string _speedText = "—";

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        _cancellation?.Cancel();
        NotifyState();
    }

    [RelayCommand(CanExecute = nameof(CanViewResult))]
    private void ViewResult()
    {
        if (SelectedFile?.Result is not { } result) return;
        var viewModel = _dialogService.CreateViewModel<TsCheckWindowViewModel>();
        viewModel.Initialize(result);
        _dialogService.Show(null, viewModel);
    }

    [RelayCommand(CanExecute = nameof(CanBrowseFolder))]
    private async Task BrowseFolderAsync()
    {
        var selected = await _dialogService.ShowOpenFolderDialogAsync(this, new OpenFolderDialogSettings
        {
            Title = LocalizationManager.Instance.String_TsBatchCheck_SelectFolder,
            SuggestedStartLocation = Directory.Exists(OutputFolder) ? new DesktopDialogStorageFolder(OutputFolder) : null
        });
        if (!_isClosed && selected?.Path is not null) OutputFolder = selected.Path.LocalPath;
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportSummaryAsync()
    {
        // 固定点击导出时的快照，选文件期间队列可继续运行。
        var strings = LocalizationManager.Instance;
        var csv = new StringBuilder();
        void AppendRow(params string[] cells) => csv.AppendLine(string.Join(",", cells.Select(TsBatchCheckReportWriter.CsvCell)));
        AppendRow(strings.String_TsCheck_Report_File, strings.String_TsBinaryMerge_Column_Size,
            strings.String_TsBinaryMerge_Column_Status, strings.String_TsBatchCheck_Column_Progress,
            strings.String_TsCheck_Report_Verdict, strings.String_TsCheck_Column_Errors,
            strings.String_TsCheck_Column_Warnings, strings.String_TsCheck_Report_Elapsed,
            strings.String_TsBatchCheck_LogFile, strings.String_TsCheck_Column_Description);
        foreach (var item in Files)
            AppendRow(item.FilePath, item.FileSize.ToString(), item.StatusText,
                item.Percent.ToString("0.00") + "%", item.VerdictText, item.ErrorCount.ToString(),
                item.WarningCount.ToString(), item.ElapsedText, item.LogPath, item.DetailText);
        var selected = await _dialogService.ShowSaveFileDialogAsync(this, new SaveFileDialogSettings
        {
            Title = strings.String_TsBatchCheck_Export,
            SuggestedFileName = "ts-check-summary.csv",
            DefaultExtension = "csv",
            Filters = [new("CSV", ["csv"])]
        });
        if (_isClosed || selected?.Path is null) return;
        try
        {
            var output = Path.GetFullPath(selected.Path.LocalPath);
            if (Files.Any(item => TsFileDropHelper.PathComparer.Equals(item.FilePath, output)))
                throw new IOException(strings.String_TsFilter_Error_SameFile);
            await File.WriteAllTextAsync(output, csv.ToString(), new UTF8Encoding(true));
            OperationMessage = string.Format(strings.String_TsCheck_Status_Exported, selected.Path.LocalPath);
        }
        catch (Exception exception)
        {
            OperationMessage = string.Format(strings.String_TsCheck_Status_Failed, exception.Message);
        }
    }

    private void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(WindowTitle));
        foreach (var item in Files) item.RefreshLocalizedText();
        NotifyState();
    }

    private void NotifyState()
    {
        RefreshVisibleFiles();
        foreach (var name in new[] { nameof(IsEmpty), nameof(HasNoVisibleIssues), nameof(CanModifyFiles), nameof(CanAddFiles), nameof(CanRemove), nameof(CanRecheck),
            nameof(CanClear), nameof(CanStart), nameof(CanStop), nameof(CanViewResult), nameof(CanExport),
            nameof(CanBrowseFolder), nameof(UseCustomFolder), nameof(Percent), nameof(SummaryText), nameof(SelectedDetail),
            nameof(HasSelectedDetail), nameof(StatusText) })
            OnPropertyChanged(name);
        AddFilesCommand.NotifyCanExecuteChanged();
        RemoveFilesCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
        RecheckSelectedCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ViewResultCommand.NotifyCanExecuteChanged();
        BrowseFolderCommand.NotifyCanExecuteChanged();
        ExportSummaryCommand.NotifyCanExecuteChanged();
    }

    private void RefreshVisibleFiles()
    {
        // DataGrid 移除隐藏行时会回调选择事件，避免递归修改集合。
        if (_isRefreshingVisibleFiles) return;
        _isRefreshingVisibleFiles = true;
        try
        {
            var visible = Files.Where(item => !OnlyIssues || HasIssues(item)).ToArray();
            var visibleSet = visible.ToHashSet();
            for (var index = VisibleFiles.Count - 1; index >= 0; index--)
                if (!visibleSet.Contains(VisibleFiles[index])) VisibleFiles.RemoveAt(index);

            if (VisibleFiles.Count == 0)
                VisibleFiles.AddRange(visible);

            // 队列顺序不变，只增删行，保留仍可见文件的选择和滚动位置。
            for (var index = 0; index < visible.Length; index++)
                if (index >= VisibleFiles.Count || VisibleFiles[index] != visible[index])
                    VisibleFiles.Insert(index, visible[index]);

            _selectedFiles.RemoveAll(item => !visibleSet.Contains(item));
            SelectedFile = _selectedFiles.FirstOrDefault();
        }
        finally
        {
            _isRefreshingVisibleFiles = false;
        }
    }

    public void OnClosed()
    {
        if (_isClosed) return;
        _isClosed = true;
        _generation++;
        _cancellation?.Cancel();
        _localization.LanguageChanged -= RefreshLocalizedText;
        Files.Clear();
        SetSelectedFiles([]);
    }
}
