using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.Models.TransportStream.Processing;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Utils.Files;

namespace TSCutter.GUI.ViewModels.Editing;

internal enum TsProgramTableExportChoice { Cancel, Original, Supplement, CurrentProgram }
internal sealed record TsProgramTableWarning(string OutputPath, TsProgramTableRecovery? Recovery);

public partial class OutputWindowViewModel
{
    internal Func<IReadOnlyList<string>, CancellationToken, Task<TsProgramTableExportChoice>>? ConfirmProgramTableWarningAsync { get; set; }
    private bool checkingProgramTables;
    private bool programTablesPrepared;
    private bool supplementOutput;
    private readonly HashSet<string> supplementOutputs = [];
    private TsProgramTableExportChoice programTableChoice;
    internal IReadOnlyList<TsProgramTableWarning> ProgramTableWarnings { get; private set; } = [];

    internal bool AddsProgramTablesTo(string path) => IsBatchMode ? supplementOutputs.Contains(path) : supplementOutput;

    private TsProgramTableRecovery? RecoveryForOutput(string path) => IsBatchMode
        ? ProgramTableWarnings.FirstOrDefault(item => MediaFileUtil.PathComparer.Equals(item.OutputPath, path))?.Recovery
        : ProgramTableWarnings.FirstOrDefault()?.Recovery;

    private int? RecoveredProgramFor(string path) => programTableChoice == TsProgramTableExportChoice.CurrentProgram
        ? RecoveryForOutput(path)?.CurrentProgramId : null;

    private TsClipMergeRequest ApplyProgramTableChoice(TsClipMergeRequest request) => RecoveredProgramFor(request.OutputPath) is { } id
        ? new TsClipMergeRequest
        {
            SourcePath = request.SourcePath, OutputPath = request.OutputPath, Ranges = request.Ranges,
            ProgramId = id, VideoPid = request.VideoPid, AudioPids = request.AudioPids,
            AudioOffset90k = request.AudioOffset90k, AudioDelayProgramId = request.AudioDelayProgramId
        } : request;

    private void ValidateRecoveredProgram(string source, string output)
    {
        if (RecoveredProgramFor(output) is not null)
            RecoveryForOutput(output)!.ValidateSource(source);
    }

    internal void SetProgramTableChoice(IReadOnlyList<string> missing, TsProgramTableExportChoice choice)
    {
        if (choice == TsProgramTableExportChoice.Supplement && ProgramTableWarnings.Any(item => item.Recovery is not { CanSupplement: true }) ||
            choice == TsProgramTableExportChoice.CurrentProgram &&
            (ProgramTableWarnings.Count == 0 || ProgramTableWarnings.Any(item => item.Recovery is not { CanExportCurrentProgram: true })))
            throw new TsClipMergeException(TsClipMergeErrorCode.ProgramTablesUnavailable);
        programTableChoice = choice;
        supplementOutput = !IsBatchMode && choice == TsProgramTableExportChoice.Supplement;
        supplementOutputs.Clear();
        if (IsBatchMode && choice == TsProgramTableExportChoice.Supplement)
            supplementOutputs.UnionWith(missing);
        programTablesPrepared = choice != TsProgramTableExportChoice.Cancel;
    }

    internal Task<IReadOnlyList<string>> FindMissingProgramTablesAsync(
        IProgress<ExportPreparationProgress>? progress, CancellationToken token)
    {
        ProgramTableWarnings = [];
        // 开始检查前固定任务快照，检查过程不读取界面集合，也不阻塞界面线程。
        var merge = _mergeRequest;
        if (IsRawByteRange || _mediaRequest is not null || _frameAccurateRequest is not null || merge?.ProgramId is not null)
            return Task.FromResult<IReadOnlyList<string>>([]);
        var batch = IsBatchMode;
        var tasks = batch ? _tasks.ToArray() :
            [new BatchTask(SourceFilePath!, OutputPath!, StartPosition, EndPosition, ProgramId, RemuxSettings,
                AudioDelayProgramId: AudioDelayProgramId, FrameAccurate: FrameAccurate)];
        // 帧精确导出由复用器重新生成节目表，不需要补充原始节目表。
        if (merge is null && tasks.All(task => task.RemuxSettings is not null || task.ProgramId is not null || task.FrameAccurate is not null))
            return Task.FromResult<IReadOnlyList<string>>([]);
        return Task.Run<IReadOnlyList<string>>(async () =>
        {
            var warnings = new List<TsProgramTableWarning>();
            var recovered = new Dictionary<(string Path, long Start, int? Program), TsProgramTableRecovery>();
            async Task<TsProgramTableRecovery> RecoverAsync(string path, long start, int? program)
            {
                var key = (Path.GetFullPath(path), start, program);
                if (!recovered.TryGetValue(key, out var recovery))
                {
                    recovery = await TsExportProgramTableProbe.InspectNearCutAsync(path, start, program, token).ConfigureAwait(false);
                    recovered.Add(key, recovery);
                }
                return recovery;
            }
            if (merge is not null)
            {
                progress?.Report(new(0, 1, Path.GetFileName(merge.SourcePath)));
                if (!await TsExportProgramTableProbe.HasTablesAsync(merge, token).ConfigureAwait(false))
                {
                    using var input = MediaFileUtil.OpenRead(merge.SourcePath);
                    var start = merge.Ranges.Count == 0 ? 0 : Math.Max(0, merge.Ranges.Min(range => range.StartPosition));
                    var layout = Utils.TransportStream.Packets.TsPacketLayout.Detect(input, token, start);
                    var ranges = TsClipMergeService.NormalizeRanges(merge.Ranges, input.Length, layout);
                    if (ranges.Count == 0)
                        throw new TsClipMergeException(TsClipMergeErrorCode.InvalidRange);
                    warnings.Add(new(merge.OutputPath, await RecoverAsync(merge.SourcePath,
                        ranges[0].StartPosition, merge.AudioDelayProgramId).ConfigureAwait(false)));
                }
            }
            else
            {
                for (var index = 0; index < tasks.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var task = tasks[index];
                    progress?.Report(new(index, tasks.Length, Path.GetFileName(task.DestinationPath)));
                    var missingTables = false;
                    try
                    {
                        if (task.RemuxSettings is null && task.ProgramId is null && task.FrameAccurate is null &&
                            !await TsExportProgramTableProbe.HasTablesAsync(task.SourcePath,
                                task.StartPosition, task.EndPosition, token).ConfigureAwait(false))
                        {
                            missingTables = true;
                            warnings.Add(new(task.DestinationPath, await RecoverAsync(task.SourcePath,
                                task.StartPosition, task.AudioDelayProgramId).ConfigureAwait(false)));
                        }
                    }
                    catch (Exception exception) when (batch &&
                        exception is IOException or UnauthorizedAccessException or ArgumentException or
                            NotSupportedException or TsClipMergeException)
                    {
                        // 已确认缺表的任务即使恢复探测失败也保留提示，避免未经确认直接导出。
                        if (missingTables)
                            warnings.Add(new(task.DestinationPath, null));
                        // 单项读取错误交给后续导出流程记录，其他任务仍然继续。
                        Console.Error.WriteLine($"Program table check skipped: {exception.Message}");
                    }
                    progress?.Report(new(index + 1, tasks.Length, Path.GetFileName(task.DestinationPath)));
                }
            }
            token.ThrowIfCancellationRequested();
            ProgramTableWarnings = warnings;
            return warnings.Select(item => item.OutputPath).ToArray();
        }, token);
    }

    private async Task<bool> CheckProgramTablesAsync()
    {
        if (programTablesPrepared)
            return true;
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
