using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.Models.TransportStream.Analysis;
using TSCutter.GUI.Models.TransportStream.Processing;
using TSCutter.GUI.Services.TransportStream.Analysis;
using TSCutter.GUI.Utils.Files;
using TSCutter.GUI.Utils.TransportStream.Packets;

namespace TSCutter.GUI.Services.Editing;

/// <summary>切点附近可恢复的节目表，以及准备阶段记录的源文件校验信息。</summary>
internal sealed record TsProgramTableRecovery(string SourcePath, long CutPosition, long SourceLength,
    DateTime SourceModified, bool HasCompletePat, int ProgramCount, IReadOnlyList<int> CompletePrograms,
    int? CurrentProgramId, IReadOnlyList<(int Pid, byte[] Data)> Tables)
{
    internal IReadOnlyList<(int Pid, byte[] Data)> ServiceTables { get; init; } = [];
    internal TsPacketLayout Layout { get; init; }

    public bool CanSupplement => HasCompletePat && ProgramCount > 0 && CompletePrograms.Count == ProgramCount;
    public bool CanExportCurrentProgram => HasCompletePat && CurrentProgramId is { } id && CompletePrograms.Contains(id);

    public void ValidateSource(string path)
    {
        var info = new FileInfo(path);
        // 录制文件可继续在末尾追加；已选范围不因追加而失效，文件缩短或同长度改写则需要重新准备。
        if (!MediaFileUtil.PathComparer.Equals(Path.GetFullPath(path), SourcePath) || !info.Exists ||
            info.Length < SourceLength || info.Length == SourceLength && info.LastWriteTimeUtc != SourceModified)
            throw new TsClipMergeException(TsClipMergeErrorCode.SourceChanged);
    }

    internal TsCheckResult GetProgramCatalog(string path, long start, int programId, CancellationToken token)
    {
        ValidateSource(path);
        if (start != CutPosition)
            throw new TsClipMergeException(TsClipMergeErrorCode.SourceChanged);
        if (!CanExportCurrentProgram || CurrentProgramId != programId)
            throw new TsClipMergeException(TsClipMergeErrorCode.ProgramTablesUnavailable);
        // 使用已经校验的切点附近表建立映射，避免文件头的节目映射与当前片段不一致。
        using var analyzer = new TsStreamAnalyzer();
        analyzer.BeginSession(path, new TsStreamAnalyzeOptions
        {
            InventoryOnly = true, IncludeServiceMetadata = true, Features = TsStreamAnalyzeFeatures.None
        });
        var continuity = new int[8192];
        Array.Fill(continuity, -1);
        var tables = TsProgramTablePacketizer.Create(Tables.Concat(ServiceTables).ToArray(),
            TsPacketLayout.FromPacketOffset(188, 0), 0, continuity);
        analyzer.AppendOutput(tables, token);
        var catalog = analyzer.CompleteSession(token);
        catalog.PacketSize = Layout.PacketSize;
        catalog.SyncOffset = Layout.PacketOffset;
        return catalog;
    }

    public IReadOnlyList<(int Pid, byte[] Data)> GetTables(string path, long start)
    {
        ValidateSource(path);
        if (start != CutPosition)
            throw new TsClipMergeException(TsClipMergeErrorCode.SourceChanged);
        if (!CanSupplement)
            throw new TsClipMergeException(TsClipMergeErrorCode.ProgramTablesUnavailable);
        return Tables;
    }
}

internal static class TsExportProgramTableProbe
{
    internal const int MaximumProbeBytes = 4 * 1024 * 1024;

    public static Task<bool> HasTablesAsync(string path, long start, long end, CancellationToken token) =>
        Task.Run(() =>
        {
            using var input = MediaFileUtil.OpenRead(path);
            var layout = TsPacketLayout.Detect(input, token, Math.Max(0, start));
            layout.RequireSupported();
            if (layout.IsM2ts && start == 0) start = layout.PacketOffset;
            end = end < 0 ? input.Length : end;
            if (start < 0 || end <= start || end > input.Length)
                throw new TsClipMergeException(TsClipMergeErrorCode.InvalidRange);
            return HasTables(input, [(start, end)], layout, token);
        }, token);

    public static Task<bool> HasTablesAsync(TsClipMergeRequest request, CancellationToken token) =>
        Task.Run(() =>
        {
            using var input = MediaFileUtil.OpenRead(request.SourcePath);
            var start = request.Ranges.Count == 0 ? 0 : Math.Max(0, request.Ranges.Min(range => range.StartPosition));
            var layout = TsPacketLayout.Detect(input, token, start);
            layout.RequireSupported();
            // 与实际合并使用相同的排序、重叠合并和 EOF 对齐规则。
            var ranges = TsClipMergeService.NormalizeRanges(request.Ranges, input.Length, layout)
                .Select(range => (range.StartPosition, range.EndPosition)).ToArray();
            return HasTables(input, ranges, layout, token);
        }, token);

    internal const int MaximumTableSearchBytes = 32 * 1024 * 1024;

    internal static async Task<IReadOnlyList<(int Pid, byte[] Data)>> ReadTablesNearCutAsync(
        string path, long start, CancellationToken token)
    {
        var recovery = await InspectNearCutAsync(path, start, null, token).ConfigureAwait(false);
        return recovery.GetTables(path, recovery.CutPosition);
    }

    internal static Task<TsProgramTableRecovery> InspectNearCutAsync(
        string path, long start, int? currentProgramId, CancellationToken token) => Task.Run(() =>
    {
        var info = new FileInfo(path);
        var length = info.Length;
        var modified = info.LastWriteTimeUtc;
        using var input = MediaFileUtil.OpenRead(path);
        var layout = TsPacketLayout.Detect(input, token, Math.Max(0, start));
        layout.RequireSupported();
        if (start == 0) start = layout.PacketOffset;
        if (start < 0 || start >= input.Length)
            throw new TsClipMergeException(TsClipMergeErrorCode.InvalidRange);
        var before = Math.Max(0, start - MaximumTableSearchBytes);
        before += (start - before) % layout.PacketSize;
        var state = new TableState(capture: true);
        state.BeginRange();
        // 优先使用切点之前最新的完整版本，保留全部节目和原始描述符。
        ScanTables(input, before, start, layout, state, false, token);
        if (!state.Complete)
        {
            // 录制文件开头或切点前缺表时，继续寻找附近第一组完整表。
            ScanTables(input, start, Math.Min(input.Length, start + MaximumTableSearchBytes),
                layout, state, true, token);
        }
        var recovery = new TsProgramTableRecovery(Path.GetFullPath(path), start, length, modified,
            state.HasCompletePat, state.ProgramCount, state.CompletePrograms, currentProgramId, state.GetTables())
        {
            Layout = layout, ServiceTables = state.GetServiceTables()
        };
        recovery.ValidateSource(path);
        return recovery;
    }, token);

    private static void ScanTables(Stream input, long start, long end, TsPacketLayout layout,
        TableState state, bool stopWhenComplete, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(layout.PacketSize * 512);
        try
        {
            input.Position = start;
            while (end - input.Position >= layout.PacketSize)
            {
                token.ThrowIfCancellationRequested();
                var count = (int)Math.Min(layout.PacketSize * 512, end - input.Position);
                count -= count % layout.PacketSize;
                input.ReadExactly(buffer.AsSpan(0, count));
                for (var offset = 0; offset < count; offset += layout.PacketSize)
                {
                    state.Push(layout.GetPacketBody(buffer.AsSpan(), offset));
                    if (stopWhenComplete && state.Complete) return;
                }
            }
            token.ThrowIfCancellationRequested();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    internal static bool HasTables(Stream input, IReadOnlyList<(long Start, long End)> ranges,
        TsPacketLayout layout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var state = new TableState();
        var buffer = ArrayPool<byte>.Shared.Rent(layout.PacketSize * 512);
        var remaining = MaximumProbeBytes;
        try
        {
            foreach (var range in ranges)
            {
                token.ThrowIfCancellationRequested();
                state.BeginRange();
                // 188 字节原样复制会保留损坏前缀，前缀也计入输出头部的探测预算。
                var start = range.Start == 0 ? Math.Max(0, layout.PacketOffset) : range.Start;
                remaining -= (int)Math.Min(remaining, Math.Max(0, start - range.Start));
                input.Position = start;
                var end = Math.Min(range.End, start + remaining);
                while (end - input.Position >= layout.PacketSize)
                {
                    token.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(layout.PacketSize * 512, end - input.Position);
                    count -= count % layout.PacketSize;
                    input.ReadExactly(buffer.AsSpan(0, count));
                    remaining -= count;
                    for (var offset = 0; offset < count; offset += layout.PacketSize)
                    {
                        state.Push(layout.GetPacketBody(buffer.AsSpan(), offset));
                        if (state.Complete) return true;
                    }
                }
                if (remaining < layout.PacketSize) break;
            }
            return false;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private sealed class SectionState
    {
        public TsPsiSectionAssembler Assembler { get; } = new();
        public int Continuity { get; set; } = -1;

        public void Reset()
        {
            Assembler.DiscardUntilPayloadStart();
            Continuity = -1;
        }
    }

    private sealed class TableState(bool capture = false)
    {
        private readonly Dictionary<int, SectionState> sections = capture
            ? new() { [0] = new(), [0x11] = new() } : new() { [0] = new() };
        private readonly Dictionary<int, (int Pid, bool Complete)> programs = [];
        private readonly bool[] patSections = new bool[256];
        private int patIdentity = -1;
        private int patSectionCount;
        private int lastPatSection;
        private readonly Dictionary<int, byte[]>? patData = capture ? [] : null;
        private readonly Dictionary<int, byte[]>? pmtData = capture ? [] : null;
        private readonly Dictionary<int, byte[]> serviceData = [];
        private int serviceIdentity = -1;

        public IReadOnlyList<(int Pid, byte[] Data)> GetServiceTables() =>
            serviceData.OrderBy(item => item.Key).Select(item => (0x11, item.Value)).ToArray();

        public bool Complete { get; private set; }
        public bool HasCompletePat => patIdentity >= 0 && patSectionCount == lastPatSection + 1 && programs.Count > 0;
        public int ProgramCount => programs.Count;
        public IReadOnlyList<int> CompletePrograms => programs.Where(item => item.Value.Complete).Select(item => item.Key).Order().ToArray();

        public IReadOnlyList<(int Pid, byte[] Data)> GetTables() =>
            patData!.OrderBy(item => item.Key).Select(item => (0, item.Value))
                .Concat(pmtData!.OrderBy(item => item.Key).Select(item => (programs[item.Key].Pid, item.Value)))
                .ToArray();

        public void BeginRange()
        {
            // 接缝两侧不连续的 section 不能拼成一张表，已经取得的完整表可以保留。
            foreach (var section in sections.Values) section.Reset();
        }

        public void Push(ReadOnlySpan<byte> packet)
        {
            var info = TsPacketParser.Parse(packet);
            if (!sections.TryGetValue(info.Pid, out var state)) return;
            if (!info.IsValid || info.TransportError || info.ScramblingControl != 0)
            {
                state.Reset();
                return;
            }
            if (info.Discontinuity) state.Reset();
            if (!info.HasPayload || info.PayloadOffset >= packet.Length) return;
            if (state.Continuity == info.ContinuityCounter) return;
            if (state.Continuity >= 0 && ((state.Continuity + 1) & 15) != info.ContinuityCounter)
                state.Assembler.DiscardUntilPayloadStart();
            state.Continuity = info.ContinuityCounter;
            var pid = info.Pid;
            state.Assembler.Push(packet[info.PayloadOffset..], info.PayloadStart, section => ReadSection(pid, section));
        }

        private void ReadSection(int pid, ReadOnlySpan<byte> section)
        {
            if (section.Length < 12 || (section[1] & 0x80) == 0 || (section[5] & 1) == 0 ||
                !TsPsiSectionBuilder.HasValidCrc(section)) return;
            var end = section.Length - 4;
            if (pid == 0x11 && section[0] == 0x42 && section.Length >= 15 && section[6] <= section[7])
            {
                var identity = section[3] << 16 | section[4] << 8 | section[5];
                if (serviceIdentity != identity)
                {
                    serviceData.Clear();
                    serviceIdentity = identity;
                }
                serviceData[section[6]] = section.ToArray();
            }
            else if (pid == 0 && section[0] == 0)
            {
                if ((end - 8) % 4 != 0 || section[6] > section[7]) return;
                // 整张表校验通过后再替换旧版本，避免畸形 PID 留下错误的完整状态。
                for (var offset = 8; offset < end; offset += 4)
                {
                    var program = section[offset] << 8 | section[offset + 1];
                    var pmtPid = (section[offset + 2] & 31) << 8 | section[offset + 3];
                    if (program != 0 && pmtPid is 0 or 0x1fff) return;
                }
                var identity = section[3] << 16 | section[4] << 8 | section[5];
                if (identity != patIdentity || lastPatSection != section[7])
                {
                    patIdentity = identity;
                    lastPatSection = section[7];
                    patSectionCount = 0;
                    Array.Clear(patSections);
                    programs.Clear();
                    patData?.Clear();
                    pmtData?.Clear();
                    var pat = sections[0];
                    sections.Clear();
                    sections.Add(0, pat);
                    if (capture)
                        sections.Add(0x11, new SectionState());
                }
                for (var offset = 8; offset < end; offset += 4)
                {
                    var program = section[offset] << 8 | section[offset + 1];
                    if (program == 0) continue;
                    var pmtPid = (section[offset + 2] & 31) << 8 | section[offset + 3];
                    if (!programs.TryGetValue(program, out var previous) || previous.Pid != pmtPid)
                    {
                        programs[program] = (pmtPid, false);
                        pmtData?.Remove(program);
                    }
                    if (!sections.ContainsKey(pmtPid))
                    {
                        var state = new SectionState();
                        state.Reset();
                        sections.Add(pmtPid, state);
                    }
                }
                if (patData is not null) patData[section[6]] = section.ToArray();
                if (!patSections[section[6]])
                {
                    patSections[section[6]] = true;
                    patSectionCount++;
                }
            }
            else if (section[0] == 2 && section.Length >= 16 && section[6] == 0 && section[7] == 0)
            {
                var program = section[3] << 8 | section[4];
                if (!programs.TryGetValue(program, out var expected) || expected.Pid != pid) return;
                var offset = 12 + ((section[10] & 15) << 8 | section[11]);
                var streams = 0;
                while (offset + 5 <= end)
                {
                    offset += 5 + ((section[offset + 3] & 15) << 8 | section[offset + 4]);
                    streams++;
                }
                if (offset == end && streams > 0)
                {
                    programs[program] = (pid, true);
                    if (pmtData is not null) pmtData[program] = section.ToArray();
                }
            }
            Complete = HasCompletePat && programs.Values.All(program => program.Complete);
        }
    }
}
