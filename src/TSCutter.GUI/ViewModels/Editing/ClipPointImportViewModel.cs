using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Utils;
using TSCutter.GUI.Utils.Application;

namespace TSCutter.GUI.ViewModels.Editing;

public sealed record ClipPointPreview(string Name, string Start, string End, string Status);

public partial class ClipPointImportViewModel : ViewModelBase, IModalDialogViewModel, IDisposable
{
    internal ClipPointFile File { get; private set; } = new();
    private double duration;
    private double currentTime;
    private bool sameSource;
    private int existingCount;
    private ClipCutMode targetMode;
    private ClipListMode targetListMode;
    private int validCount;
    private bool exactUnavailable;
    // 用户改过“替换”后不再自动调整；否则随偏移变化按是否存在模式冲突重新给出默认值。
    private bool replaceChosen;
    private bool settingReplace;

    [ObservableProperty] private decimal? offsetSeconds = 0;
    [ObservableProperty] private bool replace;
    public Bitmap? ReferenceImage { get; private set; }
    public bool HasReferenceImage => ReferenceImage is not null;
    public string SourceName => Path.GetFileName(File.Source.Path.Replace('\\', '/'));
    public string RangeType => File.ClipMode == ClipListMode.Remove
        ? LocalizationManager.Instance.String_ClipPoints_RemoveRanges : LocalizationManager.Instance.String_ClipPoints_KeepRanges;
    public string ReferenceTime => CommonUtil.FormatSeconds(File.Clips[0].StartTime);
    public IReadOnlyList<ClipPointPreview> Entries { get; private set; } = [];
    internal IReadOnlyList<ClipPointRange> Ranges => Exact
        ? File.Clips.Select(clip => new ClipPointRange(clip, clip.StartTime, clip.EndTime, true)).ToArray()
        : ClipPointService.Ranges(File, duration, (double)(OffsetSeconds ?? 0));
    internal bool Exact => sameSource && OffsetSeconds == 0;
    internal ClipCutMode ImportCutMode => Exact ? File.CutMode : targetMode;
    private bool ModeConflict => existingCount > 0 && !Replace &&
        (File.ClipMode != targetListMode || ImportCutMode != targetMode);
    public string MethodText => Exact ? LocalizationManager.Instance.String_ClipPoints_Exact : LocalizationManager.Instance.String_ClipPoints_TimeMapping;
    public string MethodHint => (exactUnavailable ? LocalizationManager.Instance.String_ClipPoints_ModeUnavailable + Environment.NewLine : "") +
        string.Format(Exact ? LocalizationManager.Instance.String_ClipPoints_ExactHint : LocalizationManager.Instance.String_ClipPoints_TimeHint,
            ImportCutMode == ClipCutMode.FrameAccurate ? LocalizationManager.Instance.String_CutMode_FrameAccurate : LocalizationManager.Instance.String_CutMode_KeyFrame);
    public string Summary => string.Format(LocalizationManager.Instance.String_ClipPoints_Summary, validCount, File.Clips.Count - validCount);
    public string ConflictText => ModeConflict ? LocalizationManager.Instance.String_ClipPoints_ModeConflict :
        (Replace ? 0 : existingCount) + validCount > CutterProjectService.MaximumItems
            ? LocalizationManager.Instance.String_ClipPoints_TooMany : "";
    private bool CanConfirm => OffsetSeconds is not null && validCount > 0 && !ModeConflict &&
        (Replace ? 0 : existingCount) + validCount <= CutterProjectService.MaximumItems;
    public bool? DialogResult { get; private set; }
    public event Action? RequestClose;

    /// <param name="matches">素材与视频轨道匹配，可以精确恢复。</param>
    /// <param name="modeUnavailable">素材匹配但保存的智能剪辑模式当前不可用：只能按时间转移，并说明原因。</param>
    internal void Initialize(ClipPointFile file, double targetDuration, double position, bool matches,
        ClipCutMode mode, ClipListMode listMode, int count, bool modeUnavailable = false)
    {
        File = file;
        duration = targetDuration;
        currentTime = position;
        sameSource = matches;
        exactUnavailable = modeUnavailable;
        targetMode = mode;
        targetListMode = listMode;
        existingCount = count;
        replaceChosen = false;
        SetDefaultReplace();
        ReferenceImage?.Dispose();
        ReferenceImage = null;
        if (file.Clips[0].StartThumbnailJpeg is { Length: > 0 } image)
        {
            try
            {
                using var stream = new MemoryStream(image, writable: false);
                ReferenceImage = new Bitmap(stream);
            }
            catch (Exception)
            {
                ReferenceImage = null;
            }
        }
        Refresh();
        OnPropertyChanged(nameof(SourceName));
        OnPropertyChanged(nameof(RangeType));
        OnPropertyChanged(nameof(ReferenceTime));
        OnPropertyChanged(nameof(ReferenceImage));
        OnPropertyChanged(nameof(HasReferenceImage));
    }

    partial void OnOffsetSecondsChanged(decimal? value)
    {
        // 偏移改变会在精确恢复与按时间转移之间切换，导入模式随之变化。
        if (!replaceChosen)
            SetDefaultReplace();
        Refresh();
    }

    partial void OnReplaceChanged(bool value)
    {
        if (!settingReplace)
            replaceChosen = true;
        Refresh();
    }

    /// <summary>只有与现有片段的模式冲突、不能追加时才默认替换。</summary>
    private void SetDefaultReplace()
    {
        settingReplace = true;
        try
        {
            Replace = existingCount > 0 && (File.ClipMode != targetListMode || ImportCutMode != targetMode);
        }
        finally { settingReplace = false; }
    }

    private void Refresh()
    {
        if (File.Clips.Count == 0)
            return;
        var strings = LocalizationManager.Instance;
        var ranges = Ranges;
        validCount = ranges.Count(range => range.InRange);
        Entries = ranges.Select((range, index) => new ClipPointPreview(
            string.IsNullOrWhiteSpace(range.Clip.Name) ? $"#{index + 1}" : range.Clip.Name,
            PreviewTime(range.Start),
            range.Clip.EndPosition < 0 ? strings.String_ClipPoints_EndOfFile : PreviewTime(range.End),
            !range.InRange ? strings.String_ClipPoints_OutOfRange : Exact ? strings.String_ClipPoints_Exact : strings.String_ClipPoints_Review)).ToArray();
        OnPropertyChanged(nameof(Entries));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(MethodText));
        OnPropertyChanged(nameof(MethodHint));
        OnPropertyChanged(nameof(ConflictText));
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    private static string PreviewTime(double seconds) => !double.IsFinite(seconds) || Math.Abs(seconds) > TimeSpan.MaxValue.TotalSeconds / 2
        ? "—" : (seconds < 0 ? "−" : "") + CommonUtil.FormatSeconds(Math.Abs(seconds));

    [RelayCommand]
    private void AlignCurrent() => OffsetSeconds = decimal.Round((decimal)(currentTime - File.Clips[0].StartTime), 3, MidpointRounding.AwayFromZero);

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        if (!CanConfirm)
            return;
        DialogResult = true;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke();

    public void Dispose() => ReferenceImage?.Dispose();
}
