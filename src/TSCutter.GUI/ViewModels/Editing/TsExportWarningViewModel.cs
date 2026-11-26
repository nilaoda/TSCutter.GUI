using System.Collections.Generic;
using System.Linq;
using HanumanInstitute.MvvmDialogs;
using TSCutter.GUI.Utils.Application;

namespace TSCutter.GUI.ViewModels.Editing;

public sealed class TsExportWarningViewModel : ViewModelBase, IModalDialogViewModel
{
    public IReadOnlyList<string> FileNames { get; set; } = [];
    private IReadOnlyList<TsProgramTableWarning> warnings = [];
    public bool CanSupplement => warnings.Count > 0 && warnings.All(item => item.Recovery is { CanSupplement: true });
    public bool CanExportCurrentProgram => !CanSupplement && warnings.Count > 0 && warnings.All(item => item.Recovery is { CanExportCurrentProgram: true });
    public bool HasRepair => CanSupplement || CanExportCurrentProgram;
    public string Message
    {
        get
        {
            var strings = LocalizationManager.Instance;
            if (CanSupplement)
                return strings.String_TsExport_TablesWarning;
            if (CanExportCurrentProgram && warnings.Count == 1)
            {
                var recovery = warnings[0].Recovery!;
                return string.Format(strings.String_TsExport_PartialTablesWarning, recovery.ProgramCount,
                    recovery.CompletePrograms.Count, recovery.CurrentProgramId);
            }
            return CanExportCurrentProgram ? strings.String_TsExport_PartialTablesBatchWarning
                : warnings.Count == 1 ? strings.String_TsExport_CurrentTablesUnavailable
                : strings.String_TsExport_UnrecoverableTablesWarning;
        }
    }
    public string Hint => CanSupplement ? LocalizationManager.Instance.String_TsExport_SupplementHint
        : CanExportCurrentProgram ? LocalizationManager.Instance.String_TsExport_CurrentProgramHint
        : LocalizationManager.Instance.String_TsExport_UnrecoverableTablesHint;
    internal TsProgramTableExportChoice Choice { get; private set; }
    public bool? DialogResult { get; private set; }

    internal void SetWarnings(IReadOnlyList<TsProgramTableWarning> value)
    {
        warnings = value;
        OnPropertyChanged(nameof(CanSupplement));
        OnPropertyChanged(nameof(CanExportCurrentProgram));
        OnPropertyChanged(nameof(HasRepair));
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(Hint));
    }

    internal void Select(TsProgramTableExportChoice choice)
    {
        Choice = choice == TsProgramTableExportChoice.Supplement && !CanSupplement ||
                 choice == TsProgramTableExportChoice.CurrentProgram && !CanExportCurrentProgram
            ? TsProgramTableExportChoice.Cancel : choice;
        DialogResult = Choice != TsProgramTableExportChoice.Cancel;
    }
}
