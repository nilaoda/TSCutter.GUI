using System;
using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using TSCutter.GUI.Models.Preview;
using System.Threading.Tasks;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Utils.Application;

namespace TSCutter.GUI.ViewModels.Editing;

public partial class MainWindowViewModel
{
    private bool toneMappingRefreshPending;
    private bool toneMappingRefreshRunning;
    private Task toneMappingRefreshTask = Task.CompletedTask;

    private bool enableSdrToneMapping;
    private bool preferHardwareDecodingForPreview;
    private VideoDynamicRange displayedVideoDynamicRange;

    public bool HasHdrPreview => displayedVideoDynamicRange is
        VideoDynamicRange.Hdr or VideoDynamicRange.Hlg or VideoDynamicRange.DolbyVision;

    private void SetDisplayedVideoDynamicRange(VideoDynamicRange value)
    {
        if (displayedVideoDynamicRange == value) return;
        displayedVideoDynamicRange = value;
        OnPropertyChanged(nameof(HasHdrPreview));
        OnPropertyChanged(nameof(CanToggleSdrToneMapping));
        ToggleSdrToneMappingCommand.NotifyCanExecuteChanged();
    }

    // 状态栏只修改当前文件的临时状态，不写入配置。
    public bool EnableSdrToneMapping
    {
        get => enableSdrToneMapping;
        set
        {
            if (!SetProperty(ref enableSdrToneMapping, value)) return;
            OnPropertyChanged(nameof(ToneMappingStatusText));
            toneMappingRefreshPending = true;
            StartToneMappingRefresh();
        }
    }

    public bool CanTogglePreviewOptions => IsVideoInitialized && !IsDecoding && !_isLoadingVideo && !_isResolvingClipEnd;
    public bool CanToggleHardwareDecoding => CanTogglePreviewOptions && AppConfig.IsHardwareDecodingSupported
        && _videoInstance is { IsHardwareDecodingAvailable: true };
    public bool CanToggleSdrToneMapping => HasHdrPreview && CanTogglePreviewOptions;

    public string ToneMappingStatusText => EnableSdrToneMapping
        ? LocalizationManager.Instance.String_Status_SdrToneMappingOn
        : LocalizationManager.Instance.String_Status_SdrToneMappingOff;

    [RelayCommand(CanExecute = nameof(CanToggleSdrToneMapping))]
    private void ToggleSdrToneMapping() => EnableSdrToneMapping = !EnableSdrToneMapping;

    private void ResetPreviewDefaults()
    {
        preferHardwareDecodingForPreview = _configService.CurrentConfig.PreferHardwareDecoding;
        EnableSdrToneMapping = _configService.CurrentConfig.EnableSdrToneMapping;
        toneMappingRefreshPending = false;
    }

    [RelayCommand(CanExecute = nameof(CanToggleHardwareDecoding))]
    private async Task ToggleHardwareDecodingAsync()
    {
        var previous = _videoInstance!;
        var targetHardware = !IsHardwareDecoding;
        var stopwatch = Stopwatch.StartNew();
        VideoInstance? candidate = null;
        DecodeResult? result = null;
        _isLoadingVideo = true;
        DecodingOpCount++;
        try
        {
            CancelClipEndScan();
            await CompleteScrubPreviewAsync();
            await toneMappingRefreshTask;
            var targetPts = _displayedFramePts;
            candidate = new VideoInstance(VideoPath, targetHardware, _activePreviewSelection,
                () => EnableSdrToneMapping, retainPreviewFrame: true, probeHardwareDecoding: true);
            await candidate.InitVideoAsync();
            result = await candidate.DecodePreviewFrameAtPtsAsync(targetPts);
            if (result is null || result.FramePts != targetPts)
                throw new InvalidOperationException(LocalizationManager.Instance.String_Status_DecoderSwitchFailed);
            if (!ReferenceEquals(previous, _videoInstance)) return;
            _videoInstance = candidate;
            candidate = null;
            preferHardwareDecodingForPreview = targetHardware;
            DecodeCost = stopwatch.ElapsedMilliseconds;
            ApplyDecodeResult(result);
            result = null;
            previous.Dispose();
            UpdateDecodeMode();
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Decoder switch failed: {exception}");
            if (ReferenceEquals(previous, _videoInstance))
            {
                if (targetHardware && candidate is { Inited: true, IsHardwareDecodingAvailable: false })
                    previous.MarkHardwareDecodingUnavailable();
                await ShowMessageAsync(exception.Message);
            }
        }
        finally
        {
            if (result is not null) DisposePreviewResult(result);
            candidate?.Dispose();
            _isLoadingVideo = false;
            DecodingOpCount = Math.Max(0, DecodingOpCount - 1);
            OnPropertyChanged(nameof(IsHardwareDecoding));
            NotifyVideoCapabilityChanged();
        }
    }

    private static void DisposePreviewResult(DecodeResult result)
    {
        result.BitmapLease?.Dispose();
        if (result.BitmapLease is null) result.Bitmap?.Dispose();
        result.GpuFrame?.Dispose();
    }

    private void StartToneMappingRefresh()
    {
        if (!toneMappingRefreshPending || toneMappingRefreshRunning || IsDecoding || _isLoadingVideo || !IsVideoInitialized)
            return;
        toneMappingRefreshTask = RefreshToneMappingAsync();
    }

    private async Task RefreshToneMappingAsync()
    {
        toneMappingRefreshRunning = true;
        var instance = _videoInstance!;
        try
        {
            while (toneMappingRefreshPending && IsVideoInitialized && !IsDecoding && !_isLoadingVideo)
            {
                toneMappingRefreshPending = false;
                instance = _videoInstance!;
                var expectedPts = _displayedFramePts;
                await RunDecodeOperationAsync(async () =>
                {
                    var stopwatch = Stopwatch.StartNew();
                    var result = await instance.RefreshCurrentFrameAsync(expectedPts: expectedPts);
                    if (result is null) return;
                    // 刷新期间若已有新画面发布，旧映射结果不能覆盖新的拖动位置。
                    if (!ReferenceEquals(instance, _videoInstance) || _displayedFramePts != expectedPts)
                    {
                        DisposePreviewResult(result);
                        return;
                    }
                    DecodeCost = stopwatch.ElapsedMilliseconds;
                    ApplyDecodeResult(result);
                });
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine($"SDR preview refresh failed: {exception}");
            // 关闭或换源后不再为旧预览弹出错误对话框。
            if (ReferenceEquals(instance, _videoInstance))
                await ShowMessageAsync(exception.Message);
        }
        finally { toneMappingRefreshRunning = false; }
    }
}
