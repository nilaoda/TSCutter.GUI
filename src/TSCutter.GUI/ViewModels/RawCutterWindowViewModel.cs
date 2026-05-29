using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Classic.CommonControls.Dialogs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.FileSystem;
using HanumanInstitute.MvvmDialogs.FrameworkDialogs;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.ViewModels;

public partial class RawCutterWindowViewModel : ViewModelBase, IModalDialogViewModel
{
    [ObservableProperty]
    public partial bool? DialogResult { get; set; }

    private readonly IDialogService _dialogService;
    private bool _isOutputting; // 防止输出命令重入
    private CancellationTokenSource? _statusCts; // 状态消息自动消失计时
    private bool _syncingSlider; // 防止滑块与输入框循环同步

    public RawCutterWindowViewModel(IDialogService dialogService)
    {
        _dialogService = dialogService;
    }

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncInfo))]
    private long _syncOffset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PacketInfoStr))]
    private long _totalPackets;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileSizeStr))]
    private long _fileSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOffsetMode))]
    private bool _isPacketMode = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeInfoStr))]
    private long _startPacket;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeInfoStr))]
    private long _endPacket;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeInfoStr), nameof(SliderStart))]
    private long _startOffset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeInfoStr), nameof(SliderEnd))]
    private long _endOffset;

    /// <summary>
    /// 输出状态消息，为空时不显示。设置后 2 秒自动清空。
    /// </summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public bool IsOffsetMode => !IsPacketMode;

    public string SyncInfo => $"Sync: {SyncOffset:N0} Byte";

    public string FileSizeStr => CommonUtil.FormatFileSize(FileSize);

    public string PacketInfoStr => TotalPackets.ToString("N0");

    public string RangeInfoStr => $"{StartOffset:N0} - {EndOffset:N0} ({CommonUtil.FormatFileSize(Math.Max(0, EndOffset - StartOffset + 1))})";

    /// <summary>
    /// Packet 模式 NumericUpDown 上限。
    /// </summary>
    public double PacketMax => Math.Max(0, TotalPackets - 1);

    /// <summary>
    /// 偏移模式 NumericUpDown 上限。
    /// </summary>
    public double OffsetMax => Math.Max(SyncOffset, FileSize - 1);

    /// <summary>
    /// 滑块范围最小值（始终为 SyncOffset）。
    /// </summary>
    public double SliderMin => SyncOffset;

    /// <summary>
    /// 滑块范围最大值（始终为 FileSize - 1）。
    /// </summary>
    public double SliderMax => Math.Max(0, FileSize - 1);

    /// <summary>
    /// 起始滑块值，双向同步 StartOffset。
    /// </summary>
    public double SliderStart
    {
        get => StartOffset;
        set
        {
            if (_syncingSlider) return;
            var v = (long)Math.Round(value);
            v = Math.Clamp(v, SyncOffset, Math.Max(SyncOffset, FileSize - 1));
            if (v > SliderEnd) v = (long)SliderEnd;
            _syncingSlider = true;
            StartOffset = v;
            StartPacket = Math.Max(0, (v - SyncOffset) / TsUtil.TsPacketSize);
            _syncingSlider = false;
        }
    }

    /// <summary>
    /// 结束滑块值，双向同步 EndOffset。
    /// </summary>
    public double SliderEnd
    {
        get => EndOffset;
        set
        {
            if (_syncingSlider) return;
            var v = (long)Math.Round(value);
            v = Math.Clamp(v, SyncOffset, Math.Max(SyncOffset, FileSize - 1));
            if (v < SliderStart) v = (long)SliderStart;
            _syncingSlider = true;
            EndOffset = v;
            EndPacket = Math.Min((v - SyncOffset) / TsUtil.TsPacketSize, Math.Max(0, TotalPackets - 1));
            _syncingSlider = false;
        }
    }

    public string StartPacketStr
    {
        get => StartPacket.ToString();
        set
        {
            if (long.TryParse(value, out var v) && v >= 0 && v < TotalPackets)
                StartPacket = v;
        }
    }

    public string EndPacketStr
    {
        get => EndPacket.ToString();
        set
        {
            if (long.TryParse(value, out var v) && v >= 0 && v < TotalPackets)
                EndPacket = v;
        }
    }

    public string StartOffsetStr
    {
        get => StartOffset.ToString();
        set
        {
            if (long.TryParse(value, out var v) && v >= 0 && v < FileSize)
                StartOffset = v;
        }
    }

    public string EndOffsetStr
    {
        get => EndOffset.ToString();
        set
        {
            if (long.TryParse(value, out var v) && v >= 0 && v < FileSize)
                EndOffset = v;
        }
    }

    /// <summary>
    /// Initializes the view model with the given TS file path.
    /// Must be called before displaying the window.
    /// </summary>
    public void Initialize(string filePath)
    {
        FilePath = filePath;
        var fileInfo = new FileInfo(filePath);
        FileSize = fileInfo.Length;

        SyncOffset = TsUtil.FindSyncOffset(filePath);
        TotalPackets = TsUtil.CountPackets(filePath, SyncOffset);

        EndPacket = Math.Max(0, TotalPackets - 1);
        StartOffset = TsUtil.PacketToOffset(StartPacket, SyncOffset);
        EndOffset = FileSize - 1;
    }

    partial void OnStartPacketChanged(long value)
    {
        if (value < 0 || _syncingSlider) return;
        _syncingSlider = true;
        StartOffset = TsUtil.PacketToOffset(value, SyncOffset);
        _syncingSlider = false;
    }

    partial void OnEndPacketChanged(long value)
    {
        if (value < 0 || _syncingSlider) return;
        _syncingSlider = true;
        EndOffset = Math.Min(TsUtil.PacketToOffset(value + 1, SyncOffset) - 1, FileSize - 1);
        _syncingSlider = false;
    }

    partial void OnIsPacketModeChanged(bool value)
    {
        // 切换模式时强制刷新
        OnPropertyChanged(nameof(RangeInfoStr));
        OnPropertyChanged(nameof(SliderStart));
        OnPropertyChanged(nameof(SliderEnd));
        OnPropertyChanged(nameof(PacketMax));
        OnPropertyChanged(nameof(OffsetMax));
    }

    [RelayCommand]
    private async Task OutputClickAsync()
    {
        // 防止重入
        if (_isOutputting) return;
        _isOutputting = true;
        try
        {
            await OutputClickCoreAsync();
        }
        finally
        {
            _isOutputting = false;
        }
    }

    /// <summary>
    /// 输出核心逻辑。始终以调用时刻的 IsPacketMode 为准决定切割方式。
    /// </summary>
    private async Task OutputClickCoreAsync()
    {
        long startPos, endPos;
        string defaultNameSuffix;

        if (IsPacketMode)
        {
            if (StartPacket < 0 || EndPacket >= TotalPackets || StartPacket > EndPacket)
            {
                await ShowMessageAsync(
                    LocalizationManager.Instance.String_RawCutter_InvalidRange,
                    "TSCutter", MessageBoxIcon.Warning);
                return;
            }
            startPos = TsUtil.PacketToOffset(StartPacket, SyncOffset);
            endPos = Math.Min(TsUtil.PacketToOffset(EndPacket + 1, SyncOffset) - 1, FileSize - 1);
            defaultNameSuffix = $"{StartPacket}-{EndPacket}";
        }
        else
        {
            startPos = Math.Max(0, StartOffset);
            endPos = Math.Min(Math.Max(startPos, EndOffset), FileSize - 1);
            defaultNameSuffix = $"{startPos}-{endPos}";
        }

        if (startPos >= endPos || endPos >= FileSize)
        {
            await ShowMessageAsync(
                LocalizationManager.Instance.String_RawCutter_InvalidRange,
                "TSCutter", MessageBoxIcon.Warning);
            return;
        }

        var defaultName = Path.GetFileNameWithoutExtension(FilePath) + $"_{defaultNameSuffix}.ts";
        var settings = new SaveFileDialogSettings
        {
            Title = LocalizationManager.Instance.String_RawCutter_Title,
            SuggestedStartLocation = new DesktopDialogStorageFolder(Path.GetDirectoryName(FilePath)!),
            SuggestedFileName = defaultName,
            Filters = [new(LocalizationManager.Instance.String_TsFiles, ["ts"])],
            DefaultExtension = "ts"
        };

        var result = await _dialogService.ShowSaveFileDialogAsync(this, settings);
        if (result is null) return;

        var outputVm = _dialogService.CreateViewModel<OutputWindowViewModel>();
        outputVm.SourceFilePath = FilePath;
        outputVm.StartPosition = startPos;
        outputVm.EndPosition = endPos;
        outputVm.OutputPath = result.Path!.LocalPath;
        await _dialogService.ShowDialogAsync(this, outputVm);

        // 仅当正常完成时在窗口内临时显示成功提示
        if (outputVm.DialogResult == true && outputVm.Exception == null)
        {
            ShowStatusMessage(LocalizationManager.Instance.String_RawCutter_OutputSuccess + " " + outputVm.OutputPath);
        }
        else if (outputVm.Exception != null)
        {
            ShowStatusMessage(LocalizationManager.Instance.String_Error + ": " + outputVm.Exception.Message);
        }
    }

    public void OnClosing(CancelEventArgs e)
    {
        _statusCts?.Cancel();
    }

    public event Action? RequestClose;

    /// <summary>
    /// 在窗口底部临时显示状态消息，2 秒后自动消失。
    /// </summary>
    private async void ShowStatusMessage(string message)
    {
        _statusCts?.Cancel();
        _statusCts = new CancellationTokenSource();
        var token = _statusCts.Token;

        StatusMessage = message;
        try
        {
            await Task.Delay(2000, token);
            StatusMessage = string.Empty;
        }
        catch (TaskCanceledException) { }
    }

    private async Task ShowMessageAsync(string message, string title, MessageBoxIcon icon)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopApp)
        {
            await MessageBox.ShowDialog(desktopApp.MainWindow!, message, title, MessageBoxButtons.Ok, icon);
        }
    }
}