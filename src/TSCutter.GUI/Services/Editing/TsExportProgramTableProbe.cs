using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.Models.TransportStream.Processing;
using TSCutter.GUI.Services.TransportStream.Analysis;
using TSCutter.GUI.Utils.Files;
using TSCutter.GUI.Utils.TransportStream.Packets;

namespace TSCutter.GUI.Services.Editing;

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

    internal static Task<IReadOnlyList<(int Pid, byte[] Data)>> ReadTablesNearCutAsync(
        string path, long start, CancellationToken token) => Task.Run(() =>
    {
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
        if (!state.Complete)
            throw new TsClipMergeException(TsClipMergeErrorCode.ProgramTablesUnavailable);
        return state.GetTables();
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
        private readonly Dictionary<int, SectionState> sections = new() { [0] = new() };
        private readonly Dictionary<int, (int Pid, bool Complete)> programs = [];
        private readonly bool[] patSections = new bool[256];
        private int patIdentity = -1;
        private int patSectionCount;
        private int lastPatSection;
        private readonly Dictionary<int, byte[]>? patData = capture ? [] : null;
        private readonly Dictionary<int, byte[]>? pmtData = capture ? [] : null;

        public bool Complete { get; private set; }

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
            if (pid == 0 && section[0] == 0)
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
            Complete = patIdentity >= 0 && patSectionCount == lastPatSection + 1 &&
                programs.Count > 0 && programs.Values.All(program => program.Complete);
        }
    }
}
