using System;
using System.Collections.Generic;
using System.Linq;
using TSCutter.GUI.Services.TransportStream.Analysis;
using TSCutter.GUI.Utils.TransportStream.Packets;

namespace TSCutter.GUI.Services.Editing;

/// <summary>TS 导出输出的附加处理；默认都不启用，此时输出保持原有的逐字节结果。</summary>
[Flags]
public enum TsOutputProcessing
{
    None = 0,
    /// <summary>整体平移 PCR/OPCR/PTS/DTS，让文件的时间从 0 开始。</summary>
    ZeroStartTime = 1,
    /// <summary>删去 PID 0x1FFF 的空包。</summary>
    RemoveNullPackets = 2
}

/// <summary>
/// 对导出服务写出的最终 TS 字节逐包做附加处理，处理后的字节交给输出文件与导出检测。
/// <list type="bullet">
/// <item>去除空包：PID 0x1FFF 的包（M2TS 连同附加头）整包删去，其余包原样保留。</item>
/// <item>起始时间归零：先暂存开头一段输出（PCR 走过约 3 秒或暂存 32 MB，以先到者为准），按其中出现的 PCR、PTS、DTS
/// 求出每个时间基准的最小值作为平移量，再把整个文件的时间戳减去平移量（按 33 位取模，源中的回绕与之后输出的回绕都保持正确）。
/// 时间基准按节目的 PCR PID 区分：各节目的 PCR 与该节目各轨道的 PTS/DTS 使用同一平移量，PCR 与视频不在同一 PID 时也一致；
/// 时钟不同的多个节目各自从 0 开始。未列入节目表的 PID 及暂存结束后才出现的时间基准使用全部时间戳的最小值。
/// PCR 只改 33 位基数，9 位扩展与保留位不变；M2TS 附加头中的到达时间戳不变。</item>
/// </list>
/// 带传输错误标志的包、加扰包的 PES 头不改写；PES 头跨包时等后续分片到齐再改写，期间暂缓写出这些包之后的数据。
/// 智能剪辑的重编码段与复制段、多段合并的各段都经同一输出写出，统一平移后衔接关系不变。
/// </summary>
internal sealed class TsOutputPostProcessor
{
    private const int Body = TsUtil.TsPacketSize;
    private const long Wrap = 1L << 33;
    private const long HalfWrap = 1L << 32;
    private const int NullPid = 0x1FFF;
    internal const long LookaheadTicks = 3 * 90_000;
    internal const int LookaheadBytes = 32 << 20;
    // 跨包 PES 头等待后续分片的上限；超过时放弃改写该头，避免无限暂存。
    private const int MaxSplitHeaderWait = 1 << 20;
    private const int FlushThreshold = 1 << 20;

    private enum FieldKind : byte { Pcr, Opcr, Pes, SplitPes }

    private readonly struct Field(FieldKind kind, int pid, long position, long dtsPosition, SplitHeader? split)
    {
        public FieldKind Kind { get; } = kind;
        public int Pid { get; } = pid;
        /// <summary>PCR/OPCR 或 PTS 字段在输出中的绝对位置。</summary>
        public long Position { get; } = position;
        /// <summary>DTS 字段的绝对位置；没有 DTS 时为 -1。</summary>
        public long DtsPosition { get; } = dtsPosition;
        public SplitHeader? Split { get; } = split;
    }

    /// <summary>跨包的 PES 头：已取得的头字节，以及每个分片在输出中的位置。</summary>
    private sealed class SplitHeader(int pid, long packetPosition)
    {
        public int Pid { get; } = pid;
        public long PacketPosition { get; } = packetPosition;
        public byte[] Bytes { get; } = new byte[19];
        public int Count;
        public int LastContinuity = -1;
        public byte[] LastPacket { get; } = new byte[Body];
        public List<(long Position, int HeaderIndex, int Length)> Fragments { get; } = [];
        /// <summary>已按平移量改写了 <see cref="Bytes"/>，之后到来的重复分片直接写入。</summary>
        public bool Applied;
        public int Length => Count >= 9 && Bytes[7] >> 6 == 2 ? 14 : 19;
    }

    private readonly TsOutputProcessing options;
    private readonly int packetSize;
    private readonly int prefix;
    private readonly Action<byte[], int, int> sink;
    private readonly bool zero;
    private readonly bool removeNulls;

    // 待写出的字节：queue[head..tail) 对应输出中的绝对位置 queueBase + head 起。
    private byte[] queue = new byte[1 << 20];
    private int head;
    private int tail;
    private long queueBase;
    private readonly byte[] partial;
    private int partialLength;

    private bool decided;
    private readonly List<Field> deferred = [];
    private readonly Dictionary<int, SplitHeader> splits = [];
    // 刚取全的跨包头：该 PID 下一个包若是最后一个分片的重复包，同样改写。
    private readonly Dictionary<int, SplitHeader> completedSplits = [];
    private readonly Dictionary<int, long> groupOffsets = [];
    private long globalOffset;
    // 暂存期间各 PCR PID 的首个 PCR，用来判断暂存是否已足够。
    private readonly Dictionary<int, long> firstPcr = [];

    // 节目表：PAT 给出 PMT PID，PMT 给出各 PID 所属的时间基准（节目的 PCR PID）。
    private readonly TsPsiSectionAssembler patAssembler = new();
    private readonly Dictionary<int, TsPsiSectionAssembler> pmtAssemblers = [];
    private readonly int[] groupOfPid = new int[0x2000];
    private readonly bool[] carriesPcr = new bool[0x2000];

    public TsOutputPostProcessor(TsOutputProcessing options, int packetSize, Action<byte[], int, int> sink)
    {
        if (packetSize is not (Body or TsPacketLayout.M2tsPacketSize))
            throw new ArgumentOutOfRangeException(nameof(packetSize));
        this.options = options;
        this.packetSize = packetSize;
        prefix = packetSize - Body;
        this.sink = sink;
        zero = options.HasFlag(TsOutputProcessing.ZeroStartTime);
        removeNulls = options.HasFlag(TsOutputProcessing.RemoveNullPackets);
        partial = new byte[packetSize];
        Array.Fill(groupOfPid, -1);
    }

    public TsOutputProcessing Options => options;
    public long NullPacketsRemoved { get; private set; }
    public long TimestampsRewritten { get; private set; }
    /// <summary>各时间基准（PCR PID；节目没有 PCR 时为 0x2000 + 节目号）的平移量，确定之前为空。</summary>
    public IReadOnlyDictionary<int, long> GroupOffsets => groupOffsets;
    /// <summary>未列入节目表的 PID 使用的平移量。</summary>
    public long GlobalOffset => globalOffset;
    public bool OffsetsDecided => decided;

    public void Write(ReadOnlySpan<byte> data)
    {
        if (partialLength > 0)
        {
            var take = Math.Min(packetSize - partialLength, data.Length);
            data[..take].CopyTo(partial.AsSpan(partialLength));
            partialLength += take;
            data = data[take..];
            if (partialLength < packetSize)
                return;
            Process(partial);
            partialLength = 0;
        }
        while (data.Length >= packetSize)
        {
            Process(data[..packetSize]);
            data = data[packetSize..];
        }
        data.CopyTo(partial);
        partialLength = data.Length;
        if (zero && !decided && tail - head >= LookaheadBytes)
            Decide();
        if (tail - head >= FlushThreshold)
            FlushReady();
    }

    /// <summary>写出结束：确定平移量、放弃未到齐的跨包头，写出全部剩余字节（含不足一包的尾部，原样写出）。</summary>
    public void Complete()
    {
        if (zero && !decided)
            Decide();
        splits.Clear();
        completedSplits.Clear();
        if (partialLength > 0)
        {
            Append(partial.AsSpan(0, partialLength));
            partialLength = 0;
        }
        FlushReady();
    }

    private void Process(ReadOnlySpan<byte> packet)
    {
        var body = packet.Slice(prefix, Body);
        if (body[0] != TsUtil.TsSyncByte)
        {
            Append(packet);
            return;
        }
        var pid = ((body[1] & 0x1F) << 8) | body[2];
        if (removeNulls && pid == NullPid)
        {
            NullPacketsRemoved++;
            return;
        }
        var position = queueBase + tail;
        Append(packet);
        if (zero && pid != NullPid)
            Inspect(position + prefix, pid);
    }

    private void Append(ReadOnlySpan<byte> bytes)
    {
        if (tail + bytes.Length > queue.Length)
        {
            if (head > 0)
            {
                queue.AsSpan(head, tail - head).CopyTo(queue);
                queueBase += head;
                tail -= head;
                head = 0;
            }
            if (tail + bytes.Length > queue.Length)
                Array.Resize(ref queue, Math.Max(queue.Length * 2, tail + bytes.Length));
        }
        bytes.CopyTo(queue.AsSpan(tail));
        tail += bytes.Length;
    }

    private Span<byte> At(long position, int length) => queue.AsSpan((int)(position - queueBase), length);

    /// <summary>写出可以确定的部分：平移量未定时不写；有未到齐的跨包头时停在该头所在的包之前。</summary>
    private void FlushReady()
    {
        if (zero && !decided)
            return;
        // 后续分片迟迟不来（该 PID 不再出现）时放弃改写，不能让暂存无限增长。
        if (splits.Count > 0)
            foreach (var stale in splits.Values.Where(split => queueBase + tail - split.PacketPosition > MaxSplitHeaderWait).ToArray())
                AbandonSplit(stale.Pid);
        var limit = tail;
        foreach (var split in splits.Values)
            limit = Math.Min(limit, (int)(split.PacketPosition - queueBase));
        if (limit <= head)
            return;
        sink(queue, head, limit - head);
        head = limit;
        if (head == tail)
        {
            queueBase += tail;
            head = tail = 0;
        }
    }

    // ---- 时间戳 ----

    /// <param name="bodyPosition">包体（不含 M2TS 附加头）在输出中的绝对位置。</param>
    private void Inspect(long bodyPosition, int pid)
    {
        var body = At(bodyPosition, Body);
        // 带传输错误标志的包内容不可信，保持原样。
        if ((body[1] & 0x80) != 0)
        {
            AbandonSplit(pid);
            return;
        }
        var payloadStart = (body[1] & 0x40) != 0;
        var adaptation = (body[3] & 0x20) != 0;
        var hasPayload = (body[3] & 0x10) != 0;
        var scrambled = (body[3] & 0xC0) != 0;
        var payloadOffset = 4;
        if (adaptation)
        {
            var length = body[4];
            payloadOffset += 1 + length;
            if (length > 0 && payloadOffset <= Body)
                InspectAdaptation(bodyPosition, body, pid, length);
        }
        if (!hasPayload || payloadOffset >= Body)
            return;
        var payload = body[payloadOffset..];
        if (!scrambled && (pid == 0 || pmtAssemblers.ContainsKey(pid)))
        {
            ReadTables(pid, payload, payloadStart);
            return;
        }
        if (completedSplits.Remove(pid, out var done) && !scrambled && IsDuplicate(done, body))
        {
            var last = done.Fragments[^1];
            if (done.Applied)
                WriteFragment(done, bodyPosition + payloadOffset, last.HeaderIndex, last.Length);
            else
                done.Fragments.Add((bodyPosition + payloadOffset, last.HeaderIndex, last.Length));
            completedSplits[pid] = done;
            return;
        }
        if (splits.TryGetValue(pid, out var split))
        {
            // 头所在包的重复包也带起始标志，按重复包处理。
            if (!scrambled && (!payloadStart || IsDuplicate(split, body)))
            {
                ContinueSplit(split, body, bodyPosition + payloadOffset, payload);
                return;
            }
            AbandonSplit(pid);
        }
        if (!payloadStart || scrambled || pid < 0x10)
            return;
        InspectPesHeader(pid, bodyPosition - prefix, bodyPosition + payloadOffset, payload);
    }

    private void InspectAdaptation(long bodyPosition, Span<byte> body, int pid, int length)
    {
        var flags = body[5];
        var offset = 6;
        if ((flags & 0x10) != 0)
        {
            if (length < 7)
                return;
            carriesPcr[pid] = true;
            AddField(new Field(FieldKind.Pcr, pid, bodyPosition + offset, -1, null));
            if (!decided)
                ObservePcrForLookahead(pid, TsTimestampFieldCodec.ReadPcrBase(body.Slice(offset, 5)));
            offset += 6;
        }
        // OPCR 与 PCR 格式相同，随所在 PID 的时间基准一同平移。
        if ((flags & 0x08) != 0 && offset + 6 <= 5 + length)
            AddField(new Field(FieldKind.Opcr, pid, bodyPosition + offset, -1, null));
    }

    private void ObservePcrForLookahead(int pid, long pcr)
    {
        if (!firstPcr.TryGetValue(pid, out var first))
        {
            firstPcr[pid] = pcr;
            return;
        }
        var advance = Modulo(pcr - first);
        if (advance < HalfWrap && advance >= LookaheadTicks)
            Decide();
    }

    /// <summary>PES 头：起始码、带可选头的 stream_id、“10”标记位、PTS/DTS 标志与头长度都要符合，才视为可改写的时间戳。</summary>
    private void InspectPesHeader(int pid, long packetPosition, long payloadPosition, ReadOnlySpan<byte> payload)
    {
        var available = Math.Min(payload.Length, 19);
        var probe = new SplitHeader(pid, payloadPosition);
        payload[..available].CopyTo(probe.Bytes);
        probe.Count = available;
        var state = CheckHeader(probe);
        if (state < 0)
            return;
        if (state > 0)
        {
            var hasDts = probe.Length == 19;
            AddField(new Field(FieldKind.Pes, pid, payloadPosition + 9, hasDts ? payloadPosition + 14 : -1, null));
            return;
        }
        // 头跨包：记下首个分片，等后续分片到齐。
        var split = new SplitHeader(pid, packetPosition);
        payload[..available].CopyTo(split.Bytes);
        split.Count = available;
        split.Fragments.Add((payloadPosition, 0, available));
        var body = At(split.PacketPosition + prefix, Body);
        split.LastContinuity = body[3] & 0x0F;
        body.CopyTo(split.LastPacket);
        splits[pid] = split;
    }

    /// <returns>1：头完整且有效；0：有效但尚未取全；-1：不是可改写的 PES 头。</returns>
    private static int CheckHeader(SplitHeader header)
    {
        var bytes = header.Bytes;
        var count = header.Count;
        ReadOnlySpan<byte> startCode = [0, 0, 1];
        if (!bytes.AsSpan(0, Math.Min(count, 3)).SequenceEqual(startCode[..Math.Min(count, 3)]))
            return -1;
        if (count >= 4 && bytes[3] is < 0xBC or 0xBC or 0xBE or 0xBF or 0xF0 or 0xF1 or 0xF2 or 0xF8 or 0xFF)
            return -1;
        if (count < 9)
            return 0;
        if ((bytes[6] & 0xC0) != 0x80 || bytes[7] >> 6 is not (2 or 3) || bytes[8] < header.Length - 9)
            return -1;
        return count >= header.Length ? 1 : 0;
    }

    private void ContinueSplit(SplitHeader split, ReadOnlySpan<byte> body, long payloadPosition, ReadOnlySpan<byte> payload)
    {
        var continuity = body[3] & 0x0F;
        // 重复包与上一分片内容相同，改写时一并改写。
        if (IsDuplicate(split, body))
        {
            var last = split.Fragments[^1];
            split.Fragments.Add((payloadPosition, last.HeaderIndex, last.Length));
            return;
        }
        var discontinuity = (body[3] & 0x20) != 0 && body[4] > 0 && (body[5] & 0x80) != 0;
        if (!discontinuity && continuity != ((split.LastContinuity + 1) & 0x0F))
        {
            AbandonSplit(split.Pid);
            return;
        }
        split.LastContinuity = continuity;
        body.CopyTo(split.LastPacket);
        var take = Math.Min(19 - split.Count, payload.Length);
        payload[..take].CopyTo(split.Bytes.AsSpan(split.Count));
        split.Fragments.Add((payloadPosition, split.Count, take));
        split.Count += take;
        var state = CheckHeader(split);
        if (state < 0)
        {
            AbandonSplit(split.Pid);
            return;
        }
        if (state == 0)
        {
            if (queueBase + tail - split.PacketPosition > MaxSplitHeaderWait)
                AbandonSplit(split.Pid);
            return;
        }
        splits.Remove(split.Pid);
        completedSplits[split.Pid] = split;
        AddField(new Field(FieldKind.SplitPes, split.Pid, -1, -1, split));
    }

    private static bool IsDuplicate(SplitHeader split, ReadOnlySpan<byte> body) =>
        (body[3] & 0x0F) == split.LastContinuity && body.SequenceEqual(split.LastPacket);

    private void AbandonSplit(int pid)
    {
        splits.Remove(pid);
        completedSplits.Remove(pid);
    }

    private void AddField(Field field)
    {
        if (decided)
            Apply(field);
        else
            deferred.Add(field);
    }

    /// <summary>按暂存部分的时间戳确定各时间基准的平移量，并改写已暂存的字段。</summary>
    private void Decide()
    {
        decided = true;
        var groups = new Dictionary<int, (long Reference, long Minimum)>();
        long? globalReference = null;
        long globalMinimum = 0;
        Span<long> values = stackalloc long[2];
        foreach (var field in deferred)
        {
            // OPCR 保存原始复用时钟，只跟随平移，不参与当前输出的归零基准。
            if (field.Kind == FieldKind.Opcr)
                continue;
            var group = GroupOf(field);
            var count = Values(field, values);
            foreach (var value in values[..count])
            {
                if (group >= 0)
                {
                    if (groups.TryGetValue(group, out var range))
                        groups[group] = (range.Reference, Math.Min(range.Minimum, Delta(value, range.Reference)));
                    else
                        groups[group] = (value, 0);
                }
                if (globalReference is not { } reference)
                    globalReference = value;
                else
                    globalMinimum = Math.Min(globalMinimum, Delta(value, reference));
            }
        }
        foreach (var (group, range) in groups)
            groupOffsets[group] = Modulo(range.Reference + range.Minimum);
        globalOffset = globalReference is { } start ? Modulo(start + globalMinimum) : 0;
        foreach (var field in deferred)
            Apply(field);
        deferred.Clear();
        firstPcr.Clear();
    }

    /// <summary>字段所属的时间基准：PCR 属于所在 PID；PES 和 OPCR 按节目表归入节目的 PCR PID，未列入节目表但本身载有 PCR 的 PID 归入自身。</summary>
    private int GroupOf(Field field)
    {
        if (field.Kind == FieldKind.Pcr)
            return field.Pid;
        var group = groupOfPid[field.Pid];
        return group >= 0 ? group : carriesPcr[field.Pid] ? field.Pid : -1;
    }

    private long OffsetOf(Field field) =>
        GroupOf(field) is >= 0 and var group && groupOffsets.TryGetValue(group, out var offset) ? offset : globalOffset;

    /// <summary>读出字段的原值（PCR 基数，或 PTS 与可能有的 DTS），返回个数。</summary>
    private int Values(Field field, Span<long> values)
    {
        switch (field.Kind)
        {
            case FieldKind.Pcr:
                values[0] = TsTimestampFieldCodec.ReadPcrBase(At(field.Position, 5));
                return 1;
            case FieldKind.Pes:
                values[0] = TsTimestampFieldCodec.ReadPesTimestamp(At(field.Position, 5));
                if (field.DtsPosition < 0)
                    return 1;
                values[1] = TsTimestampFieldCodec.ReadPesTimestamp(At(field.DtsPosition, 5));
                return 2;
            default:
                var bytes = field.Split!.Bytes;
                values[0] = TsTimestampFieldCodec.ReadPesTimestamp(bytes.AsSpan(9, 5));
                if (field.Split.Length != 19)
                    return 1;
                values[1] = TsTimestampFieldCodec.ReadPesTimestamp(bytes.AsSpan(14, 5));
                return 2;
        }
    }

    private void Apply(Field field)
    {
        if (field.Split is { } applied)
            applied.Applied = true;
        var offset = OffsetOf(field);
        if (offset == 0)
            return;
        switch (field.Kind)
        {
            case FieldKind.Pcr:
            case FieldKind.Opcr:
            {
                var value = At(field.Position, 5);
                TsTimestampFieldCodec.WritePcrBase(value, TsTimestampFieldCodec.ReadPcrBase(value) - offset);
                TimestampsRewritten++;
                break;
            }
            case FieldKind.Pes:
            {
                var start = field.Position;
                var end = field.DtsPosition >= 0 ? field.DtsPosition + 5 : start + 5;
                TimestampsRewritten += TsTimestampFieldCodec.RewritePesTimestamps(
                    At(start, (int)(end - start)), -offset, 0, field.DtsPosition >= 0 ? (int)(field.DtsPosition - start) : -1);
                break;
            }
            default:
            {
                var split = field.Split!;
                TimestampsRewritten += TsTimestampFieldCodec.RewritePesTimestamps(
                    split.Bytes.AsSpan(0, split.Length), -offset, 9, split.Length == 19 ? 14 : -1);
                foreach (var (position, headerIndex, length) in split.Fragments)
                    WriteFragment(split, position, headerIndex, length);
                break;
            }
        }
    }

    /// <summary>把改写后的头字节写回一个分片（只写时间戳所在的第 9 字节之后）；已写出的分片不再改写。</summary>
    private void WriteFragment(SplitHeader split, long position, int headerIndex, int length)
    {
        if (position < queueBase + head)
            return;
        var target = At(position, length);
        for (var index = Math.Max(9, headerIndex); index < headerIndex + length; index++)
            target[index - headerIndex] = split.Bytes[index];
    }

    // ---- 节目表 ----

    private void ReadTables(int pid, ReadOnlySpan<byte> payload, bool payloadStart)
    {
        if (pid == 0)
            patAssembler.Push(payload, payloadStart, OnPat);
        else
            pmtAssemblers[pid].Push(payload, payloadStart, OnPmt);
    }

    private void OnPat(ReadOnlySpan<byte> section)
    {
        if (section.Length < 12 || section[0] != 0x00 || !TsDvbTimeTableParser.HasValidMpegCrc(section))
            return;
        for (var offset = 8; offset + 4 <= section.Length - 4; offset += 4)
        {
            var program = (section[offset] << 8) | section[offset + 1];
            var pmtPid = ((section[offset + 2] & 0x1F) << 8) | section[offset + 3];
            if (program != 0 && pmtPid is > 0 and < NullPid && !pmtAssemblers.ContainsKey(pmtPid))
                pmtAssemblers[pmtPid] = new TsPsiSectionAssembler();
        }
    }

    private void OnPmt(ReadOnlySpan<byte> section)
    {
        if (section.Length < 16 || section[0] != 0x02 || !TsDvbTimeTableParser.HasValidMpegCrc(section))
            return;
        var program = (section[3] << 8) | section[4];
        var pcrPid = ((section[8] & 0x1F) << 8) | section[9];
        var group = pcrPid == NullPid ? 0x2000 + program : pcrPid;
        if (pcrPid != NullPid && groupOfPid[pcrPid] < 0)
            groupOfPid[pcrPid] = group;
        var offset = 12 + (((section[10] & 0x0F) << 8) | section[11]);
        var end = section.Length - 4;
        while (offset + 5 <= end)
        {
            var esPid = ((section[offset + 1] & 0x1F) << 8) | section[offset + 2];
            var infoLength = ((section[offset + 3] & 0x0F) << 8) | section[offset + 4];
            // 同一 PID 属于多个节目时沿用最先确定的时间基准。
            if (groupOfPid[esPid] < 0)
                groupOfPid[esPid] = group;
            offset += 5 + infoLength;
        }
    }

    private static long Modulo(long value)
    {
        value %= Wrap;
        return value < 0 ? value + Wrap : value;
    }

    /// <summary>value 相对 reference 的有符号差（按 33 位回绕取最近的一侧）。</summary>
    private static long Delta(long value, long reference)
    {
        var delta = Modulo(value - reference);
        return delta >= HalfWrap ? delta - Wrap : delta;
    }
}
