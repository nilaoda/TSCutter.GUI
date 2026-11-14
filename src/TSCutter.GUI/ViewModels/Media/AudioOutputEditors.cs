using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TSCutter.GUI.Models.Media;
using TSCutter.GUI.Utils.Application;

namespace TSCutter.GUI.ViewModels.Media;

/// <summary>下拉框选项：值与显示文字。</summary>
public sealed record AudioChoice<T>(T Value, string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// 输出行可以选择的来源：杜比 E 的声道组，或多轨合并的结果（只有一个）。
/// <paramref name="LayoutName"/> 为“5.1”“2.0”等。
/// </summary>
public sealed record AudioOutputSource(int Key, string Text, int ChannelCount, string LayoutName);

/// <summary>输出表格中的一行：来源、编码、声道、码率与语言。</summary>
public sealed partial class AudioOutputRow : ObservableObject
{
    private readonly AudioOutputList owner;
    private bool updating;

    internal AudioOutputRow(AudioOutputList owner, AudioOutput output, int sourceKey)
    {
        this.owner = owner;
        Sources = owner.Sources.Select(source => new AudioChoice<int>(source.Key, source.Text)).ToArray();
        updating = true;
        _enabled = output.Enabled;
        _selectedSource = Sources.FirstOrDefault(choice => choice.Value == sourceKey) ?? Sources[0];
        _selectedCodec = Codecs.First(choice => choice.Value == output.Codec);
        _language = output.Language;
        RebuildLayouts(output.Layout);
        RebuildBitrates(output.BitrateKbps);
        updating = false;
    }

    public IReadOnlyList<AudioChoice<int>> Sources { get; }

    public IReadOnlyList<AudioChoice<AudioOutputCodec>> Codecs { get; } = Enum.GetValues<AudioOutputCodec>()
        .Select(codec => new AudioChoice<AudioOutputCodec>(codec, AudioOutputCodecInfo.Name(codec))).ToArray();

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private AudioChoice<int> _selectedSource;
    [ObservableProperty] private AudioChoice<AudioOutputCodec> _selectedCodec;
    [ObservableProperty] private IReadOnlyList<AudioChoice<AudioOutputLayout>> _layouts = [];
    [ObservableProperty] private AudioChoice<AudioOutputLayout>? _selectedLayout;
    [ObservableProperty] private IReadOnlyList<AudioChoice<int>> _bitrates = [];
    [ObservableProperty] private AudioChoice<int>? _selectedBitrate;
    [ObservableProperty] private string _language;

    public bool IsLossless => AudioOutputCodecInfo.IsLossless(SelectedCodec.Value);
    public bool ShowSource => owner.ShowSourceColumn;
    internal AudioOutputSource Source => owner.Sources.First(source => source.Key == SelectedSource.Value);
    internal AudioOutputLayout Layout => SelectedLayout?.Value ?? AudioOutputLayout.Original;
    internal int OutputChannels => AudioOutputCodecInfo.OutputChannels(SelectedCodec.Value, Layout, Source.ChannelCount);

    [RelayCommand]
    private void Remove() => owner.RemoveRow(this);

    partial void OnEnabledChanged(bool value) => owner.RowChanged();

    partial void OnSelectedSourceChanged(AudioChoice<int> value)
    {
        if (updating) return;
        RebuildLayouts(Layout);
        RebuildBitrates(SelectedBitrate?.Value ?? 0);
        owner.RowChanged();
    }

    partial void OnSelectedCodecChanged(AudioChoice<AudioOutputCodec> value)
    {
        if (updating) return;
        RebuildLayouts(Layout);
        RebuildBitrates(SelectedBitrate?.Value ?? 0);
        OnPropertyChanged(nameof(IsLossless));
        owner.RowChanged();
    }

    partial void OnSelectedLayoutChanged(AudioChoice<AudioOutputLayout>? value)
    {
        if (updating) return;
        RebuildBitrates(SelectedBitrate?.Value ?? 0);
        owner.RowChanged();
    }

    private void RebuildLayouts(AudioOutputLayout preferred)
    {
        var source = Source;
        var strings = LocalizationManager.Instance;
        var choices = new List<AudioChoice<AudioOutputLayout>>();
        var original = AudioOutputCodecInfo.OutputChannels(SelectedCodec.Value, AudioOutputLayout.Original, source.ChannelCount) <
                       source.ChannelCount
            ? string.Format(strings.String_AudioOutput_Layout_Downmix, source.LayoutName, "5.1")
            : string.Format(strings.String_AudioOutput_Layout_Original, source.LayoutName);
        choices.Add(new(AudioOutputLayout.Original, original));
        if (source.ChannelCount > 2)
            choices.Add(new(AudioOutputLayout.Stereo, strings.String_AudioOutput_Layout_Stereo));
        if (source.ChannelCount > 1 && SelectedCodec.Value != AudioOutputCodec.Pcm)
            choices.Add(new(AudioOutputLayout.Mono, strings.String_AudioOutput_Layout_Mono));
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
        Bitrates = AudioOutputCodecInfo.Bitrates(codec, channels)
            .Select(value => new AudioChoice<int>(value, $"{value} kbps")).ToArray();
        var bitrate = AudioOutputCodecInfo.NormalizeBitrate(codec, channels, preferred);
        SelectedBitrate = Bitrates.FirstOrDefault(choice => choice.Value == bitrate);
        updating = keep;
    }

    /// <summary>这一行的编码设置；来源见 <see cref="SourceKey"/>。</summary>
    internal AudioOutput ToOutput() => new()
    {
        Enabled = Enabled, Codec = SelectedCodec.Value, Layout = Layout, BitrateKbps = SelectedBitrate?.Value ?? 0,
        Language = AudioOutputCodecInfo.NormalizeLanguage(Language)
    };

    internal int SourceKey => SelectedSource.Value;
}

/// <summary>
/// 输出表格：一组输出行与随编码变化的提示。杜比 E 与多轨合并共用；所属窗口决定来源列表、
/// 输出是否生效（<see cref="IsActive"/>）以及没有输出时的提示文字。
/// </summary>
public sealed partial class AudioOutputList : ObservableObject
{
    private readonly string defaultLanguage;

    /// <param name="sources">可选的来源，至少一个。</param>
    /// <param name="showSourceColumn">显示来源列（杜比 E 的声道组）。</param>
    /// <param name="sourceHeader">来源列的标题。</param>
    /// <param name="noOutputHint">处理生效但没有勾选任何输出时的提示。</param>
    internal AudioOutputList(IReadOnlyList<AudioOutputSource> sources, bool showSourceColumn, string sourceHeader,
        string noOutputHint, string defaultLanguage)
    {
        Sources = sources;
        ShowSourceColumn = showSourceColumn;
        SourceHeader = sourceHeader;
        NoOutputHint = noOutputHint;
        this.defaultLanguage = AudioOutputCodecInfo.NormalizeLanguage(defaultLanguage);
    }

    internal IReadOnlyList<AudioOutputSource> Sources { get; }
    public bool ShowSourceColumn { get; }
    public string SourceHeader { get; }
    public string NoOutputHint { get; }
    public ObservableCollection<AudioOutputRow> Rows { get; } = [];

    /// <summary>输出是否生效（选择了转码或合并）；不生效时表格停用，提示不显示。</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>行或设置变化时通知所属窗口。</summary>
    public event Action? Changed;

    private IEnumerable<AudioOutputRow> ActiveRows => Rows.Where(row => row.Enabled);
    public bool ShowFlacHint => IsActive && ActiveRows.Any(row => row.SelectedCodec.Value == AudioOutputCodec.Flac);
    public bool ShowPcmHint => IsActive && ActiveRows.Any(row => row.SelectedCodec.Value == AudioOutputCodec.Pcm);
    public bool ShowLossyLimitHint => IsActive && ActiveRows.Any(row => !row.IsLossless &&
                                                                       row.Layout == AudioOutputLayout.Original &&
                                                                       row.Source.ChannelCount > 6);
    public bool ShowNoOutputHint => IsActive && !ActiveRows.Any();

    partial void OnIsActiveChanged(bool value) => RowChanged();

    /// <summary>某个来源的默认输出：原始声道的 AC3。</summary>
    internal AudioOutput DefaultOutput(AudioOutputSource source) => Default(AudioOutputCodec.Ac3, source);

    private AudioOutput Default(AudioOutputCodec codec, AudioOutputSource source) => new()
    {
        Codec = codec, Layout = AudioOutputLayout.Original,
        BitrateKbps = AudioOutputCodecInfo.DefaultBitrate(codec,
            AudioOutputCodecInfo.OutputChannels(codec, AudioOutputLayout.Original, source.ChannelCount)),
        Language = defaultLanguage
    };

    /// <summary>每个来源各一行默认输出。</summary>
    internal IEnumerable<(AudioOutput Output, int SourceKey)> Defaults() =>
        Sources.Select(source => (DefaultOutput(source), source.Key));

    internal void Add(AudioOutput output, int sourceKey) => Rows.Add(new AudioOutputRow(this, output, sourceKey));

    internal void Replace(IEnumerable<(AudioOutput Output, int SourceKey)> outputs)
    {
        Rows.Clear();
        foreach (var (output, key) in outputs)
            Add(output, key);
        RowChanged();
    }

    [RelayCommand]
    private void AddRow()
    {
        Add(Default(AudioOutputCodec.Aac, Sources[0]), Sources[0].Key);
        RowChanged();
    }

    internal void RemoveRow(AudioOutputRow row)
    {
        Rows.Remove(row);
        RowChanged();
    }

    internal void RowChanged()
    {
        OnPropertyChanged(nameof(ShowFlacHint));
        OnPropertyChanged(nameof(ShowPcmHint));
        OnPropertyChanged(nameof(ShowLossyLimitHint));
        OnPropertyChanged(nameof(ShowNoOutputHint));
        Changed?.Invoke();
    }
}
