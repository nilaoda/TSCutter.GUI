using System.Collections.Generic;
using HanumanInstitute.MvvmDialogs;

namespace TSCutter.GUI.ViewModels.Editing;

public sealed class TsExportWarningViewModel : ViewModelBase, IModalDialogViewModel
{
    public IReadOnlyList<string> FileNames { get; set; } = [];
    internal TsProgramTableExportChoice Choice { get; private set; }
    public bool? DialogResult { get; private set; }

    internal void Select(TsProgramTableExportChoice choice)
    {
        Choice = choice;
        DialogResult = choice != TsProgramTableExportChoice.Cancel;
    }
}
