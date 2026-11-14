using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Services.Media;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.ViewModels.Editing;

public partial class RemuxExportTrackChoice : ObservableObject
{
    public int Index { get; init; }
    public string Description { get; init; } = "";
    public bool CanChange { get; init; } = true;
    public bool IsTextSubtitle { get; init; }
    /// <summary>分组标题（TS 多节目时的节目名），只在每组第一条轨道上；没有分组时为空。</summary>
    public string GroupHeader { get; init; } = "";
    public bool HasGroupHeader => GroupHeader.Length > 0;
    /// <summary>音轨在源文件中的流序号，静音检测用；不是音轨时为 -1。</summary>
    public int AudioStreamIndex { get; init; } = -1;
    [ObservableProperty] private bool _isSelected;
    /// <summary>静音检测的结果（峰值或“静音”），检测前为空。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLevel))]
    private string _levelText = "";
    [ObservableProperty] private bool _isSilent;
    public bool HasLevel => LevelText.Length > 0;
}

public partial class RemuxExportOptionsViewModel : ViewModelBase, IModalDialogViewModel
{
    public IReadOnlyList<string> Containers { get; private set; } = ["mp4", "mkv"];
    public bool IsVobOutput => Container == "vob";
    /// <summary>TS 原包导出：封装与源文件相同，只能选择轨道（<see cref="RemuxExportTrackChoice.Index"/> 为 PID）。</summary>
    public bool IsTsOutput { get; private set; }
    public bool IsRemuxOutput => !IsTsOutput;
    public string TsContainerText { get; private set; } = "";
    /// <summary>当前为智能剪辑：只输出预览视频所在节目的音视频和字幕。</summary>
    public bool ShowsFrameAccurateHint { get; private set; }
    partial void OnContainerChanged(string value) => OnPropertyChanged(nameof(IsVobOutput));
    [ObservableProperty] private string _container = "mp4";
    [ObservableProperty] private bool _fastSubtitleSeek = true;
    public List<RemuxExportTrackChoice> Tracks { get; } = [];
    private bool _isMatroskaSource;
    public bool CanUseFastSubtitleSeek => _isMatroskaSource && Tracks.Any(track => track.IsTextSubtitle && track.IsSelected);

    /// <summary>静音检测：由调用方提供，只在用户点击时运行（抽样解码各音轨，读取量与片长无关）。</summary>
    private Func<IReadOnlyCollection<int>, IProgress<double>, CancellationToken, Task<IReadOnlyList<AudioTrackLevel>>>? _analyzeAudio;
    private CancellationTokenSource? _detection;
    public bool CanDetectSilence => _analyzeAudio is not null && Tracks.Any(track => track.AudioStreamIndex >= 0);
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DetectSilenceCommand))]
    private bool _isDetectingSilence;
    [ObservableProperty] private string _silenceStatus = "";

    internal void EnableSilenceDetection(
        Func<IReadOnlyCollection<int>, IProgress<double>, CancellationToken, Task<IReadOnlyList<AudioTrackLevel>>> analyze)
    {
        _analyzeAudio = analyze;
        OnPropertyChanged(nameof(CanDetectSilence));
    }

    private bool CanStartSilenceDetection() => !IsDetectingSilence;

    /// <summary>检测各音轨是否静音：结果显示在轨道下方，静音的音轨取消勾选（用户可以再勾上）。</summary>
    [RelayCommand(CanExecute = nameof(CanStartSilenceDetection))]
    private async Task DetectSilenceAsync()
    {
        if (_analyzeAudio is null)
            return;
        var strings = LocalizationManager.Instance;
        var audio = Tracks.Where(track => track.AudioStreamIndex >= 0).ToList();
        using var detection = _detection = new CancellationTokenSource();
        IsDetectingSilence = true;
        SilenceStatus = string.Format(CultureInfo.CurrentCulture, strings.String_SilenceDetect_Running, 0);
        var progress = new Progress<double>(value =>
        {
            if (IsDetectingSilence)
                SilenceStatus = string.Format(CultureInfo.CurrentCulture, strings.String_SilenceDetect_Running,
                    (int)Math.Floor(value * 100));
        });
        try
        {
            var levels = (await _analyzeAudio(audio.Select(track => track.AudioStreamIndex).ToList(), progress, detection.Token))
                .ToDictionary(level => level.StreamIndex);
            var deselected = 0;
            foreach (var track in audio)
            {
                if (!levels.TryGetValue(track.AudioStreamIndex, out var level))
                    continue;
                track.IsSilent = level.Silent;
                track.LevelText = DescribeLevel(level);
                if (level.Silent && track.CanChange && track.IsSelected)
                {
                    track.IsSelected = false;
                    deselected++;
                }
            }
            var silent = audio.Count(track => track.IsSilent);
            SilenceStatus = silent == 0 ? strings.String_SilenceDetect_NoneFound
                : string.Format(CultureInfo.CurrentCulture, strings.String_SilenceDetect_Found, silent, deselected);
        }
        catch (OperationCanceledException)
        {
            SilenceStatus = "";
        }
        catch (Exception exception)
        {
            SilenceStatus = string.Format(CultureInfo.CurrentCulture, strings.String_SilenceDetect_Failed, exception.Message);
        }
        finally
        {
            IsDetectingSilence = false;
            _detection = null;
        }
    }

    internal static string DescribeLevel(AudioTrackLevel level)
    {
        var strings = LocalizationManager.Instance;
        if (!level.Decodable)
            return strings.String_SilenceDetect_Undecodable;
        // 杜比 E：分别列出各声道组；全部静音时整条音轨才算静音。
        if (level.DolbyE is { Count: > 0 } programs)
            return string.Format(CultureInfo.CurrentCulture, strings.String_SilenceDetect_DolbyE, string.Join(" · ",
                programs.Select(program => string.Format(CultureInfo.CurrentCulture, strings.String_SilenceDetect_DolbyEGroup,
                    program.Number, program.Layout, program.Silent || program.PeakDb is null
                        ? strings.String_SilenceDetect_SilentShort
                        : string.Format(CultureInfo.CurrentCulture, strings.String_SilenceDetect_GroupPeak,
                            program.PeakDb.Value.ToString("F1", CultureInfo.CurrentCulture))))));
        var peak = level.PeakDb is { } value ? value.ToString("F1", CultureInfo.CurrentCulture) : null;
        return level.Silent
            ? peak is null ? strings.String_SilenceDetect_SilentEmpty
                : string.Format(CultureInfo.CurrentCulture, strings.String_SilenceDetect_Silent, peak)
            : string.Format(CultureInfo.CurrentCulture, strings.String_SilenceDetect_Peak, peak);
    }

    internal void Initialize(RemuxExportSettings settings, IEnumerable<RemuxExportTrackChoice> tracks, bool isMatroskaSource)
    {
        if (settings.Disc is { } disc)
        {
            Containers = [disc.Kind == Models.Discs.DiscKind.Dvd ? "vob" : "m2ts", "mkv", "mp4"];
            OnPropertyChanged(nameof(Containers));
        }
        Container = settings.Container;
        FastSubtitleSeek = settings.FastSubtitleSeek;
        _isMatroskaSource = isMatroskaSource;
        foreach (var choice in tracks)
        {
            choice.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(RemuxExportTrackChoice.IsSelected))
                    OnPropertyChanged(nameof(CanUseFastSubtitleSeek));
            };
            Tracks.Add(choice);
        }
        OnPropertyChanged(nameof(CanUseFastSubtitleSeek));
    }

    internal void InitializeTs(string container, IEnumerable<RemuxExportTrackChoice> tracks, bool frameAccurate)
    {
        IsTsOutput = true;
        TsContainerText = string.Format(Utils.Application.LocalizationManager.Instance.String_TsTracks_SameAsSource,
            container.ToUpperInvariant());
        ShowsFrameAccurateHint = frameAccurate;
        Tracks.AddRange(tracks);
    }

    public bool? DialogResult { get; private set; }
    public event Action? RequestClose;

    [RelayCommand]
    private void Confirm()
    {
        _detection?.Cancel();
        DialogResult = true;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        _detection?.Cancel();
        RequestClose?.Invoke();
    }

    /// <summary>窗口以其他方式关闭（标题栏关闭按钮）时停止检测。</summary>
    internal void StopSilenceDetection() => _detection?.Cancel();

    internal RemuxExportSettings Apply(RemuxExportSettings source)
    {
        var result = source.Snapshot();
        result.Container = Container;
        result.FastSubtitleSeek = FastSubtitleSeek;
        result.StreamIndices = Tracks.Where(track => track.IsSelected).Select(track => track.Index).ToList();
        return result;
    }
}
