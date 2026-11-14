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

/// <summary>按声道组生成默认输出。</summary>
internal static class DolbyEOutputDefaults
{
    /// <summary>新打开的文件：不处理，输出行预先按声道组生成，切换到转码时直接使用。</summary>
    public static DolbyETrackSettings CreateSettings(DolbyETrackInfo info)
    {
        var list = DolbyETrackEditor.CreateOutputList(info);
        return new DolbyETrackSettings
        {
            Pid = info.Pid, ProgramConfig = info.Header.ProgramConfig,
            Outputs = list.Defaults().Select(pair => pair.Output.CopyTo(new DolbyEOutput { Program = pair.SourceKey })).ToList()
        };
    }
}

/// <summary>一条杜比 E 轨的设置：处理方式与按声道组的输出表格。</summary>
public sealed partial class DolbyETrackEditor : ObservableObject
{
    internal DolbyETrackEditor(DolbyETrackInfo info, DolbyETrackSettings settings)
    {
        Info = info;
        Outputs = CreateOutputList(info);
        _transcode = settings.Transcode;
        _keepOriginal = settings.KeepOriginal;
        Outputs.Replace(settings.Outputs.Select(output => ((AudioOutput)output, output.Program)));
        Outputs.IsActive = _transcode;
    }

    internal static AudioOutputList CreateOutputList(DolbyETrackInfo info) => new(
        info.Programs.Select(program => new AudioOutputSource(program.Number,
            string.Format(LocalizationManager.Instance.String_DolbyE_ProgramItem, program.Number, program.LayoutName),
            program.ChannelCount, program.LayoutName)).ToArray(),
        showSourceColumn: true, LocalizationManager.Instance.String_DolbyE_Column_Program,
        LocalizationManager.Instance.String_DolbyE_NoOutputHint, info.Language);

    internal DolbyETrackInfo Info { get; }
    public int Pid => Info.Pid;
    public AudioOutputList Outputs { get; }

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

    [ObservableProperty] private bool _transcode;
    [ObservableProperty] private bool _keepOriginal;

    public bool IsKeepAsIs
    {
        get => !Transcode;
        set => Transcode = !value;
    }

    partial void OnTranscodeChanged(bool value)
    {
        if (value && Outputs.Rows.Count == 0)
            Outputs.Replace(Outputs.Defaults());
        Outputs.IsActive = value;
        OnPropertyChanged(nameof(IsKeepAsIs));
    }

    internal void Reset()
    {
        Transcode = true;
        KeepOriginal = false;
        Outputs.Replace(Outputs.Defaults());
    }

    internal DolbyETrackSettings ToSettings() => new()
    {
        Pid = Info.Pid, ProgramConfig = Info.Header.ProgramConfig, Transcode = Transcode, KeepOriginal = KeepOriginal,
        Outputs = Outputs.Rows.Select(row => row.ToOutput().CopyTo(new DolbyEOutput { Program = row.SourceKey })).ToList()
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
