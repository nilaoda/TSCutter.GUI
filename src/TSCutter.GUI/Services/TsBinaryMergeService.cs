using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using TSCutter.GUI.Models;

namespace TSCutter.GUI.Services;

/// <summary>
/// 按原始 TS 包合并多个文件，可选择严格移除相邻文件的二进制重叠区域。
/// </summary>
internal sealed class TsBinaryMergeService
{
    private const int PacketSize = TsStreamAnalyzer.PacketSize;
    private const int RequiredSyncPackets = 5;
    private const int AnchorPacketCount = 32;
    private const int SignatureProbePacketCount = 256;
    private const int ScanPacketCount = 16_384;
    private const int CopyBufferBytes = 4 * 1024 * 1024;
    private const int AnchorBytes = AnchorPacketCount * PacketSize;
    private const int SignatureProbeBytes = SignatureProbePacketCount * PacketSize;

    public async Task<TsBinaryMergeAnalysis> AnalyzeOverlapsAsync(
        IReadOnlyList<string> sourcePaths,
        long maximumSearchBytes,
        IProgress<TsBinaryMergeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (sourcePaths.Count < 2)
            throw new TsBinaryMergeException(TsBinaryMergeErrorCode.TooFewSources);

        var snapshots = new List<TsBinaryMergeSourceSnapshot>(sourcePaths.Count);
        for (var index = 0; index < sourcePaths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await ValidateSourceAsync(sourcePaths[index], cancellationToken)
                .ConfigureAwait(false);
            snapshots.Add(snapshot);
            progress?.Report(new TsBinaryMergeProgress(
                TsBinaryMergeProgressPhase.Validating,
                index,
                sourcePaths.Count,
                0,
                0,
                0,
                (index + 1) * 5.0 / sourcePaths.Count));
        }

        maximumSearchBytes = AlignDown(Math.Max(AnchorBytes, maximumSearchBytes));
        var joins = new List<TsBinaryMergeJoinAnalysis>(sourcePaths.Count - 1);
        var progressState = new AnalysisProgressState(progress, sourcePaths.Count);
        var previousSourceIndex = 0;
        long estimatedOutputBytes = snapshots[0].FileSize;
        var hasUnmatchedJoins = false;

        for (var sourceIndex = 1; sourceIndex < snapshots.Count; sourceIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var previous = snapshots[previousSourceIndex];
            var current = snapshots[sourceIndex];
            var overlapBytes = await FindOverlapAsync(
                previous,
                current,
                maximumSearchBytes,
                sourceIndex,
                progressState,
                cancellationToken).ConfigureAwait(false);

            var hasReliableOverlap = overlapBytes >= AnchorBytes;
            var fullyContained = hasReliableOverlap && overlapBytes == current.FileSize;
            var join = new TsBinaryMergeJoinAnalysis
            {
                SourceIndex = sourceIndex,
                PreviousSourceIndex = previousSourceIndex,
                OverlapBytes = overlapBytes,
                AppendOffset = hasReliableOverlap ? overlapBytes : 0,
                HasReliableOverlap = hasReliableOverlap,
                IsFullyContained = fullyContained
            };
            joins.Add(join);
            estimatedOutputBytes += current.FileSize - join.AppendOffset;
            hasUnmatchedJoins |= !hasReliableOverlap;

            // 当前文件被前一有效文件完全覆盖时，不让它取代后续接缝的匹配基准。
            if (!fullyContained)
                previousSourceIndex = sourceIndex;

            progressState.ReportCompleted(sourceIndex, join);
        }

        foreach (var snapshot in snapshots)
            EnsureSourceUnchanged(snapshot);

        return new TsBinaryMergeAnalysis
        {
            Sources = snapshots,
            Joins = joins,
            EstimatedOutputBytes = estimatedOutputBytes,
            HasUnmatchedJoins = hasUnmatchedJoins
        };
    }

    public async Task<TsBinaryMergeResult> MergeAsync(
        IReadOnlyList<string> sourcePaths,
        string outputPath,
        TsBinaryMergeAnalysis? analysis,
        bool appendUnmatchedSources,
        IProgress<TsBinaryMergeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (sourcePaths.Count < 2)
            throw new TsBinaryMergeException(TsBinaryMergeErrorCode.TooFewSources);

        var fullOutputPath = Path.GetFullPath(outputPath);
        var fullSourcePaths = sourcePaths.Select(Path.GetFullPath).ToArray();
        if (fullSourcePaths.Any(path => PathsEqual(path, fullOutputPath)))
            throw new TsBinaryMergeException(TsBinaryMergeErrorCode.OutputMatchesSource);

        var snapshots = SnapshotSources(fullSourcePaths);
        if (analysis is not null)
        {
            ValidateAnalysis(analysis, snapshots);
            if (analysis.HasUnmatchedJoins && !appendUnmatchedSources)
                throw new TsBinaryMergeException(TsBinaryMergeErrorCode.UnmatchedJoin);
        }

        var appendOffsets = new long[sourcePaths.Count];
        if (analysis is not null)
        {
            foreach (var join in analysis.Joins)
                appendOffsets[join.SourceIndex] = join.HasReliableOverlap ? join.AppendOffset : 0;
        }

        var totalBytes = snapshots
            .Select((source, index) => source.FileSize - appendOffsets[index])
            .Sum();
        var removedOverlapBytes = appendOffsets.Sum();
        var unmatchedJoinCount = analysis?.Joins.Count(item => !item.HasReliableOverlap) ?? 0;
        var stopwatch = Stopwatch.StartNew();
        var temporaryPath = BuildTemporaryPath(fullOutputPath);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        long processedBytes = 0;
        var lastProgressTimestamp = Stopwatch.GetTimestamp();

        try
        {
            var outputDirectory = Path.GetDirectoryName(fullOutputPath);
            if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
                throw new DirectoryNotFoundException(outputDirectory);

            await using (var output = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             buffer.Length,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                for (var sourceIndex = 0; sourceIndex < snapshots.Count; sourceIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var snapshot = snapshots[sourceIndex];
                    await using var source = new FileStream(
                        snapshot.FilePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        buffer.Length,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await ValidateOpenStreamAsync(source, snapshot.FilePath, cancellationToken)
                        .ConfigureAwait(false);

                    var startOffset = appendOffsets[sourceIndex];
                    source.Position = startOffset;
                    var remaining = snapshot.FileSize - startOffset;
                    while (remaining > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var requested = (int)Math.Min(buffer.Length, remaining);
                        var bytesRead = await source.ReadAsync(
                            buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
                        if (bytesRead <= 0)
                            throw new TsBinaryMergeException(
                                TsBinaryMergeErrorCode.SourceChanged,
                                snapshot.FilePath);

                        await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
                            .ConfigureAwait(false);
                        processedBytes += bytesRead;
                        remaining -= bytesRead;

                        var now = Stopwatch.GetTimestamp();
                        if (Stopwatch.GetElapsedTime(lastProgressTimestamp, now) < TimeSpan.FromMilliseconds(100) &&
                            processedBytes < totalBytes)
                            continue;
                        lastProgressTimestamp = now;
                        progress?.Report(new TsBinaryMergeProgress(
                            TsBinaryMergeProgressPhase.Writing,
                            sourceIndex,
                            snapshots.Count,
                            processedBytes,
                            totalBytes,
                            processedBytes / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds),
                            totalBytes > 0 ? processedBytes * 100.0 / totalBytes : 100));
                    }

                    EnsureSourceUnchanged(snapshot);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, fullOutputPath, true);
            progress?.Report(new TsBinaryMergeProgress(
                TsBinaryMergeProgressPhase.Writing,
                snapshots.Count - 1,
                snapshots.Count,
                totalBytes,
                totalBytes,
                totalBytes / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds),
                100));
            return new TsBinaryMergeResult
            {
                OutputPath = fullOutputPath,
                OutputBytes = totalBytes,
                SourceCount = snapshots.Count,
                RemovedOverlapBytes = removedOverlapBytes,
                UnmatchedJoinCount = unmatchedJoinCount,
                Elapsed = stopwatch.Elapsed
            };
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<long> FindOverlapAsync(
        TsBinaryMergeSourceSnapshot previous,
        TsBinaryMergeSourceSnapshot current,
        long maximumSearchBytes,
        int sourceIndex,
        AnalysisProgressState progress,
        CancellationToken cancellationToken)
    {
        var searchBytes = AlignDown(Math.Min(
            maximumSearchBytes,
            Math.Min(previous.FileSize, current.FileSize)));
        if (searchBytes < AnchorBytes)
            return 0;

        var anchorBuffer = ArrayPool<byte>.Shared.Rent(SignatureProbeBytes);
        var scanBuffer = ArrayPool<byte>.Shared.Rent(ScanPacketCount * PacketSize);
        var compareBuffer = ArrayPool<byte>.Shared.Rent(AnchorBytes);
        try
        {
            await using var previousStream = new FileStream(
                previous.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                scanBuffer.Length,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            await using var currentStream = new FileStream(
                current.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                scanBuffer.Length,
                FileOptions.Asynchronous | FileOptions.RandomAccess);

            var signatureProbeBytes = (int)Math.Min(current.FileSize, SignatureProbeBytes);
            signatureProbeBytes -= signatureProbeBytes % PacketSize;
            await ReadExactlyAtAsync(
                currentStream.SafeFileHandle,
                anchorBuffer.AsMemory(0, signatureProbeBytes),
                0,
                cancellationToken).ConfigureAwait(false);
            progress.AddBytes(signatureProbeBytes);
            var signaturePacketIndex = FindBestSignaturePacket(
                anchorBuffer.AsSpan(0, signatureProbeBytes),
                signatureProbeBytes / PacketSize);
            var signature = CreateSignature(anchorBuffer.AsSpan(
                signaturePacketIndex * PacketSize,
                PacketSize));

            var candidateLower = Math.Max(
                previous.FileSize - searchBytes,
                previous.FileSize - current.FileSize);
            candidateLower = AlignUp(candidateLower);
            var minimumMatchBytes = Math.Max(
                AnchorBytes,
                (signaturePacketIndex + 1L) * PacketSize);
            var candidateUpper = previous.FileSize - minimumMatchBytes;
            if (candidateLower > candidateUpper)
                return 0;

            var scanPosition = candidateLower + signaturePacketIndex * PacketSize;
            var scanEnd = candidateUpper + signaturePacketIndex * PacketSize;
            previousStream.Position = scanPosition;
            while (scanPosition <= scanEnd)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requested = (int)Math.Min(
                    scanBuffer.Length,
                    scanEnd - scanPosition + PacketSize);
                requested -= requested % PacketSize;
                await previousStream.ReadExactlyAsync(
                    scanBuffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
                progress.AddBytes(requested);

                for (var offset = 0; offset < requested; offset += PacketSize)
                {
                    if (!signature.Equals(CreateSignature(
                            scanBuffer.AsSpan(offset, PacketSize))))
                        continue;

                    var selectedPacketPosition = scanPosition + offset;
                    var candidateStart = selectedPacketPosition -
                                         signaturePacketIndex * PacketSize;
                    await ReadExactlyAtAsync(
                        previousStream.SafeFileHandle,
                        compareBuffer.AsMemory(0, AnchorBytes),
                        candidateStart,
                        cancellationToken).ConfigureAwait(false);
                    progress.AddBytes(AnchorBytes);
                    if (!anchorBuffer.AsSpan(0, AnchorBytes)
                            .SequenceEqual(compareBuffer.AsSpan(0, AnchorBytes)))
                        continue;

                    var overlapBytes = previous.FileSize - candidateStart;
                    var verified = await VerifyOverlapAsync(
                        previousStream.SafeFileHandle,
                        currentStream.SafeFileHandle,
                        candidateStart,
                        overlapBytes,
                        sourceIndex,
                        progress,
                        cancellationToken).ConfigureAwait(false);
                    if (verified)
                        return overlapBytes;
                }

                scanPosition += requested;
                progress.ReportSearch(sourceIndex, scanPosition -
                    (candidateLower + signaturePacketIndex * PacketSize),
                    scanEnd - (candidateLower + signaturePacketIndex * PacketSize) + PacketSize);
            }
            return 0;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(anchorBuffer);
            ArrayPool<byte>.Shared.Return(scanBuffer);
            ArrayPool<byte>.Shared.Return(compareBuffer);
        }
    }

    private static async Task<bool> VerifyOverlapAsync(
        SafeFileHandle previousHandle,
        SafeFileHandle currentHandle,
        long previousOffset,
        long overlapBytes,
        int sourceIndex,
        AnalysisProgressState progress,
        CancellationToken cancellationToken)
    {
        var previousBuffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        var currentBuffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            long verifiedBytes = 0;
            while (verifiedBytes < overlapBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requested = (int)Math.Min(CopyBufferBytes, overlapBytes - verifiedBytes);
                await ReadExactlyAtAsync(
                    previousHandle,
                    previousBuffer.AsMemory(0, requested),
                    previousOffset + verifiedBytes,
                    cancellationToken).ConfigureAwait(false);
                await ReadExactlyAtAsync(
                    currentHandle,
                    currentBuffer.AsMemory(0, requested),
                    verifiedBytes,
                    cancellationToken).ConfigureAwait(false);
                progress.AddBytes(requested * 2L);
                if (!previousBuffer.AsSpan(0, requested)
                        .SequenceEqual(currentBuffer.AsSpan(0, requested)))
                    return false;
                verifiedBytes += requested;
                progress.ReportVerification(sourceIndex, verifiedBytes, overlapBytes);
            }
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(previousBuffer);
            ArrayPool<byte>.Shared.Return(currentBuffer);
        }
    }

    private static int FindBestSignaturePacket(ReadOnlySpan<byte> probe, int packetCount)
    {
        var bestIndex = 0;
        var bestScore = -1;
        var initialCount = Math.Min(AnchorPacketCount, packetCount);
        for (var packetIndex = 0; packetIndex < initialCount; packetIndex++)
        {
            var packet = probe.Slice(packetIndex * PacketSize, PacketSize);
            var score = 0;
            for (var index = 5; index < PacketSize; index++)
            {
                if (packet[index] != packet[index - 1])
                    score++;
            }
            if (score <= bestScore)
                continue;
            bestScore = score;
            bestIndex = packetIndex;
        }

        // 文件开头若恰好是连续空包，则扩大探测范围选取更有区分度的包，
        // 避免对大量重复空包反复执行随机读取和完整复核。
        if (bestScore >= 16)
            return bestIndex;
        for (var packetIndex = initialCount; packetIndex < packetCount; packetIndex++)
        {
            var packet = probe.Slice(packetIndex * PacketSize, PacketSize);
            var score = 0;
            for (var index = 5; index < PacketSize; index++)
            {
                if (packet[index] != packet[index - 1])
                    score++;
            }
            if (score <= bestScore)
                continue;
            bestScore = score;
            bestIndex = packetIndex;
        }
        return bestIndex;
    }

    private static PacketSignature CreateSignature(ReadOnlySpan<byte> packet) => new(
        BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(4, 8)),
        BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(48, 8)),
        BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(92, 8)),
        BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(136, 8)));

    private static async Task<TsBinaryMergeSourceSnapshot> ValidateSourceAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            throw new TsBinaryMergeException(TsBinaryMergeErrorCode.SourceMissing, fullPath);
        if (info.Length < RequiredSyncPackets * PacketSize || info.Length % PacketSize != 0)
            throw new TsBinaryMergeException(
                TsBinaryMergeErrorCode.InvalidPacketStructure,
                fullPath);

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            RequiredSyncPackets * PacketSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        await ValidateOpenStreamAsync(stream, fullPath, cancellationToken).ConfigureAwait(false);
        return new TsBinaryMergeSourceSnapshot(
            fullPath,
            info.Length,
            info.LastWriteTimeUtc);
    }

    private static async Task ValidateOpenStreamAsync(
        FileStream stream,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        if (stream.Length < RequiredSyncPackets * PacketSize || stream.Length % PacketSize != 0)
            throw new TsBinaryMergeException(
                TsBinaryMergeErrorCode.InvalidPacketStructure,
                sourcePath);

        var buffer = ArrayPool<byte>.Shared.Rent(RequiredSyncPackets * PacketSize);
        try
        {
            await ReadExactlyAtAsync(
                stream.SafeFileHandle,
                buffer.AsMemory(0, RequiredSyncPackets * PacketSize),
                0,
                cancellationToken).ConfigureAwait(false);
            for (var packet = 0; packet < RequiredSyncPackets; packet++)
            {
                if (buffer[packet * PacketSize] == 0x47)
                    continue;
                throw new TsBinaryMergeException(
                    TsBinaryMergeErrorCode.InvalidPacketStructure,
                    sourcePath);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static List<TsBinaryMergeSourceSnapshot> SnapshotSources(
        IReadOnlyList<string> sourcePaths)
    {
        var snapshots = new List<TsBinaryMergeSourceSnapshot>(sourcePaths.Count);
        foreach (var path in sourcePaths)
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                throw new TsBinaryMergeException(TsBinaryMergeErrorCode.SourceMissing, path);
            if (info.Length < RequiredSyncPackets * PacketSize || info.Length % PacketSize != 0)
                throw new TsBinaryMergeException(
                    TsBinaryMergeErrorCode.InvalidPacketStructure,
                    path);
            snapshots.Add(new TsBinaryMergeSourceSnapshot(
                path,
                info.Length,
                info.LastWriteTimeUtc));
        }
        return snapshots;
    }

    private static void ValidateAnalysis(
        TsBinaryMergeAnalysis analysis,
        IReadOnlyList<TsBinaryMergeSourceSnapshot> sources)
    {
        if (analysis.Sources.Count != sources.Count || analysis.Joins.Count != sources.Count - 1)
            throw new TsBinaryMergeException(TsBinaryMergeErrorCode.AnalysisSourceMismatch);
        for (var index = 0; index < sources.Count; index++)
        {
            var analyzed = analysis.Sources[index];
            var current = sources[index];
            if (!PathsEqual(analyzed.FilePath, current.FilePath) ||
                analyzed.FileSize != current.FileSize ||
                analyzed.LastWriteTimeUtc != current.LastWriteTimeUtc)
                throw new TsBinaryMergeException(
                    TsBinaryMergeErrorCode.AnalysisSourceMismatch,
                    current.FilePath);
        }
    }

    private static void EnsureSourceUnchanged(TsBinaryMergeSourceSnapshot snapshot)
    {
        var info = new FileInfo(snapshot.FilePath);
        if (!info.Exists || info.Length != snapshot.FileSize ||
            info.LastWriteTimeUtc != snapshot.LastWriteTimeUtc)
            throw new TsBinaryMergeException(
                TsBinaryMergeErrorCode.SourceChanged,
                snapshot.FilePath);
    }

    private static async Task ReadExactlyAtAsync(
        SafeFileHandle handle,
        Memory<byte> destination,
        long fileOffset,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var count = await RandomAccess.ReadAsync(
                handle,
                destination[read..],
                fileOffset + read,
                cancellationToken).ConfigureAwait(false);
            if (count <= 0)
                throw new EndOfStreamException();
            read += count;
        }
    }

    private static long AlignDown(long value) => value / PacketSize * PacketSize;

    private static long AlignUp(long value) =>
        value <= 0 ? 0 : (value + PacketSize - 1) / PacketSize * PacketSize;

    private static string BuildTemporaryPath(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath)!;
        var fileName = Path.GetFileName(outputPath);
        return Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.tmp");
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 清理失败不能覆盖原始异常。
        }
    }

    private readonly record struct PacketSignature(
        ulong First,
        ulong Second,
        ulong Third,
        ulong Fourth);

    private sealed class AnalysisProgressState(
        IProgress<TsBinaryMergeProgress>? progress,
        int sourceCount)
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private long _bytesRead;
        private long _lastReportTimestamp;

        public void AddBytes(long value) => _bytesRead += value;

        public void ReportSearch(int sourceIndex, long processed, long total) =>
            Report(TsBinaryMergeProgressPhase.Searching, sourceIndex, processed, total, 0.8);

        public void ReportVerification(int sourceIndex, long processed, long total) =>
            Report(TsBinaryMergeProgressPhase.Verifying, sourceIndex, processed, total, 0.8, 0.2);

        public void ReportCompleted(int sourceIndex, TsBinaryMergeJoinAnalysis join)
        {
            var completed = 5 + sourceIndex * 95.0 / Math.Max(1, sourceCount - 1);
            progress?.Report(new TsBinaryMergeProgress(
                TsBinaryMergeProgressPhase.Verifying,
                sourceIndex,
                sourceCount,
                _bytesRead,
                0,
                _bytesRead / Math.Max(0.001, _stopwatch.Elapsed.TotalSeconds),
                completed,
                join));
        }

        private void Report(
            TsBinaryMergeProgressPhase phase,
            int sourceIndex,
            long processed,
            long total,
            double phaseScale,
            double phaseOffset = 0)
        {
            var now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(_lastReportTimestamp, now) < TimeSpan.FromMilliseconds(100))
                return;
            _lastReportTimestamp = now;
            var local = total > 0 ? Math.Clamp(processed / (double)total, 0, 1) : 0;
            local = phaseOffset + local * phaseScale;
            var percent = 5 + ((sourceIndex - 1) + local) * 95.0 /
                          Math.Max(1, sourceCount - 1);
            progress?.Report(new TsBinaryMergeProgress(
                phase,
                sourceIndex,
                sourceCount,
                _bytesRead,
                total,
                _bytesRead / Math.Max(0.001, _stopwatch.Elapsed.TotalSeconds),
                percent));
        }
    }
}
