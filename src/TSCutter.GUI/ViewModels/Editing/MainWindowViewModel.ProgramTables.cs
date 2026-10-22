using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Classic.CommonControls.Dialogs;
using TSCutter.GUI.Utils.Application;

namespace TSCutter.GUI.ViewModels.Editing;

public partial class MainWindowViewModel
{
    private async Task<bool> PrepareProgramTableExportAsync(OutputWindowViewModel output,
        IReadOnlyList<string>? clips = null, Func<bool>? selectionUnchanged = null)
    {
        bool IsCurrent() => selectionUnchanged?.Invoke() ?? true;
        try
        {
            var missing = await RunExportPreparationAsync(output.FindMissingProgramTablesAsync, IsCurrent);
            if (!IsCurrent()) return false;
            var choice = TsProgramTableExportChoice.Original;
            if (missing.Count > 0)
            {
                var warning = _dialogService.CreateViewModel<TsExportWarningViewModel>();
                warning.FileNames = clips ?? missing.Select(path => Path.GetFileName(path)).ToArray();
                await _dialogService.ShowDialogAsync(this, warning);
                choice = warning.Choice;
            }
            if (choice == TsProgramTableExportChoice.Cancel || !IsCurrent()) return false;
            output.SetProgramTableChoice(missing, choice);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception exception)
        {
            if (IsCurrent())
                await ShowMessageAsync(FormatMergeError(exception), LocalizationManager.Instance.String_Error, MessageBoxIcon.Error);
            return false;
        }
    }
}
