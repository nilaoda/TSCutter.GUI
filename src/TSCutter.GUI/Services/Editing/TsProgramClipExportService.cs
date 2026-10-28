using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.Models.TransportStream.Analysis;
using TSCutter.GUI.Models.TransportStream.Processing;
using TSCutter.GUI.Services.TransportStream.Analysis;
using TSCutter.GUI.Services.TransportStream.Processing;
using TSCutter.GUI.Utils.Files;
using TSCutter.GUI.Utils.TransportStream.Packets;

namespace TSCutter.GUI.Services.Editing;

internal sealed class TsProgramClipExportService
{
    private const int PacketSize = TsUtil.TsPacketSize;
    private const int BufferSize = PacketSize * 4096;

    internal static async Task<TsServiceFilterService.OutputContext> OpenWriterAsync(
        string sourcePath, string outputPath, int programId, CancellationToken cancellationToken,
        ExportTsCheckSession? checkSession = null)
    {
        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(outputPath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new TsFilterException(TsFilterErrorCode.SameFile);
        var catalog = await Task.Run(() => new TsStreamAnalyzer().AnalyzeAsync(sourcePath,
            cancellationToken: cancellationToken, options: new TsStreamAnalyzeOptions
            {
                InventoryOnly = true,
                IncludeServiceMetadata = true,
                MinimumBytes = TsStreamAnalyzeOptions.StandardProbeBytes,
                MaxBytes = 64L * 1024 * 1024,
                StablePacketCount = 8192,
                Features = TsStreamAnalyzeFeatures.None
            }), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (catalog.PacketCount == 0 || !catalog.Programs.TryGetValue(programId, out var program) ||
            program.StreamDefinitions.Count == 0)
            throw new TsFilterException(TsFilterErrorCode.MissingProgram, programId);
        return TsServiceFilterService.OutputContext.Create(
            new TsServiceFilterOutput { ServiceId = programId, OutputPath = outputPath },
            catalog, cancellationToken, overwrite: true, checkSession);
    }

    public async Task ExportAsync(string sourcePath, string outputPath, long startPosition,
        long endPosition, int programId, IProgress<TsFilterProgress>? progress,
        CancellationToken cancellationToken, ExportTsCheckSession? checkSession = null)
    {
        await using var source = MediaFileUtil.OpenRead(sourcePath, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var probe = await Task.Run(() => TsScramblingProbe.Probe(sourcePath, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        var layout = probe.Layout;
        layout.RequireSupported();
        var stride = layout.PacketSize;
        var sync = layout.PacketOffset;
        if (!probe.IsSupportedTransportStream)
            throw new TsFilterException(TsFilterErrorCode.SyncLost, 0);
        var sourceLength = source.Length;
        var start = startPosition == 0 ? sync : startPosition;
        var end = endPosition < 0 ? sourceLength - (sourceLength - sync) % stride : endPosition;
        if (start < sync || end <= start || end > sourceLength ||
            (start - sync) % stride != 0 || (end - sync) % stride != 0)
            throw new TsClipMergeException(TsClipMergeErrorCode.InvalidRange);

        var buffer = ArrayPool<byte>.Shared.Rent(stride * 4096);
        var stopwatch = Stopwatch.StartNew();
        TsServiceFilterService.OutputContext? output = null;
        try
        {
            output = await OpenWriterAsync(sourcePath, outputPath, programId, cancellationToken, checkSession).ConfigureAwait(false);
            output.WriteInitialTables();
            source.Position = start;
            long processed = 0;
            var total = end - start;
            while (processed < total)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requested = (int)Math.Min(stride * 4096, total - processed);
                await source.ReadExactlyAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
                for (var offset = 0; offset < requested; offset += stride)
                    output.WriteFilteredPacket(buffer.AsSpan(offset, stride), start + processed + offset);
                await output.FlushIfNeededAsync(cancellationToken).ConfigureAwait(false);
                processed += requested;
                progress?.Report(new TsFilterProgress(processed, total,
                    output.BytesWritten + output.BufferedLength, output.PacketsWritten,
                    processed / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds), stopwatch.Elapsed));
            }
            // 导出固定的起始快照范围；正在录制的文件继续追加不影响已读取部分。
            if (source.Length < sourceLength)
                throw new TsClipMergeException(TsClipMergeErrorCode.SourceChanged);
            await output.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            if (output is not null)
                await output.DisposeAndDeleteAsync().ConfigureAwait(false);
            throw new TsClipMergeException(TsClipMergeErrorCode.SourceChanged);
        }
        catch
        {
            if (output is not null)
                await output.DisposeAndDeleteAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            try
            {
                if (output is not null)
                    await output.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
