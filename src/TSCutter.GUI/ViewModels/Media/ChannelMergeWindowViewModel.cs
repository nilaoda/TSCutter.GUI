using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using TSCutter.GUI.Models.Media;
using TSCutter.GUI.Services.Media;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.Utils.Media;

namespace TSCutter.GUI.ViewModels.Media;

/// <summary>分析结果的小标签：电平（普通）、符合预期（醒目）、需要注意（警示）。</summary>
public sealed record ChannelMergeChip(string Text, bool IsGood, bool IsWarning)
{
    public bool IsPlain => !IsGood && !IsWarning;
}

/// <summary>新建或从工程恢复多轨合并设置的默认值。</summary>
internal static class ChannelMergeDefaults
{
    /// <summary>新打开的文件：不合并；三个槽位按 PID 顺序取前三条音轨，输出行预先生成。</summary>
    public static ChannelMergeSettings Create(ChannelMergeProgramInfo program)
    {
        var outputs = ChannelMergeWindowViewModel.CreateOutputList(program);
        return new ChannelMergeSettings
        {
            ProgramId = program.ProgramId,
            Sources = program.Tracks.Take(ChannelMergeSettings.SlotCount).Select(track => new ChannelMergeSource { Pid = track.Pid }).ToList(),
            Outputs = outputs.Defaults().Select(pair => pair.Output).ToList()
        };
    }
}

/// <summary>一个槽位：目标声道、来源音轨、左右互换与分析结果。</summary>
public sealed partial class ChannelMergeSlotEditor : ObservableObject
{
    private readonly ChannelMergeWindowViewModel owner;

    internal ChannelMergeSlotEditor(ChannelMergeWindowViewModel owner, ChannelMergeSlot slot, IReadOnlyList<AudioChoice<int>> tracks,
        ChannelMergeSource source)
    {
        this.owner = owner;
        Slot = slot;
        Tracks = tracks;
        _selectedTrack = tracks.FirstOrDefault(choice => choice.Value == source.Pid) ?? tracks[0];
        _swap = source.Swap;
    }

    internal ChannelMergeSlot Slot { get; }
    public IReadOnlyList<AudioChoice<int>> Tracks { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AssignmentText))]
    private bool _swap;

    [ObservableProperty] private AudioChoice<int> _selectedTrack;
    [ObservableProperty] private IReadOnlyList<ChannelMergeChip> _chips = [];

    public string Name => Slot switch
    {
        ChannelMergeSlot.Front => LocalizationManager.Instance.String_ChannelMerge_Slot_Front,
        ChannelMergeSlot.CenterLfe => LocalizationManager.Instance.String_ChannelMerge_Slot_CenterLfe,
        _ => LocalizationManager.Instance.String_ChannelMerge_Slot_Surround
    };

    /// <summary>槽位的两路目标声道。</summary>
    internal (string First, string Second) Targets => Slot switch
    {
        ChannelMergeSlot.Front => ("L", "R"),
        ChannelMergeSlot.CenterLfe => ("C", "LFE"),
        _ => ("Ls", "Rs")
    };

    public string AssignmentText
    {
        get
        {
            var strings = LocalizationManager.Instance;
            var (first, second) = Targets;
            var (a, b) = Swap ? (strings.String_ChannelMerge_Right, strings.String_ChannelMerge_Left)
                : (strings.String_ChannelMerge_Left, strings.String_ChannelMerge_Right);
            return string.Format(strings.String_ChannelMerge_Assignment, first, a, second, b);
        }
    }

    [RelayCommand]
    private void ToggleSwap() => Swap = !Swap;

    partial void OnSwapChanged(bool value) => owner.MappingChanged();

    partial void OnSelectedTrackChanged(AudioChoice<int> value) => owner.MappingChanged();

    internal ChannelMergeSource ToSource() => new() { Pid = SelectedTrack.Value, Swap = Swap };
}

/// <summary>
/// 多轨合并设置窗口：处理方式、三个槽位的来源与左右互换、按需运行的声道分析，以及输出表格（与杜比 E 共用）。
/// </summary>
public sealed partial class ChannelMergeWindowViewModel : ViewModelBase, IModalDialogViewModel
{
    private ChannelMergeProgramInfo program = new(0, "", []);
    private Func<IReadOnlyList<int>, IReadOnlyList<int>, IProgress<double>, CancellationToken, Task<ChannelMergeAnalysis>>? analyze;
    private CancellationTokenSource? analysis;
    private ChannelMergeAnalysis? result;

    public ObservableCollection<ChannelMergeSlotEditor> Slots { get; } = [];
    public AudioOutputList Outputs { get; private set; } = CreateOutputList(new ChannelMergeProgramInfo(0, "", []));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsKeep))]
    private bool _merge;

    [ObservableProperty] private bool _keepOriginal;
    [ObservableProperty] private string _analysisStatus = "";
    [ObservableProperty] private bool _isAnalyzing;
    [ObservableProperty] private string _verdictTitle = "";
    [ObservableProperty] private string _verdictText = "";
    [ObservableProperty] private bool _showVerdictGood;
    [ObservableProperty] private bool _showVerdictSuggest;
    [ObservableProperty] private bool _showVerdictWarning;

    public bool IsKeep
    {
        get => !Merge;
        set => Merge = !value;
    }

    public string ProgramText { get; private set; } = "";
    public string SummaryText { get; private set; } = "";
    public bool CanAnalyze => analyze is not null && Merge;

    /// <summary>三个槽位选了重复的音轨。</summary>
    public bool HasDuplicateSlots => Slots.Select(slot => slot.SelectedTrack.Value).Distinct().Count() < Slots.Count;

    public bool? DialogResult { get; private set; }
    public event Action? RequestClose;

    internal static AudioOutputList CreateOutputList(ChannelMergeProgramInfo program) => new(
        [new AudioOutputSource(0, LocalizationManager.Instance.String_ChannelMerge_SourceName, 6, "5.1")],
        showSourceColumn: false, "", LocalizationManager.Instance.String_ChannelMerge_NoOutputHint,
        program.Tracks.Count > 0 ? program.Tracks[0].Language : "");

    internal void Initialize(ChannelMergeProgramInfo info, ChannelMergeSettings settings,
        Func<IReadOnlyList<int>, IReadOnlyList<int>, IProgress<double>, CancellationToken, Task<ChannelMergeAnalysis>>? analyzer)
    {
        program = info;
        analyze = analyzer;
        var strings = LocalizationManager.Instance;
        ProgramText = string.IsNullOrWhiteSpace(info.Name)
            ? string.Format(strings.String_ChannelMerge_Program, info.ProgramId)
            : string.Format(strings.String_ChannelMerge_ProgramNamed, info.ProgramId, info.Name);
        SummaryText = string.Format(strings.String_ChannelMerge_Summary, info.Tracks.Count,
            info.Tracks.Count > 0 ? info.Tracks[0].SampleRate / 1000.0 : 0);
        Outputs = CreateOutputList(info);
        Outputs.Changed += () => OnPropertyChanged(nameof(CanConfirm));
        var choices = info.Tracks.Select(track => new AudioChoice<int>(track.Pid, track.Text)).ToArray();
        var sources = settings.Sources.Count == ChannelMergeSettings.SlotCount
            ? settings.Sources
            : ChannelMergeDefaults.Create(info).Sources;
        Slots.Clear();
        foreach (var (slot, index) in Enum.GetValues<ChannelMergeSlot>().Select((slot, index) => (slot, index)))
            Slots.Add(new ChannelMergeSlotEditor(this, slot, choices, sources[index]));
        // 先放入已有的输出行，再设置处理方式（切换到合并时只在没有输出行时生成默认行）。
        Outputs.Replace(settings.Outputs.Select(output => (output, 0)));
        Merge = settings.Merge;
        KeepOriginal = settings.KeepOriginal;
        Outputs.IsActive = Merge;
        AnalysisStatus = strings.String_ChannelMerge_AnalyzeIdle;
        UpdateResult();
        OnPropertyChanged(string.Empty);
    }

    partial void OnMergeChanged(bool value)
    {
        if (value && Outputs.Rows.Count == 0)
            Outputs.Replace(Outputs.Defaults());
        Outputs.IsActive = value;
        OnPropertyChanged(nameof(CanAnalyze));
        OnPropertyChanged(nameof(CanConfirm));
    }

    /// <summary>映射改变：重新得出结论（不必重新解码），更新标签与重复槽位的提示。</summary>
    internal void MappingChanged()
    {
        OnPropertyChanged(nameof(HasDuplicateSlots));
        OnPropertyChanged(nameof(CanConfirm));
        UpdateResult();
    }

    public bool CanConfirm => !Merge || !HasDuplicateSlots;

    // 分析进行中再次点击是取消，所以命令在执行中也要可用。
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task AnalyzeAsync()
    {
        if (analyze is null)
            return;
        if (IsAnalyzing)
        {
            analysis?.Cancel();
            return;
        }
        var cancellation = analysis = new CancellationTokenSource();
        IsAnalyzing = true;
        var strings = LocalizationManager.Instance;
        AnalysisStatus = string.Format(strings.String_ChannelMerge_Analyzing, 0);
        try
        {
            var progress = new Progress<double>(value =>
            {
                if (IsAnalyzing)
                    AnalysisStatus = string.Format(strings.String_ChannelMerge_Analyzing, (int)Math.Round(value * 100));
            });
            result = await analyze(program.Tracks.Select(track => track.Pid).ToArray(), SelectedPids(), progress, cancellation.Token);
            AnalysisStatus = strings.String_ChannelMerge_Analyzed;
            UpdateResult();
        }
        catch (OperationCanceledException)
        {
            AnalysisStatus = strings.String_ChannelMerge_AnalyzeIdle;
        }
        catch (Exception exception)
        {
            AnalysisStatus = string.Format(strings.String_ChannelMerge_AnalyzeFailed, exception.Message);
        }
        finally
        {
            IsAnalyzing = false;
            cancellation.Dispose();
            if (ReferenceEquals(analysis, cancellation))
                analysis = null;
        }
    }

    /// <summary>窗口关闭（含标题栏关闭按钮）时停止正在进行的分析。</summary>
    public void CancelAnalysis() => analysis?.Cancel();

    [RelayCommand]
    private void ApplySuggestion()
    {
        if (result?.Suggestion is not { } suggestion)
            return;
        for (var i = 0; i < Slots.Count && i < suggestion.Count; i++)
        {
            var slot = Slots[i];
            slot.SelectedTrack = slot.Tracks.First(choice => choice.Value == suggestion[i].Pid);
            slot.Swap = suggestion[i].Swap;
        }
    }

    private IReadOnlyList<int> SelectedPids() => Slots.Select(slot => slot.SelectedTrack.Value).ToArray();

    private void UpdateResult()
    {
        ShowVerdictGood = ShowVerdictSuggest = ShowVerdictWarning = false;
        if (result is null)
        {
            foreach (var slot in Slots)
                slot.Chips = [new ChannelMergeChip(slot.AssignmentText, false, false)];
            return;
        }
        var conclusion = ChannelMergeAnalyzer.Conclude(result.Tracks, SelectedPids());
        result = conclusion;
        foreach (var slot in Slots)
            slot.Chips = Chips(slot, conclusion.Tracks.FirstOrDefault(level => level.Pid == slot.SelectedTrack.Value));
        var strings = LocalizationManager.Instance;
        switch (conclusion.Verdict)
        {
            case ChannelMergeVerdict.Suggested when conclusion.Suggestion is { } suggestion:
                var same = suggestion.Select((item, index) => item.Pid == Slots[index].SelectedTrack.Value && item.Swap == Slots[index].Swap)
                    .All(match => match);
                if (same)
                {
                    VerdictTitle = strings.String_ChannelMerge_Verdict_Match;
                    VerdictText = strings.String_ChannelMerge_Verdict_MatchText;
                    ShowVerdictGood = true;
                }
                else
                {
                    VerdictTitle = strings.String_ChannelMerge_Verdict_Suggest;
                    VerdictText = string.Format(strings.String_ChannelMerge_Verdict_SuggestText,
                        $"0x{suggestion[0].Pid:X4}", $"0x{suggestion[1].Pid:X4}", $"0x{suggestion[2].Pid:X4}") +
                        (suggestion[1].Swap ? strings.String_ChannelMerge_Verdict_SuggestSwap : "");
                    ShowVerdictSuggest = true;
                }
                break;
            case ChannelMergeVerdict.NotSurround:
                VerdictTitle = strings.String_ChannelMerge_Verdict_NotSurround;
                VerdictText = strings.String_ChannelMerge_Verdict_NotSurroundText;
                ShowVerdictWarning = true;
                break;
            case ChannelMergeVerdict.ToneOnly:
                VerdictTitle = strings.String_ChannelMerge_Verdict_Tone;
                VerdictText = strings.String_ChannelMerge_Verdict_ToneText;
                ShowVerdictWarning = true;
                break;
            default:
                VerdictTitle = strings.String_ChannelMerge_Verdict_Undetermined;
                VerdictText = strings.String_ChannelMerge_Verdict_UndeterminedText;
                ShowVerdictWarning = true;
                break;
        }
    }

    private static IReadOnlyList<ChannelMergeChip> Chips(ChannelMergeSlotEditor slot, ChannelMergeTrackLevel? level)
    {
        var strings = LocalizationManager.Instance;
        var chips = new List<ChannelMergeChip>();
        if (level is null || !level.Decodable)
            return [new ChannelMergeChip(strings.String_ChannelMerge_Chip_Undecodable, false, true)];
        var (first, second) = slot.Targets;
        var (a, b) = slot.Swap ? (level.Right, level.Left) : (level.Left, level.Right);
        chips.Add(new ChannelMergeChip($"{first} {Level(a)}", false, false));
        chips.Add(new ChannelMergeChip($"{second} {Level(b)}", false, false));
        if (level.Silent)
            chips.Add(new ChannelMergeChip(strings.String_ChannelMerge_Chip_Silent, false, true));
        else if (level.Tone)
            chips.Add(new ChannelMergeChip(strings.String_ChannelMerge_Chip_Tone, false, true));
        else
        {
            if (level.Mono)
                chips.Add(new ChannelMergeChip(strings.String_ChannelMerge_Chip_Mono, false, true));
            if (level.DuplicateOf is { } other)
                chips.Add(new ChannelMergeChip(string.Format(strings.String_ChannelMerge_Chip_Duplicate, $"0x{other:X4}"), false, true));
            // 中置与低音槽位的第二路应只有低频；其他声道只有低频时提醒。
            if (slot.Slot == ChannelMergeSlot.CenterLfe && b.LowOnly)
                chips.Add(new ChannelMergeChip(strings.String_ChannelMerge_Chip_LfeOk, true, false));
            else if (a.LowOnly || b.LowOnly)
                chips.Add(new ChannelMergeChip(strings.String_ChannelMerge_Chip_LowOnly, false, true));
        }
        return chips;
    }

    private static string Level(ChannelMergeChannelLevel level) => level.PeakDb is { } db
        ? string.Format(LocalizationManager.Instance.String_ChannelMerge_Chip_Peak, db.ToString("0.0"))
        : LocalizationManager.Instance.String_ChannelMerge_Chip_Silent;

    internal ChannelMergeSettings Result => new()
    {
        ProgramId = program.ProgramId, Merge = Merge, KeepOriginal = KeepOriginal,
        Sources = Slots.Select(slot => slot.ToSource()).ToList(),
        Outputs = Outputs.Rows.Select(row => row.ToOutput()).ToList()
    };

    [RelayCommand]
    private void Confirm()
    {
        if (!CanConfirm)
            return;
        analysis?.Cancel();
        DialogResult = true;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        analysis?.Cancel();
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Reset()
    {
        var defaults = ChannelMergeDefaults.Create(program);
        for (var i = 0; i < Slots.Count; i++)
        {
            Slots[i].SelectedTrack = Slots[i].Tracks.First(choice => choice.Value == defaults.Sources[i].Pid);
            Slots[i].Swap = false;
        }
        Merge = true;
        KeepOriginal = false;
        Outputs.Replace(Outputs.Defaults());
    }
}
