using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Utils;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.ViewModels.TransportStream.Analysis;

namespace TSCutter.GUI.ViewModels.Editing;

/// <summary>结果窗口显示的一次单个导出：成功时附导出记录，失败时附错误说明。</summary>
public sealed class ExportResultInfo
{
    public required string OutputPath { get; init; }
    public bool Success { get; init; }
    /// <summary>成功导出登记的记录；未登记时为 null，大小按文件读取，不显示检测结论。</summary>
    public ExportRecord? Record { get; init; }
    /// <summary>导出类型；有记录时以记录为准。</summary>
    public ExportKind? Kind { get; init; }
    /// <summary>导出类型之外的改动与音频延迟；有记录时以记录为准。</summary>
    public ExportModifiers Modifiers { get; init; }
    public int AudioDelayMs { get; init; }
    public TimeSpan Elapsed { get; init; }
    /// <summary>导出类型之外的标签（如删除模式）。</summary>
    public IReadOnlyList<string> ExtraTags { get; init; } = [];
    /// <summary>输出内容概述：片段数、时长，删除模式为删除段数与保留时长。</summary>
    public string ContentText { get; init; } = "";
    /// <summary>智能剪辑重新编码的汇总；其他导出方式为 null。</summary>
    public string? ReencodeText { get; init; }
    public string? ErrorText { get; init; }
}

/// <summary>
/// 单个导出结束后的结果窗口：成功时显示文件、位置、大小、耗时、内容和 TS 检测结论，并可打开文件或所在文件夹；
/// 失败时显示错误说明与已用时间。数据来自导出时登记的记录，不再读取输出内容。
/// </summary>
public partial class ExportResultWindowViewModel : ViewModelBase, IModalDialogViewModel
{
    private readonly IDialogService _dialogService;
    private ExportResultInfo _info = new() { OutputPath = "" };

    public ExportResultWindowViewModel(IDialogService dialogService) => _dialogService = dialogService;

    public bool? DialogResult { get; private set; }
    public event Action? RequestClose;

    public string Title { get; private set; } = "";
    public bool IsSuccess { get; private set; }
    public bool IsFailure => !IsSuccess;
    public string FileName { get; private set; } = "";
    public IReadOnlyList<string> Tags { get; private set; } = [];
    public bool HasTags => Tags.Count > 0;
    public string OutputPath => _info.OutputPath;
    /// <summary>
    /// 所在文件夹（文件名已在标题中）。路径没有空格，在分隔符后插入零宽空格作为换行点，长路径按目录逐段换行；
    /// 只用于显示，悬停提示和复制摘要使用原始路径。
    /// </summary>
    public string LocationText => (Path.GetDirectoryName(OutputPath) ?? OutputPath)
        .Replace("\\", "\\\u200B").Replace("/", "/\u200B");
    public bool HasOutputFile { get; private set; }
    public string SizeText { get; private set; } = "";
    public string ElapsedText { get; private set; } = "";
    public string ContentText => _info.ContentText;
    public bool HasContent => IsSuccess && ContentText.Length > 0;
    public string RangesText { get; private set; } = "";
    public bool HasRanges => IsSuccess && RangesText.Length > 0;
    public string ReencodeText => _info.ReencodeText ?? "";
    public bool HasReencode => IsSuccess && _info.ReencodeText is not null;
    public string CheckStatusText { get; private set; } = "";
    public string CheckDetailText { get; private set; } = "";
    public bool HasCheck { get; private set; }
    public bool CanViewCheckDetails { get; private set; }
    public string ErrorText => _info.ErrorText ?? "";
    /// <summary>失败后输出位置没有文件；有文件时（例如保留了导出前的旧文件）显示其位置。</summary>
    public bool ShowsNoFile => IsFailure && !HasOutputFile;
    public bool ShowsLocation => IsSuccess || HasOutputFile;

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial bool DontShowAgain { get; set; }

    internal void Initialize(ExportResultInfo info)
    {
        _info = info;
        var strings = LocalizationManager.Instance;
        IsSuccess = info.Success;
        Title = IsSuccess ? strings.String_ExportResult_TitleSuccess : strings.String_ExportResult_TitleFailure;
        FileName = Path.GetFileName(info.OutputPath);
        HasOutputFile = File.Exists(info.OutputPath);
        var record = info.Record;
        Tags = (IsSuccess && (record?.Kind ?? info.Kind) is { } kind
                ? ExportRecordView.FormatTags(kind, record?.Modifiers ?? info.Modifiers,
                    record?.AudioDelayMs ?? info.AudioDelayMs, strings) : [])
            .Concat(info.ExtraTags).ToArray();
        var size = record?.FileSize ?? (HasOutputFile ? new FileInfo(info.OutputPath).Length : 0);
        SizeText = CommonUtil.FormatFileSize(size);
        ElapsedText = FormatElapsed(info.Elapsed);
        if (IsSuccess && size > 0 && info.Elapsed.TotalSeconds > 0)
            ElapsedText += " · " + string.Format(strings.String_ExportResult_AverageSpeed,
                CommonUtil.FormatFileSize(size / info.Elapsed.TotalSeconds));
        RangesText = record?.ClipRangeSummary ?? "";
        HasCheck = IsSuccess && record is not null;
        if (record is not null)
        {
            CheckStatusText = ExportRecordView.BuildCheckStatus(record, strings, out var detail, out var canView);
            CheckDetailText = detail;
            CanViewCheckDetails = canView;
        }
    }

    /// <summary>耗时精确到 0.1 秒，格式与导出记录一致（时:分:秒）。</summary>
    internal static string FormatElapsed(TimeSpan elapsed) =>
        $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}.{elapsed.Milliseconds / 100}";

    [RelayCommand]
    private void OpenFile()
    {
        if (!File.Exists(OutputPath))
        {
            StatusText = LocalizationManager.Instance.String_Export_Records_FileChanged;
            return;
        }
        try
        {
            CommonUtil.OpenFile(OutputPath);
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
        }
    }

    [RelayCommand]
    private void OpenFolder() => CommonUtil.OpenFileLocation(OutputPath);

    [RelayCommand]
    private void ViewCheckDetails()
    {
        // 与导出记录窗口相同：只有完整结果仍保留且输出文件未被更改时才打开检测详情。
        if (_info.Record is not { RetainedResult: { } result } record || !CanViewCheckDetails)
            return;
        if (!ExportRecordRegistry.IsOutputUnchanged(record))
        {
            StatusText = LocalizationManager.Instance.String_Export_Records_FileChanged;
            return;
        }
        var viewModel = _dialogService.CreateViewModel<TsCheckWindowViewModel>();
        viewModel.Initialize(result);
        _dialogService.Show(this, viewModel);
    }

    [RelayCommand]
    private async Task CopySummaryAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow?.Clipboard is not { } clipboard)
            return;
        await clipboard.SetTextAsync(BuildSummaryText());
        StatusText = LocalizationManager.Instance.String_Export_Records_Copied;
    }

    /// <summary>成功时与导出记录的摘要相同；失败时为文件、路径、错误与已用时间。</summary>
    internal string BuildSummaryText()
    {
        if (IsSuccess && _info.Record is { } record)
            return ExportRecordsWindowViewModel.BuildSummaryText(record);
        var strings = LocalizationManager.Instance;
        var builder = new StringBuilder();
        builder.AppendLine($"{strings.String_Export_Records_Column_File}: {FileName}");
        builder.AppendLine($"{strings.String_Export_Records_Column_Path}: {OutputPath}");
        if (IsSuccess)
            builder.AppendLine($"{strings.String_Export_Records_Column_Size}: {SizeText}");
        builder.AppendLine($"{strings.String_Export_Records_Column_Elapsed}: {FormatElapsed(_info.Elapsed)}");
        if (ErrorText.Length > 0)
            builder.AppendLine(ErrorText);
        return builder.ToString().TrimEnd();
    }

    [RelayCommand]
    private void Close()
    {
        DialogResult = true;
        RequestClose?.Invoke();
    }
}
