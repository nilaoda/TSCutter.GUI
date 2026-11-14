using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using TSCutter.GUI.Services.TransportStream.Processing;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Services.TransportStream.Analysis;
using TSCutter.GUI.Utils.TransportStream.Packets;

namespace TSCutter.GUI.Services.Editing;

/// <summary>写出的视频访问单元来源：原样复制、改写了内容的源画面、重新编码的画面。</summary>
internal enum TsVideoPiece { Source, Replaced, Encoded }

/// <summary>
/// TS → TS 智能剪辑的输出：以源文件的传输包为骨架逐包写出，保留原片的传输层异常
/// （CC 跳号、TEI、重复包、PES 长度不符、PCR 与时间戳跳变、同步丢失），与关键帧剪辑一致。
/// <list type="bullet">
/// <item>复制部分的视频与选中的音频、字幕按 PES 原样写出；节目表、空包与其余 PID 在首尾保留内容之间原样写出。</item>
/// <item>内容改写过的 PES（I 帧改为 IDR、切片头重编号、补参数集、边界处只保留部分音频帧）写回原包位置：
/// 保留各包包头（TEI、CC 跳号）与适配字段（PCR），只换负载；多出的数据接在后面新增包，不足时最后一包填充、多余的包删去。</item>
/// <item>重编码的画面依次填入被替换画面（显示时间在所选范围内）的视频包位置，保留其 PCR 与到达时机；
/// 位置不够时在下一个保留画面之前补包。</item>
/// <item>每个 PID 的 CC 以偏移接续：新增、删除包不产生假的跳号，原有的跳号与重复包保持不变。</item>
/// </list>
/// 片段内保持源时间戳；后续片段的 PTS/DTS/PCR 整体平移 timestampOffset。导出管线读到的每个包先经 <see cref="Demuxed"/> 登记，
/// 写出的内容经 <see cref="Video"/> 与 <see cref="Other"/> 交来；本类按源文件顺序追随管线，决定未定时等待。
/// </summary>
internal sealed class FrameAccurateTsOutput : IDisposable
{
    private const int Body = 188;
    private const long TimestampModulus = 1L << 33;
    private const int ReadBlock = 1 << 20;

    private sealed class Piece(byte[] data, long pts, long dts, bool verbatim)
    {
        public byte[] Data { get; } = data;
        public long Pts { get; } = pts;
        public long Dts { get; } = dts;
        public bool Verbatim { get; } = verbatim;
    }

    /// <summary>源文件中的一个 PES（按首包位置标识）及管线对它的处理。</summary>
    private sealed class PesRecord(long position, int pid)
    {
        public long Position { get; } = position;
        public int Pid { get; } = pid;
        public int Demuxed;
        public int Holds;
        public readonly List<Piece> Pieces = [];
        public bool Queued;
        public bool Started;
        // 开始写出时记下首个访问单元的时间戳，随后释放各访问单元的数据。
        public long FirstPts = long.MinValue;
        public long FirstDts = long.MinValue;
    }

    private enum Mode { Drop, Source, Replace, Slot }

    /// <summary>扫描中每个 PID 当前所在的 PES 及其写出状态。</summary>
    private sealed class PidState
    {
        public Mode Mode = Mode.Drop;
        public PesRecord? Record;
        public byte[]? Payload;
        public int PayloadOffset;
        public int Offset;
        public int LastOut = -1;
        public bool Rebase;
        // 上一个带负载的源包（重复包按字节完全相同判断；带传输错误标志的包不算重复包）。
        public byte[]? PreviousPayload;
        public bool Duplicate;
        public bool LastSourceEmitted;
        public byte[]? LastPacket;
        public bool OpenKept;
        public EncodedUnit? SlotUnit;
        // 本片段中该 PID 已写出过保留的 PES；之后、下一个保留 PES 之前未经管线的 PES 按原样保留。
        public bool HadKept;
    }

    private sealed class EncodedUnit(byte[] data, bool key)
    {
        public byte[] Data { get; } = data;
        public bool Key { get; } = key;
        public int Offset;
    }

    private readonly TsPacketLayout layout;
    private readonly FileStream source;
    private bool disposed;
    private readonly FileStream? output;
    // 多节目素材：写出的包再经节目过滤（与关键帧剪辑的按节目导出相同），只留所选节目的 PID 并重建节目表。
    private readonly TsServiceFilterService.OutputContext? filter;
    private readonly ExportTsCheckSession? checkSession;
    private readonly int videoPid;
    private readonly HashSet<int> pesPids;
    private readonly HashSet<int> tablePids;
    private readonly bool hevc;
    private readonly bool h264;
    private readonly Dictionary<long, PesRecord> records = [];
    private readonly Dictionary<int, long> maxDemuxed = [];
    // 每个 PID 已交来内容的最靠后的 PES 位置。
    private readonly Dictionary<int, long> keptAhead = [];
    private readonly Dictionary<int, PidState> states = [];
    // 视频按写出顺序排列：源 PES（复制或改写）与重编码画面。
    private readonly Queue<(PesRecord? Pes, EncodedUnit? Encoded)> videoOrder = new();
    private readonly Dictionary<int, List<byte[]>> lastTables = [];
    private readonly List<byte[]> pending = [];
    private readonly byte[] readBuffer = new byte[ReadBlock];
    private long readStart = -1;
    private int readLength;
    private readonly byte[] writeBuffer = new byte[1 << 20];
    private int writeLength;
    private long scan = -1;
    private long firstDemuxedPosition = -1;
    private bool hasDemuxedVideo;
    private long timestampOffset;
    private long segmentStartPts;
    private long? segmentEndPts;
    private bool segmentEnded;
    private bool started;
    private bool anyOutput;
    // 片段结束后仍在前方、尚未写出的保留 PES 数：为 0 且当前没有未写完的保留 PES 时停止扫描。
    private int remainingKept;
    private byte videoStreamId = 0xE0;
    private uint arrivalHeader;
    private uint arrivalOffset;
    private bool arrivalRebase;

    private long bytesWritten;
    public long BytesWritten => filter is null ? bytesWritten : filter.BytesWritten + filter.BufferedLength;

    /// <param name="filter">多节目素材的节目过滤输出（已创建输出文件）；为 null 时直接写入 outputPath。</param>
    public FrameAccurateTsOutput(string sourcePath, string outputPath, TsPacketLayout layout, int videoPid,
        IEnumerable<int> pesPids, IEnumerable<int> tablePids, bool hevc, bool h264, ExportTsCheckSession? checkSession,
        TsServiceFilterService.OutputContext? filter = null)
    {
        this.filter = filter;
        this.layout = layout;
        this.videoPid = videoPid;
        this.pesPids = [.. pesPids];
        this.tablePids = [.. tablePids];
        this.hevc = hevc;
        this.h264 = h264;
        this.checkSession = checkSession;
        source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1,
            FileOptions.RandomAccess);
        try
        {
            if (filter is not null)
            {
                // 过滤输出自己写开头的节目表并替换后续的节目表包，不再补源节目表的原包。
                this.tablePids = [];
                filter.WriteInitialTables();
                return;
            }
            output = TSCutter.GUI.Services.Editing.DolbyE.TsOutputFile.Create(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan);
        }
        catch
        {
            // 过滤输出由调用方在构造失败时释放；这里只关闭自己打开的源文件。
            source.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 输出包长与源相同（界面中 TS 输出沿用源的 TS/M2TS 格式）时按传输包写出；多节目素材另经节目过滤。
    /// TS 输出不能选择轨道：未经管线的 PID（数据轨道、无法解析的字幕等）与节目表一样原样透传。
    /// </summary>
    internal static bool CanUse(TsPacketLayout layout, string container) =>
        layout.IsSupported && layout.IsM2ts == (container == "m2ts");

    public void BeginSegment(long startPts, long? endPts, long offset)
    {
        segmentStartPts = startPts;
        segmentEndPts = endPts;
        timestampOffset = offset;
        segmentEnded = false;
        scan = -1;
        firstDemuxedPosition = -1;
        hasDemuxedVideo = false;
        records.Clear();
        maxDemuxed.Clear();
        keptAhead.Clear();
        videoOrder.Clear();
        foreach (var state in states.Values)
        {
            state.Mode = Mode.Drop;
            state.Record = null;
            state.Payload = null;
            state.OpenKept = false;
            state.HadKept = false;
            state.SlotUnit = null;
            state.PreviousPayload = null;
            state.Rebase = state.LastOut >= 0;
        }
        arrivalRebase = anyOutput && layout.IsM2ts;
        // 非连续片段不能沿用上一段尚未完成的 EIT section。
        filter?.BeginSegment();
        pcrSeams.Clear();
        pcrSeams.UnionWith(pcrClocks.Keys);
    }

    /// <summary>管线读到一个包（已补全同一 PES 中后续访问单元缺失的位置）。</summary>
    public void Demuxed(int pid, long position)
    {
        if (position < 0)
            return;
        if (!hasDemuxedVideo)
        {
            var aligned = layout.AlignPosition(position);
            firstDemuxedPosition = firstDemuxedPosition < 0 ? aligned : Math.Min(firstDemuxedPosition, aligned);
            // 解复用器可能先交来音频，而视频 PES 实际位于它之前。
            // 等首个视频包到达，再从已知最早位置扫描，避免越过尚未交来的视频。
            if (pid == videoPid)
            {
                hasDemuxedVideo = true;
                scan = firstDemuxedPosition;
            }
        }
        if (!records.TryGetValue(position, out var record))
            records[position] = record = new PesRecord(position, pid);
        record.Demuxed++;
        maxDemuxed[pid] = Math.Max(maxDemuxed.GetValueOrDefault(pid, -1), position);
        Pump();
    }

    /// <summary>管线暂存了该 PES 的包、稍后才写出（开放 GOP 的复制起点 I 帧）。</summary>
    public void Hold(long position)
    {
        if (records.TryGetValue(position, out var record))
            record.Holds++;
    }

    public void Video(TsVideoPiece kind, long position, ReadOnlySpan<byte> data, long pts, long dts, bool key)
    {
        if (kind == TsVideoPiece.Encoded)
        {
            videoOrder.Enqueue((null, new EncodedUnit(BuildEncodedPes(data, pts, dts), key)));
            Pump();
            return;
        }
        var record = Find(position);
        if (!record.Queued)
        {
            record.Queued = true;
            videoOrder.Enqueue((record, null));
        }
        if (record.Holds > 0)
            record.Holds--;
        record.Pieces.Add(new Piece(data.ToArray(), pts, dts, kind == TsVideoPiece.Source));
        keptAhead[record.Pid] = Math.Max(keptAhead.GetValueOrDefault(record.Pid, -1), position);
        Pump();
    }

    public void Other(long position, ReadOnlySpan<byte> data, long pts, long dts)
    {
        var record = Find(position);
        record.Pieces.Add(new Piece(data.ToArray(), pts, dts, true));
        keptAhead[record.Pid] = Math.Max(keptAhead.GetValueOrDefault(record.Pid, -1), position);
        Pump();
    }

    private PesRecord Find(long position)
    {
        // 已开始写出的 PES 不能再追加内容：说明写出顺序与源文件不一致，明确报错而不是丢弃。
        if (!records.TryGetValue(position, out var record) || record.Started)
            throw new FrameAccurateExportException(FrameAccurateExportError.IncompatibleStream, $"TS packet position {position}");
        return record;
    }

    /// <summary>片段的全部内容已交来：未写出的 PES 一律不保留，写完最后一个保留 PES 后结束本片段。</summary>
    public void EndSegment()
    {
        segmentEnded = true;
        remainingKept = 0;
        foreach (var record in records.Values)
            if (record.Pieces.Count > 0 && !record.Started)
                remainingKept++;
        Pump();
        // 排在最后、还没有位置可填的重编码画面接在末尾。
        if (states.TryGetValue(videoPid, out var videoState) && videoState.SlotUnit is { } slotUnit)
        {
            WriteEncodedRemainder(slotUnit);
            videoState.SlotUnit = null;
        }
        while (videoOrder.TryPeek(out var unit) && unit.Encoded is { } encoded)
        {
            WriteEncodedRemainder(encoded);
            videoOrder.Dequeue();
        }
        if (videoOrder.Count > 0)
            throw new FrameAccurateExportException(FrameAccurateExportError.IncompatibleStream, "TS video order");
        foreach (var (pid, state) in states)
            if (state.Mode == Mode.Replace && state.Payload is { } payload && state.PayloadOffset < payload.Length)
                InsertRemainder(pid, state);
        pending.Clear();
    }

    public void Complete()
    {
        Flush();
        if (filter is not null)
            filter.CompleteAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        else
            output!.Flush(true);
    }

    /// <summary>可重复调用：正常完成后立即释放，导出结束时的统一清理再调用一次不产生影响。</summary>
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        if (filter is not null)
            filter.DisposeAsync().AsTask().GetAwaiter().GetResult();
        output?.Dispose();
        source.Dispose();
    }

    // ---------------- 扫描 ----------------

    private void Pump()
    {
        if (scan < 0)
            return;
        Span<byte> packet = stackalloc byte[layout.PacketSize];
        while (true)
        {
            if (!ReadPacket(scan, packet, out var length))
                return;
            if (length < layout.PacketSize)
            {
                // 文件末尾不完整的包：片段结束时原样带上，与关键帧剪辑一致。
                if (segmentEnded && started && OpenKeptCount() > 0)
                    EmitRaw(packet[..length]);
                scan = long.MaxValue;
                return;
            }
            var body = packet.Slice(layout.PrefixSize, Body);
            if (body[0] != 0x47)
            {
                // 同步丢失：到下一个同步点之间的字节原样保留（作为普通透传数据）。
                var skipped = Resync(scan);
                if (skipped <= 0)
                {
                    scan = long.MaxValue;
                    return;
                }
                if (started)
                    pending.Add(ReadBytes(scan, skipped));
                scan += skipped;
                continue;
            }
            if (segmentEnded && remainingKept == 0 && OpenKeptCount() == 0)
            {
                scan = long.MaxValue;
                return;
            }
            if (!Process(packet, body))
                return;
            scan += layout.PacketSize;
        }
    }

    private int OpenKeptCount()
    {
        var count = 0;
        foreach (var state in states.Values)
            if (state.OpenKept)
                count++;
        return count;
    }

    /// <summary>处理一个源包；需要等待管线的决定时返回 false。</summary>
    private bool Process(Span<byte> packet, Span<byte> body)
    {
        var pid = ((body[1] & 0x1F) << 8) | body[2];
        var pusi = (body[1] & 0x40) != 0;
        if (layout.IsM2ts)
            arrivalHeader = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(packet);
        if (!pesPids.Contains(pid))
        {
            if (tablePids.Contains(pid))
                RememberTable(pid, pusi, packet);
            if (started)
                pending.Add(packet.ToArray());
            return true;
        }
        var state = State(pid);
        var hasPayload = (body[3] & 0x10) != 0;
        state.Duplicate = hasPayload && (body[1] & 0x80) == 0 && state.PreviousPayload is { } previous &&
                          body.SequenceEqual(previous);
        if (!Dispatch(pid, state, packet, body, pusi && !state.Duplicate))
            return false;
        if (hasPayload)
            state.PreviousPayload = body.ToArray();
        return true;
    }

    /// <summary>按当前 PES 的写法处理一个包；startsPes 时先确定新 PES 的写法（重复的起始包按续包处理）。</summary>
    private bool Dispatch(int pid, PidState state, Span<byte> packet, Span<byte> body, bool startsPes)
    {
        if (startsPes)
        {
            if (!records.TryGetValue(scan, out var record))
            {
                // 管线已读过该 PID 之后的包却没有这个 PES：解复用器未输出（损坏或起点之前），不保留。
                if (!segmentEnded && maxDemuxed.GetValueOrDefault(pid, -1) <= scan)
                    return false;
                record = new PesRecord(scan, pid);
            }
            if (!segmentEnded && (record.Holds > 0 || maxDemuxed.GetValueOrDefault(pid, -1) <= scan))
                return false;
            if (!StartPes(pid, state, record, body))
                return false;
        }
        else if (state.Record is null)
        {
            DropPacket(pid, state, packet, body);
            return true;
        }
        switch (state.Mode)
        {
            case Mode.Source:
                Flush(pending);
                EmitSource(pid, state, packet);
                break;
            case Mode.Replace:
                Flush(pending);
                FillReplace(pid, state, packet, body);
                break;
            case Mode.Slot:
                FillSlot(pid, state, packet, body);
                break;
            default:
                DropPacket(pid, state, packet, body);
                break;
        }
        return true;
    }

    /// <summary>
    /// 扫描到某 PID 新 PES 的首包：结束上一个 PES，按管线的处理确定本 PES 的写法。
    /// 被替换画面的 PES 依次分配一个重编码画面（每个位置一个，放不下的部分在下一个视频 PES 之前补包），
    /// 画面在输出中的位置与原画面相差不超过重排序深度，不会越过时间戳跳变；还不知道下一个画面时返回 false 等待。
    /// </summary>
    private bool StartPes(int pid, PidState state, PesRecord record, ReadOnlySpan<byte> body)
    {
        var mode = Classify(record);
        // 保留区间内解复用器没有输出（损坏）或管线未写出的 PES：与关键帧剪辑一样原样保留，异常一并留下。
        // 音频与字幕：前后都有保留的 PES 时保留，还不知道后面是否有时等待；视频：只限复制区内解复用器完全未输出的 PES。
        var between = false;
        if (mode == Mode.Drop && state.HadKept)
        {
            if (pid != videoPid)
            {
                if (keptAhead.GetValueOrDefault(pid, -1) > scan)
                    between = true;
                else if (!segmentEnded)
                    return false;
            }
            else
                between = record.Demuxed == 0 && state.Mode is Mode.Source or Mode.Replace &&
                          videoOrder.TryPeek(out var ahead) && ahead.Pes is { } next && next.Position > scan;
        }
        if (between)
            mode = Mode.Source;
        var slot = pid == videoPid && mode == Mode.Drop && PesPts(body) is { } slotPts && InSegment(slotPts);
        if (slot && !videoOrder.TryPeek(out _) && !segmentEnded)
            return false;
        if (pid == videoPid && state.SlotUnit is { } previous)
        {
            WriteEncodedRemainder(previous);
            state.SlotUnit = null;
        }
        if (pid == videoPid && mode is Mode.Source or Mode.Replace && !between)
        {
            // 写出顺序在它之前的重编码画面必须先写完；顺序首项不是它说明管线的写出顺序与源文件不一致。
            while (videoOrder.TryPeek(out var unit) && unit.Encoded is { } encoded)
            {
                WriteEncodedRemainder(encoded);
                videoOrder.Dequeue();
            }
            if (!videoOrder.TryDequeue(out var next) || next.Pes != record)
                throw new FrameAccurateExportException(FrameAccurateExportError.IncompatibleStream, "TS video order");
        }
        if (state.Mode == Mode.Replace && state.Payload is { } payload && state.PayloadOffset < payload.Length)
            InsertRemainder(pid, state);
        // 上一个 PES 已写完：不再需要它的记录。
        if (state.Record is { } done)
            records.Remove(done.Position);
        state.Record = record;
        if (!record.Started && record.Pieces.Count > 0 && segmentEnded)
            remainingKept--;
        record.Started = true;
        state.Mode = mode;
        state.Payload = null;
        state.PayloadOffset = 0;
        state.OpenKept = mode is Mode.Source or Mode.Replace;
        state.HadKept |= state.OpenKept;
        if (pid == videoPid && TryReadPesHeader(body, out var header) && header.Length > 3)
            videoStreamId = header[3];
        if (mode == Mode.Replace)
            state.Payload = BuildReplacedPes(record, body, scan);
        if (record.Pieces.Count > 0)
        {
            record.FirstPts = record.Pieces[0].Pts;
            record.FirstDts = record.Pieces[0].Dts;
            record.Pieces.Clear();
        }
        if (slot && videoOrder.TryPeek(out var queued) && queued.Encoded is { } assigned)
        {
            videoOrder.Dequeue();
            state.SlotUnit = assigned;
            state.Mode = Mode.Slot;
        }
        return true;
    }

    private static Mode Classify(PesRecord record)
    {
        if (record.Pieces.Count == 0)
            return Mode.Drop;
        if (record.Pieces.Count == record.Demuxed && record.Pieces.TrueForAll(piece => piece.Verbatim))
            return Mode.Source;
        return Mode.Replace;
    }

    private bool InSegment(long pts)
    {
        if (Difference(pts, segmentStartPts) < 0)
            return false;
        return segmentEndPts is not { } end || Difference(pts, end) <= 0;
    }

    /// <summary>33 位时间戳之差（考虑回绕），返回 a − b。</summary>
    private static long Difference(long a, long b)
    {
        var diff = ((a - b) % TimestampModulus + TimestampModulus) % TimestampModulus;
        return diff >= TimestampModulus / 2 ? diff - TimestampModulus : diff;
    }

    // ---------------- 写出各类包 ----------------

    private void EmitSource(int pid, PidState state, Span<byte> packet)
    {
        var copy = packet.ToArray();
        var body = copy.AsSpan(layout.PrefixSize, Body);
        if ((body[1] & 0x40) != 0 && state.Record is { FirstPts: not long.MinValue } record)
            RewriteTimestamps(body, record.FirstPts, record.FirstDts);
        else if ((body[1] & 0x40) != 0)
            ShiftPesTimestamps(body);
        EmitWithContinuity(pid, state, copy, keepSourceGap: true);
    }

    /// <summary>改写 PES 的负载依次写回原包：保留包头与适配字段，最后一包用适配字段填充，多余的源包删去。</summary>
    private void FillReplace(int pid, PidState state, Span<byte> packet, Span<byte> body)
    {
        var payload = state.Payload!;
        // 只有适配字段的包（PCR、填充）不携带负载、不占 CC，原样保留。
        if ((body[3] & 0x10) == 0)
        {
            var kept = packet.ToArray();
            EmitWithContinuity(pid, state, kept, keepSourceGap: true);
            return;
        }
        if (state.Duplicate)
        {
            // 源中的重复包：上一包已写出时原样重复写出的那一包，否则一并删去。
            if (state.LastSourceEmitted && state.LastPacket is { } last)
                EmitRaw(last);
            return;
        }
        if (state.PayloadOffset >= payload.Length)
        {
            DropPacket(pid, state, packet, body);
            return;
        }
        var copy = packet.ToArray();
        var target = copy.AsSpan(layout.PrefixSize, Body);
        var (adaptation, _) = Adaptation(target);
        if (state.PayloadOffset == 0)
            target[1] |= 0x40;
        else
            target[1] &= 0xBF;
        state.PayloadOffset += WritePayload(copy, adaptation, payload.AsSpan(state.PayloadOffset), keepAdaptation: true);
        EmitWithContinuity(pid, state, copy, keepSourceGap: true);
    }

    /// <summary>被替换画面的包位置：放入分配给它的重编码画面；画面已写完时删去多余的包（保留其中的 PCR）。</summary>
    private void FillSlot(int pid, PidState state, Span<byte> packet, Span<byte> body)
    {
        var encoded = state.SlotUnit;
        if (encoded is null || encoded.Offset >= encoded.Data.Length || (body[3] & 0x10) == 0)
        {
            DropPacket(pid, state, packet, body);
            return;
        }
        Flush(pending);
        var copy = packet.ToArray();
        var target = copy.AsSpan(layout.PrefixSize, Body);
        // 新数据：清除原包的传输错误标志与随机访问标志，保留 PCR。
        target[1] &= 0x7F;
        var (adaptation, _) = Adaptation(target);
        if (adaptation > 1)
            target[5] &= 0xBF;
        var first = encoded.Offset == 0;
        target[1] = (byte)(first ? target[1] | 0x40 : target[1] & 0xBF);
        if (first && encoded.Key)
            adaptation = SetRandomAccess(copy, adaptation);
        encoded.Offset += WritePayload(copy, adaptation, encoded.Data.AsSpan(encoded.Offset), keepAdaptation: true);
        EmitWithContinuity(pid, state, copy, keepSourceGap: false);
    }

    private void DropPacket(int pid, PidState state, Span<byte> packet, ReadOnlySpan<byte> body)
    {
        var hasPayload = (body[3] & 0x10) != 0;
        state.LastSourceEmitted = false;
        // 删去带负载的包：之后的 CC 前移一位，不产生跳号（重复包本就不占 CC）。
        if (hasPayload && !state.Duplicate)
            state.Offset = (state.Offset - 1) & 0x0F;
        if (!started || !HasPcr(body))
            return;
        // 保留 PCR：改为只有适配字段的包（不带负载、CC 不递增）。
        var copy = packet.ToArray();
        var target = copy.AsSpan(layout.PrefixSize, Body);
        target[1] &= 0xBF;
        var adaptationLength = target[4];
        target[3] = (byte)((target[3] & 0xC0) | 0x20);
        target[4] = 183;
        target[5] &= 0xBF;
        target[(5 + adaptationLength)..].Fill(0xFF);
        ShiftPcr(target);
        Flush(pending);
        if (state.LastOut >= 0)
            target[3] = (byte)((target[3] & 0xF0) | state.LastOut);
        WriteArrival(copy);
        Emit(copy);
    }

    /// <summary>写出一个包并接续该 PID 的 CC：keepSourceGap 时按源 CC 加偏移（保留原有跳号），否则紧接上一包。</summary>
    private void EmitWithContinuity(int pid, PidState state, byte[] packet, bool keepSourceGap)
    {
        var body = packet.AsSpan(layout.PrefixSize, Body);
        var hasPayload = (body[3] & 0x10) != 0;
        var sourceCc = body[3] & 0x0F;
        if (state.Rebase)
        {
            // 后续片段：每个 PID 只计算一次偏移，消除接缝处的跳号，片段内部的跳号保持。
            state.Offset = ((hasPayload ? state.LastOut + 1 : state.LastOut) - sourceCc) & 0x0F;
            state.Rebase = false;
        }
        int cc;
        if (keepSourceGap)
            cc = (sourceCc + state.Offset) & 0x0F;
        else
        {
            cc = state.LastOut < 0 ? sourceCc : hasPayload ? (state.LastOut + 1) & 0x0F : state.LastOut;
            state.Offset = (cc - sourceCc) & 0x0F;
        }
        body[3] = (byte)((body[3] & 0xF0) | cc);
        state.LastOut = cc;
        state.LastSourceEmitted = true;
        ShiftPcr(body);
        WriteArrival(packet);
        state.LastPacket = packet;
        Emit(packet);
    }

    /// <summary>当前 PID 已没有可写回的源包，剩余负载接在后面新增包（不带 PCR）。</summary>
    private void InsertRemainder(int pid, PidState state)
    {
        var payload = state.Payload!;
        while (state.PayloadOffset < payload.Length)
        {
            var packet = NewPacket(pid, state.PayloadOffset == 0);
            state.PayloadOffset += WritePayload(packet, 0, payload.AsSpan(state.PayloadOffset), keepAdaptation: false);
            EmitNew(pid, state, packet);
        }
    }

    private void WriteEncodedRemainder(EncodedUnit encoded)
    {
        var state = State(videoPid);
        while (encoded.Offset < encoded.Data.Length)
        {
            var packet = NewPacket(videoPid, encoded.Offset == 0);
            var adaptation = encoded.Offset == 0 && encoded.Key ? SetRandomAccess(packet, 0) : 0;
            encoded.Offset += WritePayload(packet, adaptation, encoded.Data.AsSpan(encoded.Offset), keepAdaptation: adaptation > 0);
            EmitNew(videoPid, state, packet);
        }
    }

    private void EmitNew(int pid, PidState state, byte[] packet)
    {
        Flush(pending);
        var body = packet.AsSpan(layout.PrefixSize, Body);
        var cc = state.LastOut < 0 ? 0 : (state.LastOut + 1) & 0x0F;
        body[3] = (byte)((body[3] & 0xF0) | cc);
        // 新增包占用一个 CC，之后的源包整体后移一位。该 PID 此前没有写出过包（或片段刚开始）时，
        // 下一个源包直接接在新增包之后。
        if (state.LastOut < 0)
            state.Rebase = true;
        else if (!state.Rebase)
            state.Offset = (state.Offset + 1) & 0x0F;
        state.LastOut = cc;
        WriteArrival(packet);
        state.LastPacket = packet;
        Emit(packet);
    }

    private byte[] NewPacket(int pid, bool start)
    {
        var packet = new byte[layout.PacketSize];
        var body = packet.AsSpan(layout.PrefixSize, Body);
        body[0] = 0x47;
        body[1] = (byte)((start ? 0x40 : 0) | ((pid >> 8) & 0x1F));
        body[2] = (byte)pid;
        body[3] = 0x10;
        if (layout.IsM2ts)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet, arrivalHeader);
        return packet;
    }

    /// <summary>
    /// 把负载写入包（adaptation 为现有适配字段的总长度，含长度字节）；剩余数据不足以填满时以适配字段填充。返回写入的负载字节数。
    /// </summary>
    private int WritePayload(byte[] packet, int adaptation, ReadOnlySpan<byte> payload, bool keepAdaptation)
    {
        var body = packet.AsSpan(layout.PrefixSize, Body);
        if (!keepAdaptation)
            adaptation = 0;
        var capacity = Body - 4 - adaptation;
        var count = Math.Min(capacity, payload.Length);
        if (count < capacity)
        {
            // 填充：扩展（或新建）适配字段。只差 1 字节时适配字段长度为 0。
            var stuffing = capacity - count;
            if (adaptation == 0)
            {
                body[4] = (byte)(stuffing - 1);
                if (stuffing > 1)
                {
                    body[5] = 0;
                    body[6..(4 + stuffing)].Fill(0xFF);
                }
            }
            else
            {
                body[(4 + adaptation)..(4 + adaptation + stuffing)].Fill(0xFF);
                body[4] = (byte)(body[4] + stuffing);
            }
            adaptation += stuffing;
        }
        body[3] = (byte)((body[3] & 0xCF) | (adaptation > 0 ? 0x30 : 0x10));
        payload[..count].CopyTo(body[(4 + adaptation)..]);
        return count;
    }

    /// <summary>设置随机访问标志（需要时新建适配字段），返回适配字段总长度。</summary>
    private int SetRandomAccess(byte[] packet, int adaptation)
    {
        var body = packet.AsSpan(layout.PrefixSize, Body);
        if (adaptation == 0)
        {
            body[3] = (byte)(body[3] | 0x20);
            body[4] = 1;
            body[5] = 0x40;
            return 2;
        }
        if (body[4] > 0)
            body[5] |= 0x40;
        return adaptation;
    }

    /// <summary>适配字段总长度（含长度字节）与是否带负载。</summary>
    private static (int Length, bool Payload) Adaptation(ReadOnlySpan<byte> body)
    {
        var control = (body[3] >> 4) & 3;
        var length = (control & 2) != 0 ? 1 + Math.Min(body[4], (byte)183) : 0;
        return (length, (control & 1) != 0);
    }

    // 适配字段长度超过 183 的包（多见于传输错误包）内容不可信，不按 PCR 处理。
    private static bool HasPcr(ReadOnlySpan<byte> body) =>
        (body[3] & 0x20) != 0 && body[4] is >= 7 and <= 183 && (body[5] & 0x10) != 0;

    private void ShiftPcr(Span<byte> body)
    {
        if (!HasPcr(body))
            return;
        ShiftPcrValue(body);
        MarkSeam(body);
    }

    /// <summary>
    /// 后续片段每个 PCR PID 的首个 PCR：倒退或与此前间隔差别明显时设置 discontinuity_indicator，
    /// 声明这是合法的剪接点（各片段的复用缓冲延迟不同），而不是 PCR 异常。
    /// </summary>
    private void MarkSeam(Span<byte> body)
    {
        var pid = ((body[1] & 0x1F) << 8) | body[2];
        var raw = ((long)body[6] << 25) | ((long)body[7] << 17) | ((long)body[8] << 9) | ((long)body[9] << 1) | ((long)body[10] >> 7);
        if (pcrClocks.TryGetValue(pid, out var clock))
        {
            var delta = Difference(raw, clock.Last);
            if (pcrSeams.Remove(pid) && (delta <= 0 || Math.Abs(delta - clock.Interval) >= SeamThreshold))
                body[5] |= 0x80;
            else if (delta > 0)
                clock.Interval = delta;
            clock.Last = raw;
        }
        else
            pcrClocks[pid] = new PcrClock { Last = raw };
    }

    private const long SeamThreshold = 90000 / 10;

    private sealed class PcrClock
    {
        public long Last;
        public long Interval;
    }

    private readonly Dictionary<int, PcrClock> pcrClocks = [];
    private readonly HashSet<int> pcrSeams = [];

    private void ShiftPcrValue(Span<byte> body)
    {
        if (timestampOffset == 0)
            return;
        var raw = ((long)body[6] << 25) | ((long)body[7] << 17) | ((long)body[8] << 9) | ((long)body[9] << 1) | ((long)body[10] >> 7);
        raw = ((raw + timestampOffset) % TimestampModulus + TimestampModulus) % TimestampModulus;
        body[6] = (byte)(raw >> 25);
        body[7] = (byte)(raw >> 17);
        body[8] = (byte)(raw >> 9);
        body[9] = (byte)(raw >> 1);
        body[10] = (byte)((body[10] & 0x7F) | (int)((raw & 1) << 7));
    }

    // ---------------- PES ----------------

    private static int PayloadStart(ReadOnlySpan<byte> body)
    {
        var (adaptation, payload) = Adaptation(body);
        return payload ? 4 + adaptation : Body;
    }

    private static bool TryReadPesHeader(ReadOnlySpan<byte> body, out ReadOnlySpan<byte> header)
    {
        header = default;
        var start = PayloadStart(body);
        var data = body[start..];
        if (data.Length < 9 || data[0] != 0 || data[1] != 0 || data[2] != 1 || data[3] < 0xBC || data[3] is 0xBE or 0xBF)
            return false;
        var length = 9 + data[8];
        if (length > data.Length)
            return false;
        header = data[..length];
        return true;
    }

    private static long? PesPts(ReadOnlySpan<byte> body) =>
        TryReadPesHeader(body, out var header) && (header[7] & 0x80) != 0 && header.Length >= 14 ? ReadTimestamp(header[9..]) : null;

    private static long ReadTimestamp(ReadOnlySpan<byte> field) =>
        ((long)(field[0] >> 1 & 7) << 30) | ((long)field[1] << 22) | ((long)(field[2] >> 1) << 15) | ((long)field[3] << 7) | ((long)field[4] >> 1);

    private static void WriteTimestamp(Span<byte> field, long value)
    {
        value = (value % TimestampModulus + TimestampModulus) % TimestampModulus;
        field[0] = (byte)((field[0] & 0xF1) | (int)((value >> 30 & 7) << 1) | 1);
        field[1] = (byte)(value >> 22);
        field[2] = (byte)(((value >> 15 & 0x7F) << 1) | 1);
        field[3] = (byte)(value >> 7);
        field[4] = (byte)(((value & 0x7F) << 1) | 1);
    }

    /// <summary>按写出时的时间戳改写 PES 头中已有的 PTS/DTS（不增删字段，保持包结构不变）。</summary>
    private static void RewriteTimestamps(Span<byte> body, long pts, long dts)
    {
        var start = PayloadStart(body);
        if (!TryReadPesHeader(body, out var header))
            return;
        var flags = header[7] >> 6;
        var fields = body[(start + 9)..];
        if ((flags & 2) != 0 && pts != long.MinValue && ReadTimestamp(fields) != (pts % TimestampModulus + TimestampModulus) % TimestampModulus)
            WriteTimestamp(fields, pts);
        if (flags == 3 && dts != long.MinValue && ReadTimestamp(fields[5..]) != (dts % TimestampModulus + TimestampModulus) % TimestampModulus)
            WriteTimestamp(fields[5..], dts);
    }

    /// <summary>透传的 PES（未经管线的 PID）只按片段平移时间戳。</summary>
    private void ShiftPesTimestamps(Span<byte> body)
    {
        if (timestampOffset == 0 || !TryReadPesHeader(body, out var header))
            return;
        var flags = header[7] >> 6;
        var fields = body[(PayloadStart(body) + 9)..];
        if ((flags & 2) != 0)
            WriteTimestamp(fields, ReadTimestamp(fields) + timestampOffset);
        if (flags == 3)
            WriteTimestamp(fields[5..], ReadTimestamp(fields[5..]) + timestampOffset);
    }

    /// <summary>
    /// 内容改写的 PES：沿用源 PES 头（时间戳换成写出的值），负载为管线写出的数据；
    /// 源头声明了长度时按总长度的变化调整，原有的长度不符（如缺包）保持不变。
    /// </summary>
    private byte[] BuildReplacedPes(PesRecord record, ReadOnlySpan<byte> body, long position)
    {
        var dataLength = 0;
        foreach (var piece in record.Pieces)
            dataLength += piece.Data.Length;
        if (!TryReadPesHeader(body, out var sourceHeader))
            throw new FrameAccurateExportException(FrameAccurateExportError.IncompatibleStream, "PES header");
        var header = sourceHeader.ToArray();
        var flags = header[7] >> 6;
        if ((flags & 2) != 0)
            WriteTimestamp(header.AsSpan(9), record.Pieces[0].Pts);
        if (flags == 3)
            WriteTimestamp(header.AsSpan(14), record.Pieces[0].Dts);
        var result = new byte[header.Length + dataLength];
        var declared = (header[4] << 8) | header[5];
        if (declared != 0)
        {
            var updated = declared + result.Length - MeasurePes(position, record.Pid);
            if (updated is <= 0 or > 0xFFFF)
                updated = 0;
            header[4] = (byte)(updated >> 8);
            header[5] = (byte)updated;
        }
        header.CopyTo(result, 0);
        var offset = header.Length;
        foreach (var piece in record.Pieces)
        {
            piece.Data.CopyTo(result, offset);
            offset += piece.Data.Length;
        }
        return result;
    }

    /// <summary>源文件中从 position 开始的 PES 在传输包中实际携带的字节数（到该 PID 的下一个 PES 起点为止）。</summary>
    private int MeasurePes(long position, int pid)
    {
        var total = 0;
        var buffer = new byte[layout.PacketSize];
        using var reader = new FileStream(source.Name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        reader.Position = position;
        var first = true;
        while (reader.Read(buffer) == buffer.Length)
        {
            var body = buffer.AsSpan(layout.PrefixSize, Body);
            if (body[0] != 0x47)
                break;
            var packetPid = ((body[1] & 0x1F) << 8) | body[2];
            if (packetPid == pid)
            {
                if (!first && (body[1] & 0x40) != 0)
                    break;
                first = false;
                total += Body - PayloadStart(body);
            }
        }
        return total;
    }

    /// <summary>重编码画面的 PES：H.264/HEVC 缺少访问单元分隔符时补上（TS 要求），视频 PES 不声明长度。</summary>
    private byte[] BuildEncodedPes(ReadOnlySpan<byte> data, long pts, long dts)
    {
        ReadOnlySpan<byte> delimiter = h264 ? [0, 0, 0, 1, 0x09, 0xF0] : hevc ? [0, 0, 0, 1, 0x46, 0x01, 0x50] : [];
        if (delimiter.Length > 0 && StartsWithDelimiter(data))
            delimiter = [];
        var both = dts != pts && dts != long.MinValue;
        var header = new byte[both ? 19 : 14];
        header[2] = 1;
        header[3] = videoStreamId;
        header[6] = 0x84;
        header[7] = (byte)(both ? 0xC0 : 0x80);
        header[8] = (byte)(both ? 10 : 5);
        header[9] = (byte)(both ? 0x31 : 0x21);
        WriteTimestamp(header.AsSpan(9), pts);
        if (both)
        {
            header[14] = 0x11;
            WriteTimestamp(header.AsSpan(14), dts);
        }
        var result = new byte[header.Length + delimiter.Length + data.Length];
        header.CopyTo(result, 0);
        delimiter.CopyTo(result.AsSpan(header.Length));
        data.CopyTo(result.AsSpan(header.Length + delimiter.Length));
        return result;
    }

    private bool StartsWithDelimiter(ReadOnlySpan<byte> data)
    {
        var offset = data.Length >= 4 && data[..4].SequenceEqual(new byte[] { 0, 0, 0, 1 }) ? 4
            : data.Length >= 3 && data[..3].SequenceEqual(new byte[] { 0, 0, 1 }) ? 3 : -1;
        if (offset < 0 || offset >= data.Length)
            return false;
        return h264 ? (data[offset] & 0x1F) == 9 : (data[offset] >> 1 & 0x3F) == 35;
    }

    // ---------------- 节目表、透传与写出 ----------------

    private void RememberTable(int pid, bool pusi, ReadOnlySpan<byte> packet)
    {
        if (started)
            return;
        if (pusi)
            lastTables[pid] = [];
        if (lastTables.TryGetValue(pid, out var list))
            list.Add(packet.ToArray());
    }

    private PidState State(int pid)
    {
        if (!states.TryGetValue(pid, out var state))
            states[pid] = state = new PidState();
        return state;
    }

    private void Flush(List<byte[]> packets)
    {
        if (!started)
        {
            started = true;
            // 开头补最近一份节目表原包，CC 与之后的源节目表包连续。
            foreach (var (_, table) in lastTables)
                foreach (var packet in table)
                    Emit(packet);
        }
        foreach (var packet in packets)
        {
            if (packet.Length == layout.PacketSize && packet[layout.PrefixSize] == 0x47)
            {
                var body = packet.AsSpan(layout.PrefixSize, Body);
                var pid = ((body[1] & 0x1F) << 8) | body[2];
                var state = State(pid);
                if (state.Rebase || state.Offset != 0)
                {
                    var hasPayload = (body[3] & 0x10) != 0;
                    var cc = body[3] & 0x0F;
                    if (state.Rebase)
                    {
                        state.Offset = ((hasPayload ? state.LastOut + 1 : state.LastOut) - cc) & 0x0F;
                        state.Rebase = false;
                    }
                    body[3] = (byte)((body[3] & 0xF0) | ((cc + state.Offset) & 0x0F));
                }
                state.LastOut = body[3] & 0x0F;
                ShiftPcr(body);
                if ((body[1] & 0x40) != 0)
                    ShiftPesTimestamps(body);
                WriteArrival(packet);
            }
            Emit(packet);
        }
        packets.Clear();
    }

    private void WriteArrival(byte[] packet)
    {
        if (!layout.IsM2ts || packet.Length != layout.PacketSize)
            return;
        var header = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(packet);
        if (arrivalRebase)
        {
            // 后续片段的到达时间接续上一包，保留片段内的间隔与复制许可位。
            arrivalOffset = (lastArrival + 1 - (header & 0x3FFFFFFF)) & 0x3FFFFFFF;
            arrivalRebase = false;
        }
        var time = ((header & 0x3FFFFFFF) + arrivalOffset) & 0x3FFFFFFF;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet, (header & 0xC0000000) | time);
    }

    private uint lastArrival;

    private void EmitRaw(ReadOnlySpan<byte> data)
    {
        Flush(pending);
        Emit(data);
    }

    private void Emit(ReadOnlySpan<byte> data)
    {
        anyOutput = true;
        if (layout.IsM2ts && data.Length == layout.PacketSize)
            lastArrival = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data) & 0x3FFFFFFF;
        if (filter is not null)
        {
            // 节目过滤按完整的包处理；同步丢失的残余字节无法归属节目，不写出。
            if (data.Length == layout.PacketSize && data[layout.PrefixSize] == 0x47)
                filter.WriteFilteredPacket(data, 0);
            return;
        }
        if (writeLength + data.Length > writeBuffer.Length)
            Flush();
        data.CopyTo(writeBuffer.AsSpan(writeLength));
        writeLength += data.Length;
    }

    private void Flush()
    {
        if (writeLength == 0)
            return;
        output!.Write(writeBuffer, 0, writeLength);
        checkSession?.Append(writeBuffer.AsSpan(0, writeLength));
        bytesWritten += writeLength;
        writeLength = 0;
    }

    // ---------------- 读取源文件 ----------------

    private bool ReadPacket(long position, Span<byte> packet, out int length)
    {
        length = 0;
        if (position == long.MaxValue)
            return false;
        if (readStart < 0 || position < readStart || position + layout.PacketSize > readStart + readLength)
        {
            source.Position = position;
            readStart = position;
            readLength = 0;
            int read;
            while (readLength < readBuffer.Length && (read = source.Read(readBuffer, readLength, readBuffer.Length - readLength)) > 0)
                readLength += read;
        }
        var available = (int)Math.Min(layout.PacketSize, readStart + readLength - position);
        if (available <= 0)
            return false;
        readBuffer.AsSpan((int)(position - readStart), available).CopyTo(packet);
        length = available;
        return true;
    }

    private byte[] ReadBytes(long position, int count)
    {
        var result = new byte[count];
        source.Position = position;
        source.ReadExactly(result);
        readStart = -1;
        return result;
    }

    /// <summary>从 position 起找下一个连续同步的包起点，返回跳过的字节数；读到文件末尾时返回 0。</summary>
    private int Resync(long position)
    {
        Span<byte> window = new byte[layout.PacketSize * 8 + 4096];
        source.Position = position;
        var read = source.Read(window);
        readStart = -1;
        for (var offset = 1; offset + layout.PacketSize * 3 <= read; offset++)
        {
            var ok = true;
            for (var i = 0; i < 3 && ok; i++)
                ok = window[offset + layout.PrefixSize + i * layout.PacketSize] == 0x47;
            if (ok)
                return offset;
        }
        return read > 0 && read < window.Length ? read : 0;
    }
}
