using System.Collections.Generic;
using HanumanInstitute.MvvmDialogs;

namespace TSCutter.GUI.ViewModels.Editing;

public sealed class TsTimestampWarningViewModel : ViewModelBase, IModalDialogViewModel
{
    public IReadOnlyList<string> FileNames { get; set; } = [];
    public bool? DialogResult { get; private set; }

    internal void Confirm() => DialogResult = true;
}
