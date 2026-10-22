using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.Models.TransportStream.Processing;
using TSCutter.GUI.Services.Editing;

namespace TSCutter.GUI.ViewModels.Editing;

internal enum TsProgramTableExportChoice { Cancel, Original, Supplement }

public partial class OutputWindowViewModel
{
    internal Func<IReadOnlyList<string>, CancellationToken, Task<TsProgramTableExportChoice>>? ConfirmProgramTableWarningAsync { get; set; }
    private bool checkingProgramTables;
    private bool programTablesPrepared;
    private bool supplementOutput;
    private readonly HashSet<string> supplementOutputs = [];
    private bool HasProgramTableSupplement => supplementOutput || supplementOutputs.Count > 0;

    internal bool AddsProgramTablesTo(string path) => IsBatchMode ? supplementOutputs.Contains(path) : supplementOutput;

    internal void SetProgramTableChoice(IReadOnlyList<string> missing, TsProgramTableExportChoice choice)
    {
        supplementOutput = !IsBatchMode && choice == TsProgramTableExportChoice.Supplement;
        supplementOutputs.Clear();
        if (IsBatchMode && choice == TsProgramTableExportChoice.Supplement) supplementOutputs.UnionWith(missing);
        programTablesPrepared = choice != TsProgramTableExportChoice.Cancel;
    }

    internal Task<IReadOnlyList<string>> FindMissingProgramTablesAsync(
        IProgress<ExportPreparationProgress>? progress, CancellationToken token)
    {
        // 开始检查前固定任务快照，检查过程不读取界面集合，也不阻塞界面线程。
        var merge = _mergeRequest;
        if (IsRawByteRange || _mediaRequest is not null || merge?.ProgramId is not null)
            return Task.FromResult<IReadOnlyList<string>>([]);
        var batch = IsBatchMode;
        var tasks = batch ? _tasks.ToArray() :
            [new BatchTask(SourceFilePath!, OutputPath!, StartPosition, EndPosition, ProgramId, RemuxSettings)];
        if (merge is null && tasks.All(task => task.RemuxSettings is not null || task.ProgramId is not null))
            return Task.FromResult<IReadOnlyList<string>>([]);
        return Task.Run<IReadOnlyList<string>>(async () =>
        {
            var warnings = new List<string>();
            if (merge is not null)
            {
                progress?.Report(new(0, 1, Path.GetFileName(merge.SourcePath)));
                if (merge.ProgramId is null && !await TsExportProgramTableProbe.HasTablesAsync(merge, token).ConfigureAwait(false))
                    warnings.Add(merge.OutputPath);
                return warnings;
            }
            for (var index = 0; index < tasks.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var task = tasks[index];
                progress?.Report(new(index, tasks.Length, Path.GetFileName(task.DestinationPath)));
                try
                {
                    if (task.RemuxSettings is null && task.ProgramId is null &&
                        !await TsExportProgramTableProbe.HasTablesAsync(task.SourcePath,
                            task.StartPosition, task.EndPosition, token).ConfigureAwait(false))
                        warnings.Add(task.DestinationPath);
                }
                catch (Exception exception) when (batch &&
                    exception is IOException or UnauthorizedAccessException or ArgumentException or
                        NotSupportedException or TsClipMergeException)
                {
                    // 单项读取错误交给后续导出流程记录，其他任务仍然继续。
                    Console.Error.WriteLine($"Program table check skipped: {exception.Message}");
                }
                progress?.Report(new(index + 1, tasks.Length, Path.GetFileName(task.DestinationPath)));
            }
            token.ThrowIfCancellationRequested();
            return warnings;
        }, token);
    }

    private async Task<bool> CheckProgramTablesAsync()
    {
        if (programTablesPrepared) return true;
        checkingProgramTables = true;
        try
        {
            var missing = await FindMissingProgramTablesAsync(null, _cts.Token);
            _cts.Token.ThrowIfCancellationRequested();
            var choice = missing.Count == 0 ? TsProgramTableExportChoice.Original :
                ConfirmProgramTableWarningAsync is null ? TsProgramTableExportChoice.Cancel :
                    await ConfirmProgramTableWarningAsync(missing.Select(Path.GetFileName).ToArray()!, _cts.Token);
            _cts.Token.ThrowIfCancellationRequested();
            if (choice != TsProgramTableExportChoice.Cancel)
            {
                SetProgramTableChoice(missing, choice);
                return true;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Exception = exception; }
        finally { checkingProgramTables = false; }
        DialogResult = false;
        RequestClose?.Invoke();
        return false;
    }
}
