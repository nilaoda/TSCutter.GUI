using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using TSCutter.GUI.Models.Media;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.Utils.Media.DolbyE;

namespace TSCutter.GUI.ViewModels.Media;

/// <summary>下拉框选项：值与显示文字。</summary>
public sealed record DolbyEChoice<T>(T Value, string Text)
{
    public override string ToString() => Text;
}

/// <summary>按声道组生成默认输出。</summary>
internal static class DolbyEOutputDefaults
{
    public static List<DolbyEOutput> Create(DolbyETrackInfo info) => info.Programs.Select(program => new DolbyEOutput
    {
        Program = program.Number, Codec = DolbyECodec.Ac3, Layout = DolbyELayout.Original,
        BitrateKbps = DolbyECodecInfo.DefaultBitrate(DolbyECodec.Ac3,
            DolbyECodecInfo.OutputChannels(DolbyECodec.Ac3, DolbyELayout.Original, program.ChannelCount)),
        Language = NormalizeLanguage(info.Language)
    }).ToList();

    /// <summary>新打开的文件：不处理，输出行预先按声道组生成，切换到转码时直接使用。</summary>
    public static DolbyETrackSettings CreateSettings(DolbyETrackInfo info) => new()
    {
        Pid = info.Pid, ProgramConfig = info.Header.ProgramConfig, Outputs = Create(info)
    };

    public static string NormalizeLanguage(string? language)
    {
        var text = (language ?? "").Trim().ToLowerInvariant();
        return text.Length == 3 && text.All(char.IsAsciiLetterLower) ? text : "";
    }
}

/// <summary>输出表格中的一行。</summary>
public sealed partial class DolbyEOutputRow : ObservableObject
{
    private readonly DolbyETrackEditor owner;
    private bool updating;

    internal DolbyEOutputRow(DolbyETrackEditor owner, DolbyEOutput output)
    {
        this.owner = owner;
        Programs = owner.Info.Programs.Select(program => new DolbyEChoice<int>(program.Number,
            string.Format(LocalizationManager.Instance.String_DolbyE_ProgramItem, program.Number, program.LayoutName))).ToArray();
        updating = true;
        _enabled = output.Enabled;
        _selectedProgram = Programs.FirstOrDefault(choice => choice.Value == output.Program) ?? Programs[0];
        _selectedCodec = Codecs.First(choice => choice.Value == output.Codec);
        _language = output.Language;
        RebuildLayouts(output.Layout);
        RebuildBitrates(output.BitrateKbps);
        updating = false;
    }

    public IReadOnlyList<DolbyEChoice<int>> Programs { get; }

    public IReadOnlyList<DolbyEChoice<DolbyECodec>> Codecs { get; } = Enum.GetValues<DolbyECodec>()
        .Select(codec => new DolbyEChoice<DolbyECodec>(codec, DolbyECodecInfo.Name(codec))).ToArray();

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private DolbyEChoice<int> _selectedProgram;
    [ObservableProperty] private DolbyEChoice<DolbyECodec> _selectedCodec;
    [ObservableProperty] private IReadOnlyList<DolbyEChoice<DolbyELayout>> _layouts = [];
    [ObservableProperty] private DolbyEChoice<DolbyELayout>? _selectedLayout;
    [ObservableProperty] private IReadOnlyList<DolbyEChoice<int>> _bitrates = [];
    [ObservableProperty] private DolbyEChoice<int>? _selectedBitrate;
    [ObservableProperty] private string _language;

    public bool IsLossless => DolbyECodecInfo.IsLossless(SelectedCodec.Value);
    internal DolbyEProgram Program => owner.Info.Programs[SelectedProgram.Value - 1];
    internal DolbyELayout Layout => SelectedLayout?.Value ?? DolbyELayout.Original;
    internal int OutputChannels => DolbyECodecInfo.OutputChannels(SelectedCodec.Value, Layout, Program.ChannelCount);

    [RelayCommand]
    private void Remove() => owner.RemoveRow(this);

    partial void OnEnabledChanged(bool value) => owner.RowChanged();

    partial void OnSelectedProgramChanged(DolbyEChoice<int> value)
    {
        if (updating) return;
        RebuildLayouts(Layout);
        RebuildBitrates(SelectedBitrate?.Value ?? 0);
        owner.RowChanged();
    }

    partial void OnSelectedCodecChanged(DolbyEChoice<DolbyECodec> value)
    {
        if (updating) return;
        RebuildLayouts(Layout);
        RebuildBitrates(SelectedBitrate?.Value ?? 0);
        OnPropertyChanged(nameof(IsLossless));
        owner.RowChanged();
    }

    partial void OnSelectedLayoutChanged(DolbyEChoice<DolbyELayout>? value)
    {
        if (updating) return;
        RebuildBitrates(SelectedBitrate?.Value ?? 0);
        owner.RowChanged();
    }

    private void RebuildLayouts(DolbyELayout preferred)
    {
        var program = owner.Info.Programs[SelectedProgram.Value - 1];
        var choices = new List<DolbyEChoice<DolbyELayout>>();
        var original = DolbyECodecInfo.OutputChannels(SelectedCodec.Value, DolbyELayout.Original, program.ChannelCount) < program.ChannelCount
            ? string.Format(LocalizationManager.Instance.String_DolbyE_Layout_Downmix, program.LayoutName, "5.1")
            : string.Format(LocalizationManager.Instance.String_DolbyE_Layout_Original, program.LayoutName);
        choices.Add(new(DolbyELayout.Original, original));
        if (program.ChannelCount > 2)
            choices.Add(new(DolbyELayout.Stereo, LocalizationManager.Instance.String_DolbyE_Layout_Stereo));
        if (program.ChannelCount > 1 && SelectedCodec.Value != DolbyECodec.Pcm)
            choices.Add(new(DolbyELayout.Mono, LocalizationManager.Instance.String_DolbyE_Layout_Mono));
        var keep = updating;
        updating = true;
        Layouts = choices;
        SelectedLayout = choices.FirstOrDefault(choice => choice.Value == preferred) ?? choices[0];
        updating = keep;
    }

    private void RebuildBitrates(int preferred)
    {
        var codec = SelectedCodec.Value;
        var channels = OutputChannels;
        var keep = updating;
        updating = true;
        Bitrates = DolbyECodecInfo.Bitrates(codec, channels)
            .Select(value => new DolbyEChoice<int>(value, $"{value} kbps")).ToArray();
        var bitrate = DolbyECodecInfo.NormalizeBitrate(codec, channels, preferred);
        SelectedBitrate = Bitrates.FirstOrDefault(choice => choice.Value == bitrate);
        updating = keep;
    }

    internal DolbyEOutput ToOutput() => new()
    {
        Enabled = Enabled, Program = SelectedProgram.Value, Codec = SelectedCodec.Value, Layout = Layout,
        BitrateKbps = SelectedBitrate?.Value ?? 0, Language = DolbyEOutputDefaults.NormalizeLanguage(Language)
    };
}

/// <summary>一条杜比 E 轨的设置。</summary>
public sealed partial class DolbyETrackEditor : ObservableObject
{
    internal DolbyETrackEditor(DolbyETrackInfo info, DolbyETrackSettings settings)
    {
        Info = info;
        _transcode = settings.Transcode;
        _keepOriginal = settings.KeepOriginal;
        foreach (var output in settings.Outputs)
            Rows.Add(new DolbyEOutputRow(this, output));
    }

    internal DolbyETrackInfo Info { get; }
    public int Pid => Info.Pid;

    public string DisplayText => string.Join(" · ", new[] { $"PID 0x{Info.Pid:X4}", Info.Language, "Dolby E" }
        .Where(text => !string.IsNullOrWhiteSpace(text)));

    public string Summary => string.Format(LocalizationManager.Instance.String_DolbyE_Summary, Info.Header.WordBits,
        DolbyEConfiguration.FrameRateName(Info.Header.FrameRateCode), Info.Header.ProgramConfig, ConfigurationName(Info.Header.ProgramConfig));

    public string ProgramCountText => string.Format(LocalizationManager.Instance.String_DolbyE_ProgramCount,
        Info.Programs.Count, Info.Header.ChannelCount);

    public string ChannelMapText => string.Join(Environment.NewLine, Info.Programs.Select(program =>
        string.Format(LocalizationManager.Instance.String_DolbyE_ChannelMapItem, program.Number,
            program.ChannelCount == 1 ? $"{program.FirstChannel}" : $"{program.FirstChannel}–{program.LastChannel}",
            program.ChannelNames)));

    public ObservableCollection<DolbyEOutputRow> Rows { get; } = [];

    [ObservableProperty] private bool _transcode;
    [ObservableProperty] private bool _keepOriginal;

    public bool IsKeepAsIs
    {
        get => !Transcode;
        set => Transcode = !value;
    }

    private IEnumerable<DolbyEOutputRow> ActiveRows => Rows.Where(row => row.Enabled);
    public bool ShowFlacHint => Transcode && ActiveRows.Any(row => row.SelectedCodec.Value == DolbyECodec.Flac);
    public bool ShowPcmHint => Transcode && ActiveRows.Any(row => row.SelectedCodec.Value == DolbyECodec.Pcm);
    public bool ShowAc3Hint => Transcode && ActiveRows.Any(row => !row.IsLossless &&
                                                               row.Layout == DolbyELayout.Original && row.Program.ChannelCount > 6);
    public bool ShowNoOutputHint => Transcode && !ActiveRows.Any();

    partial void OnTranscodeChanged(bool value)
    {
        if (value && Rows.Count == 0)
            foreach (var output in DolbyEOutputDefaults.Create(Info))
                Rows.Add(new DolbyEOutputRow(this, output));
        OnPropertyChanged(nameof(IsKeepAsIs));
        RowChanged();
    }

    [RelayCommand]
    private void AddRow()
    {
        var program = Info.Programs[0];
        Rows.Add(new DolbyEOutputRow(this, new DolbyEOutput
        {
            Program = program.Number, Codec = DolbyECodec.Aac,
            BitrateKbps = DolbyECodecInfo.DefaultBitrate(DolbyECodec.Aac,
                DolbyECodecInfo.OutputChannels(DolbyECodec.Aac, DolbyELayout.Original, program.ChannelCount)),
            Language = DolbyEOutputDefaults.NormalizeLanguage(Info.Language)
        }));
        RowChanged();
    }

    internal void RemoveRow(DolbyEOutputRow row)
    {
        Rows.Remove(row);
        RowChanged();
    }

    internal void Reset()
    {
        Transcode = true;
        KeepOriginal = false;
        Rows.Clear();
        foreach (var output in DolbyEOutputDefaults.Create(Info))
            Rows.Add(new DolbyEOutputRow(this, output));
        RowChanged();
    }

    internal void RowChanged()
    {
        OnPropertyChanged(nameof(ShowFlacHint));
        OnPropertyChanged(nameof(ShowPcmHint));
        OnPropertyChanged(nameof(ShowAc3Hint));
        OnPropertyChanged(nameof(ShowNoOutputHint));
    }

    internal DolbyETrackSettings ToSettings() => new()
    {
        Pid = Info.Pid, ProgramConfig = Info.Header.ProgramConfig, Transcode = Transcode, KeepOriginal = KeepOriginal,
        Outputs = Rows.Select(row => row.ToOutput()).ToList()
    };

    internal static string ConfigurationName(int programConfig) => programConfig == 23
        ? LocalizationManager.Instance.String_DolbyE_Config_71Screen
        : DolbyEConfiguration.Name(programConfig);
}

/// <summary>杜比 E 输出设置窗口。</summary>
public sealed partial class DolbyEOutputWindowViewModel : ViewModelBase, IModalDialogViewModel
{
    public ObservableCollection<DolbyETrackEditor> Tracks { get; } = [];
    [ObservableProperty] private DolbyETrackEditor? _selectedTrack;
    public bool ShowTrackPicker => Tracks.Count > 1;

    public bool? DialogResult { get; private set; }
    public event Action? RequestClose;

    internal void Initialize(IEnumerable<DolbyETrackInfo> tracks, IReadOnlyDictionary<int, DolbyETrackSettings> current,
        int? selectedPid)
    {
        foreach (var info in tracks)
            Tracks.Add(new DolbyETrackEditor(info, current.TryGetValue(info.Pid, out var settings)
                ? settings.Snapshot() : DolbyEOutputDefaults.CreateSettings(info)));
        SelectedTrack = Tracks.FirstOrDefault(track => track.Pid == selectedPid) ?? Tracks.FirstOrDefault();
        OnPropertyChanged(nameof(ShowTrackPicker));
    }

    internal IReadOnlyList<DolbyETrackSettings> Result => Tracks.Select(track => track.ToSettings()).ToArray();

    [RelayCommand]
    private void Confirm()
    {
        DialogResult = true;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke();

    [RelayCommand]
    private void Reset() => SelectedTrack?.Reset();

}
