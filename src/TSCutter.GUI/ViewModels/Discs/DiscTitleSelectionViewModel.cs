using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using TSCutter.GUI.Models.Discs;
using TSCutter.GUI.Services.Discs;
using TSCutter.GUI.Utils;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.ViewModels.Discs;

public partial class DiscTitleRow : ObservableObject
{
    public DiscTitleRow(DiscTitleInfo title, DiscInput input, string numberText, string durationText,
        string detailText, bool recommended)
    {
        Title = title;
        Input = input;
        NumberText = numberText;
        DurationText = durationText;
        DetailText = detailText;
        Recommended = recommended;
    }

    public DiscTitleInfo Title { get; }
    public DiscInput Input { get; }
    public string NumberText { get; }
    public string DurationText { get; }
    public string DetailText { get; }
    public bool Recommended { get; }
    public string RecommendedText => Recommended ? LocalizationManager.Instance.String_Disc_Recommended : string.Empty;
}

public partial class DiscTitleSelectionViewModel : ViewModelBase, IModalDialogViewModel
{
    public List<DiscTitleRow> Titles { get; } = [];
    public string Heading { get; private set; } = "";
    public bool? DialogResult { get; private set; }
    public event Action? RequestClose;

    [ObservableProperty]
    private DiscTitleRow? _selectedRow;

    public DiscInput? SelectedInput => SelectedRow?.Input;
    public DiscTitleInfo? SelectedTitle => SelectedRow?.Title;

    partial void OnSelectedRowChanged(DiscTitleRow? value) => ConfirmCommand.NotifyCanExecuteChanged();

    internal void Initialize(string sourcePath, DiscScanResult scan)
    {
        var kindText = scan.Kind == DiscKind.BluRay
            ? LocalizationManager.Instance.String_Disc_KindBluRay
            : LocalizationManager.Instance.String_Disc_KindDvd;
        Heading = string.Format(LocalizationManager.Instance.String_Disc_TitleSelectionHeading,
            kindText, Path.GetFileName(sourcePath));
        Titles.Clear();
        DialogResult = null;
        foreach (var (title, index) in scan.Titles.Select((title, index) => (title, index)))
        {
            Titles.Add(new DiscTitleRow(
                title,
                scan.Inputs[index],
                DescribeNumber(scan.Kind, title.Number),
                CommonUtil.FormatSeconds(title.Duration.TotalSeconds),
                DescribeDetails(title),
                title.Recommended));
        }
        SelectedRow = Titles.FirstOrDefault(row => row.Recommended) ?? Titles.FirstOrDefault();
        OnPropertyChanged(nameof(Titles));
        OnPropertyChanged(nameof(Heading));
    }

    private static string DescribeNumber(DiscKind kind, int number) => kind == DiscKind.BluRay
        ? $"{number:D5}.mpls"
        : number.ToString();

    private static string DescribeDetails(DiscTitleInfo title)
    {
        var parts = new List<string>();
        if (title.ChapterCount > 0)
            parts.Add(string.Format(LocalizationManager.Instance.String_Disc_Chapters, title.ChapterCount));
        if (title.AngleCount > 1)
            parts.Add(string.Format(LocalizationManager.Instance.String_Disc_Angles, title.AngleCount));
        if (title.ClipCount > 1)
            parts.Add(string.Format(LocalizationManager.Instance.String_Disc_Clips, title.ClipCount));
        if (!string.IsNullOrWhiteSpace(title.VideoSpec))
            parts.Add(title.VideoSpec);
        if (title.AudioLanguages.Count > 0)
            parts.Add(string.Format(LocalizationManager.Instance.String_Disc_AudioLanguages,
                string.Join(", ", title.AudioLanguages)));
        if (title.SubtitleLanguages.Count > 0)
            parts.Add(string.Format(LocalizationManager.Instance.String_Disc_SubtitleLanguages,
                string.Join(", ", title.SubtitleLanguages)));
        return string.Join(" · ", parts);
    }

    private bool CanConfirm() => SelectedRow is not null;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        DialogResult = true;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke();
}
