using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.TransportStream.Analysis;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Utils;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.ViewModels.TransportStream.Analysis;

namespace TSCutter.GUI.ViewModels.Editing;

/// <summary>导出记录窗口的行视图；文字随语言变化时整体重建。</summary>
public sealed class ExportRecordView
{
    public ExportRecordView(ExportRecord record, LocalizationManager strings)
    {
        Record = record;
        TimeText = record.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
        KindText = FormatKind(record.Kind, strings);
        FileSizeText = CommonUtil.FormatFileSize(record.FileSize);
        CheckStatusText = BuildCheckStatus(record, strings, out var detail, out var canViewDetails);
        CheckDetailText = detail;
        CanViewDetails = canViewDetails;
    }

    public ExportRecord Record { get; }
    public string TimeText { get; }
    public string KindText { get; }
    public string FileSizeText { get; }
    public string CheckStatusText { get; }
    /// <summary>行内检测状态说明（tooltip）；含失败原因与记录过期提示。</summary>
    public string CheckDetailText { get; }
    /// <summary>登记时完整结果是否可直接打开；点击时还会重新校验文件状态。</summary>
    public bool CanViewDetails { get; }

    public static string FormatKind(ExportKind kind, LocalizationManager strings) => kind switch
    {
        ExportKind.TsCopy => strings.String_Export_Kind_TsCopy,
        ExportKind.TsProgramOnly => strings.String_Export_Kind_TsProgramOnly,
        ExportKind.TsProgramTablesSupplement => strings.String_Export_Kind_TsProgramTablesSupplement,
        ExportKind.TsMerge => strings.String_Export_Kind_TsMerge,
        ExportKind.M2tsRemux => strings.String_Export_Kind_M2tsRemux,
        ExportKind.Mp4Remux => strings.String_Export_Kind_Mp4Remux,
        ExportKind.MkvRemux => strings.String_Export_Kind_MkvRemux,
        _ => strings.String_Export_Kind_VobRemux
    };

    public static string BuildCheckStatus(ExportRecord record, LocalizationManager strings,
        out string detail, out bool canViewDetails)
    {
        canViewDetails = false;
        if (!record.CheckApplicable)
        {
            detail = strings.String_Export_Check_NotApplicable;
            return strings.String_Export_Check_NotApplicable;
        }
        if (!record.CheckEnabled)
        {
            detail = strings.String_Export_Check_Disabled;
            return strings.String_Export_Check_NotRun;
        }
        switch (record.CheckState)
        {
            case TsExportCheckState.NotStarted:
            case TsExportCheckState.Aborted:
                detail = strings.String_Export_Check_NotRunDetail;
                return strings.String_Export_Check_NotRun;
            case TsExportCheckState.Failed:
                detail = record.FailureKind switch
                {
                    TsExportCheckFailure.SeekDetected => strings.String_Export_Check_FailureSeek,
                    _ => record.FailureDetail ?? strings.String_Export_Check_FailureException
                };
                return strings.String_Export_Check_Failed;
            case TsExportCheckState.Running:
                detail = strings.String_Export_Check_NotRunDetail;
                return strings.String_Export_Check_NotRun;
        }

        var summary = record.CheckVerdict switch
        {
            TsCheckVerdict.Pass => strings.String_Export_Check_Pass,
            TsCheckVerdict.Warning => string.Format(strings.String_Export_Check_WarningCount, record.WarningCount),
            null or TsCheckVerdict.Incomplete => strings.String_Export_Check_NotRun,
            _ => string.Format(strings.String_Export_Check_ErrorCount, record.ErrorCount, record.WarningCount)
        };
        detail = summary;
        if (!ExportRecordRegistry.IsOutputUnchanged(record))
            detail += "\n" + strings.String_Export_Records_FileChanged;
        else if (!record.HasRetainedResult)
            detail += "\n" + strings.String_Export_Check_DetailsReleased;
        else
            canViewDetails = record.CheckVerdict is not (null or TsCheckVerdict.Incomplete);
        return summary;
    }
}

public partial class ExportRecordsWindowViewModel : ViewModelBase, IModalDialogViewModel
{
    private readonly IDialogService _dialogService;
    private readonly ExportRecordRegistry? _registry;
    private bool _isOpen;

    public bool? DialogResult { get; }
    public event Action? RequestClose;

    public ObservableCollection<ExportRecordView> Records { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText), nameof(HasSelection), nameof(CanViewDetails))]
    private ExportRecordView? _selectedRecord;

    public string SummaryText => SelectedRecord is { } view ? BuildSummaryText(view.Record) : "";
    public bool HasSelection => SelectedRecord is not null;
    public bool CanViewDetails => SelectedRecord?.CanViewDetails == true;

    [ObservableProperty]
    private string _statusText = "";

    public bool HasRecords => Records.Count > 0;

    public ExportRecordsWindowViewModel(IDialogService dialogService)
    {
        _dialogService = dialogService;
        _registry = App.ExportRecordRegistry;
        RefreshRecords();
    }

    public void OnOpened()
    {
        if (_isOpen)
            return;
        _isOpen = true;
        if (_registry is not null)
            _registry.Changed += OnRegistryChanged;
        App.LocalizationService.LanguageChanged += OnLanguageChanged;
        RefreshRecords();
    }

    private void OnRegistryChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (_isOpen)
            RefreshRecords();
    });

    private void OnLanguageChanged()
    {
        RefreshRecords();
    }

    private void RefreshRecords()
    {
        var selectedId = SelectedRecord?.Record.Id;
        SelectedRecord = null;
        StatusText = "";
        Records.Clear();
        var strings = LocalizationManager.Instance;
        if (_registry is not { } registry)
            return;
        foreach (var record in registry.Records)
            Records.Add(new ExportRecordView(record, strings));
        OnPropertyChanged(nameof(HasRecords));
        if (selectedId is { } id)
            SelectRecord(id);
        SelectedRecord ??= Records.FirstOrDefault();
    }

    public void SelectRecord(long recordId)
    {
        foreach (var view in Records)
        {
            if (view.Record.Id == recordId)
            {
                SelectedRecord = view;
                return;
            }
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var record = SelectedRecord?.Record;
        if (record is null)
            return;
        CommonUtil.OpenFileLocation(record.OutputPath);
    }

    [RelayCommand]
    private void ViewDetails()
    {
        var record = SelectedRecord?.Record;
        if (record is null)
            return;
        RefreshRecords();
        // 打开基于文件位置的详情前，检查输出是否还存在、是否已被覆盖；
        // 过期记录只给出说明，不冒充当前文件的检测结论。
        if (!record.CheckApplicable || !record.CheckEnabled ||
            record.CheckState != TsExportCheckState.Completed || !record.HasRetainedResult)
        {
            StatusText = record.CheckApplicable && record.CheckEnabled
                ? LocalizationManager.Instance.String_Export_Check_DetailsReleased
                : LocalizationManager.Instance.String_Export_Check_NotRunDetail;
            return;
        }
        if (!ExportRecordRegistry.IsOutputUnchanged(record))
        {
            StatusText = LocalizationManager.Instance.String_Export_Records_FileChanged;
            return;
        }
        var viewModel = _dialogService.CreateViewModel<TsCheckWindowViewModel>();
        viewModel.Initialize(record.RetainedResult!);
        _dialogService.Show(this, viewModel);
    }

    [RelayCommand]
    private async Task CopySummaryAsync()
    {
        var view = SelectedRecord;
        if (view is null)
            return;
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow?.Clipboard is not { } clipboard)
            return;
        await clipboard.SetTextAsync(BuildSummaryText(view.Record));
        StatusText = LocalizationManager.Instance.String_Export_Records_Copied;
    }

    public static string BuildSummaryText(ExportRecord record)
    {
        var strings = LocalizationManager.Instance;
        var builder = new StringBuilder(512);
        builder.AppendLine($"{strings.String_Export_Records_Column_File}: {record.FileName}");
        builder.AppendLine($"{strings.String_Export_Records_Column_Path}: {record.OutputPath}");
        builder.AppendLine($"{strings.String_Export_Records_Column_Kind}: {ExportRecordView.FormatKind(record.Kind, strings)}");
        builder.AppendLine($"{strings.String_Export_Records_Column_Size}: {CommonUtil.FormatFileSize(record.FileSize)}");
        builder.AppendLine($"{strings.String_Export_Records_Column_Elapsed}: {record.Elapsed:hh\\:mm\\:ss\\.fff}");
        builder.AppendLine($"{strings.String_Export_Records_Column_Clips}: {record.ClipCount}");
        if (record.ClipRangeSummary.Length > 0)
            builder.AppendLine(record.ClipRangeSummary);
        var status = ExportRecordView.BuildCheckStatus(record, strings, out var detail, out _);
        builder.AppendLine($"{strings.String_Export_Check_Title}: {status}");
        if (detail != status)
        {
            foreach (var line in detail.Split('\n'))
            {
                if (line != status)
                    builder.AppendLine(line);
            }
        }
        if (record.CheckEnabled && record.CheckApplicable &&
            record.CheckState == TsExportCheckState.Completed && record.CheckVerdict is not (null or TsCheckVerdict.Incomplete))
        {
            var counts = string.Format(strings.String_Export_Check_ErrorCount, record.ErrorCount, record.WarningCount);
            if (counts != status)
                builder.AppendLine(counts);
            if (record.CheckCategories.Count > 0)
            {
                builder.AppendLine(strings.String_Export_Check_Categories);
                foreach (var category in record.CheckCategories
                             .GroupBy(item => FormatCheckCategory(item.Type, strings))
                             .Select(group => (Name: group.Key, Count: group.Sum(item => item.Count)))
                             .OrderByDescending(item => item.Count))
                    builder.AppendLine(string.Format(strings.String_Export_Check_CategoryFormat, category.Name, category.Count));
            }
        }
        builder.AppendLine($"{strings.String_Export_Records_Time}: {record.Timestamp:yyyy-MM-dd HH:mm:ss}");
        return builder.ToString().TrimEnd();
    }

    private static string FormatCheckCategory(TsCheckEventType type, LocalizationManager strings) => type switch
    {
        TsCheckEventType.SyncLoss or TsCheckEventType.InvalidPacketHeader => strings.String_Export_Check_Category_Packet,
        TsCheckEventType.TransportError => strings.String_Export_Check_Category_Transport,
        TsCheckEventType.ContinuityGap => strings.String_Export_Check_Category_Continuity,
        TsCheckEventType.DuplicatePacket or TsCheckEventType.ConflictingDuplicate => strings.String_Export_Check_Category_Duplicate,
        TsCheckEventType.PesSizeMismatch => strings.String_Export_Check_Category_Length,
        TsCheckEventType.PsiCrcError or TsCheckEventType.PsiMalformed or TsCheckEventType.MissingProgramTable => strings.String_Export_Check_Category_Program,
        TsCheckEventType.PcrBackward or TsCheckEventType.PcrJump or TsCheckEventType.PcrGap or TsCheckEventType.MissingPcr => strings.String_Export_Check_Category_Clock,
        TsCheckEventType.PtsBackward or TsCheckEventType.PtsJump or TsCheckEventType.DtsBackward or
            TsCheckEventType.DtsAfterPts or TsCheckEventType.MissingTimestamp or TsCheckEventType.StreamGap => strings.String_Export_Check_Category_Timing,
        TsCheckEventType.AvSyncDrift => strings.String_Export_Check_Category_AvSync,
        TsCheckEventType.TrailingBytes => strings.String_Export_Check_Category_Tail,
        _ => strings.String_Export_Check_Category_Other
    };

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();

    public void OnClosed()
    {
        if (!_isOpen)
            return;
        _isOpen = false;
        if (_registry is not null)
            _registry.Changed -= OnRegistryChanged;
        App.LocalizationService.LanguageChanged -= OnLanguageChanged;
    }
}
