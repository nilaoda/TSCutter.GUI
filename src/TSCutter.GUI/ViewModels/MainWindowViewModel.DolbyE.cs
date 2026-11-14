using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using TSCutter.GUI.Models.Media;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.Utils.Media.DolbyE;
using TSCutter.GUI.ViewModels.Media;

namespace TSCutter.GUI.ViewModels;

// 杜比 E：按 PID 记录每条杜比 E 轨的导出处理方式。不同文件的配置通常不同，只随工程文件和导出任务保存，打开文件时默认不处理；
// 修改后与音频延迟一样把已导出的片段标为待导出。
public partial class MainWindowViewModel
{
    private Dictionary<int, DolbyETrackSettings> _dolbyESettings = [];

    private IReadOnlyDictionary<int, DolbyETrackInfo> DolbyETracks =>
        _videoInstance?.DolbyETracks ?? new Dictionary<int, DolbyETrackInfo>();

    public bool HasDolbyETracks => IsVideoInitialized && DolbyETracks.Count > 0;
    public bool IsDolbyEConfigured => _dolbyESettings.Values.Any(settings => settings.HasWork);

    public string DolbyEButtonTip
    {
        get
        {
            var tip = LocalizationManager.Instance.String_DolbyE_ButtonTip;
            var lines = _dolbyESettings.Values.Where(settings => settings.HasWork).Select(settings =>
            {
                var outputs = string.Join(", ", settings.Outputs.Where(output => output.Enabled).Select(output =>
                    string.Format(LocalizationManager.Instance.String_DolbyE_ProgramItem, output.Program,
                        AudioOutputCodecInfo.Name(output.Codec))));
                var text = string.Format(settings.KeepOriginal
                    ? LocalizationManager.Instance.String_DolbyE_SummaryKeep
                    : LocalizationManager.Instance.String_DolbyE_SummaryReplace, outputs);
                return DolbyETracks.Count > 1 ? $"PID 0x{settings.Pid:X4}: {text}" : text;
            }).ToArray();
            return lines.Length == 0 ? tip : tip + "\n" + string.Join("\n", lines);
        }
    }

    /// <summary>当前文件中仍然存在的杜比 E 轨的设置；导出计划见 <see cref="CurrentAudioRebuildPlan"/>。</summary>
    private IEnumerable<DolbyETrackSettings> ActiveDolbyESettings => HasDolbyETracks
        ? _dolbyESettings.Values.Where(settings => DolbyETracks.ContainsKey(settings.Pid))
        : [];

    private bool CanConfigureDolbyE => CanEditVideo && HasDolbyETracks;

    /// <summary>打开或更换文件后按新文件的杜比 E 轨重建设置。</summary>
    private void ResetDolbyESettings()
    {
        _dolbyESettings = DolbyETracks.Values.ToDictionary(info => info.Pid, DolbyEOutputDefaults.CreateSettings);
        NotifyDolbyEChanged();
    }

    /// <summary>从工程恢复：只恢复当前文件中仍存在的杜比 E 轨，声道组超出范围的输出行去掉。</summary>
    private void RestoreDolbyESettings(IEnumerable<DolbyETrackSettings>? saved)
    {
        if (saved is null)
            return;
        foreach (var settings in saved)
        {
            if (!DolbyETracks.TryGetValue(settings.Pid, out var info))
                continue;
            var copy = settings.Snapshot();
            copy.ProgramConfig = info.Header.ProgramConfig;
            copy.Outputs = copy.Outputs.Where(output => output.Program >= 1 && output.Program <= info.Programs.Count).ToList();
            _dolbyESettings[settings.Pid] = copy;
        }
        NotifyDolbyEChanged();
    }

    private List<DolbyETrackSettings>? CaptureDolbyESettings() => HasDolbyETracks
        ? _dolbyESettings.Values.OrderBy(settings => settings.Pid).Select(settings => settings.Snapshot()).ToList()
        : null;

    private void NotifyDolbyEChanged()
    {
        OnPropertyChanged(nameof(HasDolbyETracks));
        OnPropertyChanged(nameof(IsDolbyEConfigured));
        OnPropertyChanged(nameof(DolbyEButtonTip));
        OnPropertyChanged(nameof(HasUnsavedProjectChanges));
        ConfigureDolbyECommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanConfigureDolbyE))]
    private async Task ConfigureDolbyEAsync()
    {
        var video = _videoInstance;
        var dialog = _dialogService.CreateViewModel<DolbyEOutputWindowViewModel>();
        dialog.Initialize(DolbyETracks.Values.OrderBy(info => info.Pid), _dolbyESettings,
            SelectedPreviewAudioTrack?.DolbyEProgram is not null ? SelectedPreviewAudioTrack.Selection.StreamId : null);
        await _dialogService.ShowDialogAsync(this, dialog);
        if (dialog.DialogResult != true || !ReferenceEquals(video, _videoInstance))
            return;
        var updated = dialog.Result.ToDictionary(settings => settings.Pid);
        var before = AudioRebuildPlan.Create(_dolbyESettings.Values);
        var after = AudioRebuildPlan.Create(updated.Values);
        _dolbyESettings = updated;
        // 导出结果取决于转码计划；计划有变化时已导出的片段需要重新导出。
        if (!PlansEqual(before, after))
            InvalidateClipExports();
        NotifyDolbyEChanged();
    }

    private static bool PlansEqual(AudioRebuildPlan? left, AudioRebuildPlan? right) =>
        AudioRebuildPlan.Signature(left) == AudioRebuildPlan.Signature(right);
}
