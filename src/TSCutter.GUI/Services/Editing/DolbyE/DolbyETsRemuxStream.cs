using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.Models.Media;
using TSCutter.GUI.Services.TransportStream.Analysis;
using TSCutter.GUI.Utils.Media.DolbyE;
using TSCutter.GUI.Utils.TransportStream.Packets;

namespace TSCutter.GUI.Services.Editing.DolbyE;

/// <summary>
/// 一次导出的 TS 重组设置与结果：转码杜比 E 轨、删去未选的轨道。导出成功后提交独立的 FLAC 文件，失败时丢弃。
/// </summary>
internal sealed class DolbyERemuxSetup(DolbyEExportPlan? plan, string sourcePath, string outputPath, ExportTsCheckSession? session,
    IReadOnlyCollection<int>? excludedPids = null)
{
    public DolbyEExportPlan Plan { get; } = plan ?? new DolbyEExportPlan();
    /// <summary>不输出的轨道（PID）：这些包从输出中删去，节目表中的对应条目一并去掉。</summary>
    public IReadOnlySet<int> ExcludedPids { get; } = new HashSet<int>(excludedPids ?? []);
    /// <summary>实际删去了未选轨道的包或节目表条目。</summary>
    public bool TracksRemoved { get; internal set; }
    /// <summary>导出的源文件：开头的节目表决定杜比 E 轨所在的节目与可用的 PID。</summary>
    public string SourcePath { get; } = sourcePath;
    /// <summary>最终输出路径：独立 FLAC 文件按它命名（导出服务可能先写临时文件）。</summary>
    public string OutputPath { get; } = outputPath;
    public ExportTsCheckSession? Session { get; } = session;
    /// <summary>经工厂打开的输出流个数；为 0 说明这条导出路径没有经过重组。</summary>
    public int StreamCount { get; internal set; }
    /// <summary>节目表中找到了杜比 E 轨并加入了新音轨。</summary>
    public bool Changed { get; internal set; }
    public int DecodedFrames { get; internal set; }
    /// <summary>写出结束时（释放输出流时）发生的错误。</summary>
    public Exception? Error { get; internal set; }
    internal List<(string Temporary, string Final)> SideFiles { get; } = [];

    /// <summary>导出成功：把独立 FLAC 文件改成正式文件名。</summary>
    public IReadOnlyList<string> Commit()
    {
        var result = new List<string>();
        foreach (var (temporary, final) in SideFiles)
        {
            File.Move(temporary, final, overwrite: true);
            result.Add(final);
        }
        SideFiles.Clear();
        return result;
    }

    /// <summary>导出失败或取消：删除未提交的独立 FLAC 文件。</summary>
    public void Abort()
    {
        foreach (var (temporary, _) in SideFiles)
            DolbyETsRemuxStream.TryDelete(temporary);
        SideFiles.Clear();
    }
}

/// <summary>
/// TS 导出服务打开输出文件的入口。没有杜比 E 转码时返回普通文件流，参数与原来完全相同；
/// 导出窗口设置了转码计划或轨道选择时返回重组流，导出服务的写出代码不变。
/// </summary>
internal static class TsOutputFile
{
    private static readonly AsyncLocal<DolbyERemuxSetup?> CurrentSetup = new();

    /// <summary>当前异步流程中的重组设置；在导出窗口中设置，随调用传到导出服务。</summary>
    public static DolbyERemuxSetup? Current
    {
        get => CurrentSetup.Value;
        set => CurrentSetup.Value = value;
    }

    public static FileStream Create(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize,
        FileOptions options)
    {
        if (CurrentSetup.Value is not { } setup)
            return new FileStream(path, mode, access, share, bufferSize, options);
        if (setup.StreamCount++ > 0)
            throw new InvalidOperationException("Dolby E rebuilding supports a single output stream per export.");
        return new DolbyETsRemuxStream(path, mode, access, share, bufferSize, options, setup);
    }
}

/// <summary>
/// 写出时重组杜比 E 音轨的输出流：导出服务写入的 TS 字节逐包处理，除下面几类包外全部原样写出，
/// 保留原有的传输错误（CC 跳号、TEI、重复包、时间戳与 PCR 跳变）：
/// <list type="bullet">
/// <item>含杜比 E 轨的 PMT：只换负载，保留各包包头（含 TEI 与 CC）和适配字段；新节目表变长时在后面补包、变短时去掉多余的包，
/// 该 PID 之后的 CC 按偏移接续，原有的跳号不变。CRC 错误、结构不完整的 PMT 原样保留。</item>
/// <item>杜比 E 包：保留原轨时原样复制；替换时由新音轨的包依次占用它们的位置，多余的位置删去，
/// 载有 PCR 的包改为只含适配字段的包，PCR 原样保留。</item>
/// <item>新音轨：每个编码帧一个 PES，放在与其时间对应的杜比 E PES 之前；新 PID 的 CC 从 0 连续。</item>
/// <item>未选的轨道：包全部删去，PMT 中的条目一并去掉；若它承载节目的 PCR，带 PCR 的包改为只含适配字段的包。</item>
/// </list>
/// 杜比 E 包写入时即解码、编码。新音轨要比对应的杜比 E 帧提前放入，因此输出暂存到解码领先若干个杜比 E 帧后再写出，
/// 暂存量有上限。损坏或缺失的杜比 E 帧在新音轨中表现为同样的间隙。检测会话收到的是最终写出的字节。
/// </summary>
internal sealed class DolbyETsRemuxStream : FileStream
{
    private const int PrescanBytes = 64 << 20;
    // 写出某个杜比 E PES 前，解码至少要领先这么多个 PES，确保编码器延迟内的帧都已产生。
    private const int LookaheadPes = 8;
    // 暂存超过这个量（杜比 E 长时间中断）时不再等待解码，按已有的编码帧写出。
    private const int MaxHeldBytes = 64 << 20;
    private const int PacketBodySize = TsUtil.TsPacketSize;

    private sealed record EsEntry(int StreamType, int Pid, byte[] Info);

    private sealed record PmtInfo(int ProgramNumber, int PcrPid, IReadOnlyList<EsEntry> Streams);

    private sealed class OutputStream(int pid, int streamType, byte[] descriptors, byte streamId)
    {
        public int Pid { get; } = pid;
        public int StreamType { get; } = streamType;
        public byte[] Descriptors { get; } = descriptors;
        public byte StreamId { get; } = streamId;
        public int Continuity { get; set; }
    }

    private sealed class Track
    {
        public required int Pid { get; init; }
        public required int ProgramConfig { get; init; }
        public required bool KeepOriginal { get; init; }
        public required bool IsPcrPid { get; init; }
        public required List<DolbyEOutputChain> Chains { get; init; }
        // 按输出序号（Chains 下标）对应的 TS 输出；FLAC 独立文件没有。
        public required Dictionary<int, OutputStream> Streams { get; init; }
        public required List<(string Temporary, string Final)> SideFiles { get; init; }
        public DolbyETrackTranscoder? Transcoder { get; set; }
        // 解码端状态。
        public PesAssembler Assembler { get; } = new();
        public Queue<(DolbyEEncodedUnit Unit, int Slot)> Units { get; } = new();
        public int ProcessedIndex = -1;
        // 写出端状态。
        public int PesIndex = -1;
        public byte[]? LastPacket;
        public Queue<byte[]> Pending { get; } = new();
    }

    private readonly DolbyERemuxSetup setup;
    private TsPacketLayout layout;
    private List<Track> tracks = [];
    private Dictionary<int, Track> byPid = [];
    private PmtRewriter? pmt;
    private PacketWriter? writer;
    // 各节目的 PCR PID：删去的轨道若承载 PCR，PCR 仍要保留。
    private readonly HashSet<int> pcrPids = [];
    // 按 PID 索引的未选轨道表：逐包判断，比查集合快。
    private readonly bool[] excluded = new bool[0x2000];
    private bool initialized;
    private bool active;
    private bool finishing;
    private bool closed;
    // 未凑满一个包的输入字节。
    private byte[] partial = [];
    private int partialLength;
    // 暂存的包（环形队列，包长固定）。
    private byte[] held = [];
    private int heldHead;
    private int heldCount;

    internal DolbyETsRemuxStream(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize,
        FileOptions options, DolbyERemuxSetup setup) : base(path, mode, access, share, bufferSize, options)
    {
        this.setup = setup;
    }

    public override bool CanSeek => false;

    public override long Position
    {
        get => base.Position;
        set => throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => WriteCore(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer) => WriteCore(buffer);

    public override void WriteByte(byte value) => WriteCore([value]);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WriteCore(buffer.AsSpan(offset, count));
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WriteCore(buffer.Span);
        return ValueTask.CompletedTask;
    }

    // 刷新只写出已确定的部分；暂存的包要等解码领先或写出结束。
    public override void Flush() => Flush(false);

    public override void Flush(bool flushToDisk)
    {
        writer?.Flush();
        base.Flush(flushToDisk);
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        writer?.Flush();
        return base.FlushAsync(cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Complete();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        Complete();
        return base.DisposeAsync();
    }

    /// <summary>写出结束：冲出解码与编码器，写出全部暂存的包与剩余的新音轨。错误记入设置，不在释放时抛出。</summary>
    private void Complete()
    {
        if (closed)
            return;
        closed = true;
        try
        {
            if (active && setup.Error is null)
            {
                foreach (var track in tracks)
                {
                    if (track.Assembler.Index >= 0)
                        Deliver(track);
                    track.Transcoder!.Finish((unit, slot) => track.Units.Enqueue((unit, slot)));
                    setup.DecodedFrames += track.Transcoder.DecodedFrames;
                }
                finishing = true;
                Drain();
                pmt!.FlushAll(writer!);
                foreach (var track in tracks)
                {
                    FlushPending(track, writer!, null);
                    EnqueueUnits(track, int.MaxValue);
                    FlushPending(track, writer!, null);
                }
                if (partialLength > 0)
                    writer!.WriteRaw(partial.AsSpan(0, partialLength));
                writer!.Flush();
                // 与其他导出路径一样只交给系统缓存，不强制落盘。
                base.Flush(false);
                setup.Changed = tracks.Count > 0;
            }
        }
        catch (Exception exception)
        {
            setup.Error ??= exception;
        }
        finally
        {
            foreach (var track in tracks)
            {
                track.Transcoder?.Dispose();
                if (track.Transcoder is null)
                    foreach (var chain in track.Chains)
                        chain.Dispose();
            }
            tracks = [];
        }
    }

    private void WriteCore(ReadOnlySpan<byte> data)
    {
        if (!initialized)
            Initialize();
        if (!active)
        {
            Emit(data);
            return;
        }
        try
        {
            var size = layout.PacketSize;
            if (partialLength > 0)
            {
                var take = Math.Min(size - partialLength, data.Length);
                data[..take].CopyTo(partial.AsSpan(partialLength));
                partialLength += take;
                data = data[take..];
                if (partialLength < size)
                    return;
                Process(partial);
                partialLength = 0;
            }
            while (data.Length >= size)
            {
                // 只删轨道时，连续的普通包（不删去、不是节目表）整块写出。
                var run = tracks.Count == 0 ? PlainRun(data) : 0;
                if (run > 0)
                {
                    writer!.WriteRun(data[..run]);
                    data = data[run..];
                    continue;
                }
                Process(data[..size]);
                data = data[size..];
            }
            data.CopyTo(partial);
            partialLength = data.Length;
        }
        catch (Exception exception)
        {
            setup.Error ??= exception;
            throw;
        }
    }

    /// <summary>开头连续可原样写出的包的总长度：同步字节正确、不是未选轨道、不是 PAT/PMT。</summary>
    private int PlainRun(ReadOnlySpan<byte> data)
    {
        var size = layout.PacketSize;
        var run = 0;
        while (run + size <= data.Length)
        {
            var body = layout.GetPacketBody(data.Slice(run, size));
            var pid = ((body[1] & 0x1F) << 8) | body[2];
            if (body[0] != TsUtil.TsSyncByte || pid == 0 || excluded[pid] || pmt!.IsPmtPid(pid))
                break;
            run += size;
        }
        return run;
    }

    // 派生的文件流中，基类的 Span 重载会转回本类覆盖的数组重载；写入基流只能用数组重载。
    private void Emit(ReadOnlySpan<byte> data)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(data.Length);
        try
        {
            data.CopyTo(buffer);
            EmitBlock(buffer, data.Length);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void EmitBlock(byte[] buffer, int count)
    {
        base.Write(buffer, 0, count);
        setup.Session?.Append(buffer.AsSpan(0, count));
    }

    /// <summary>
    /// 首次写入时读取源文件开头的节目表，为每条要转码的杜比 E 轨准备输出；既没有要转码的轨道、也没有要删去的轨道时原样写出。
    /// </summary>
    private void Initialize()
    {
        initialized = true;
        try
        {
            layout = TsPacketLayout.Detect(setup.SourcePath);
            layout.RequireSupported();
            // 未选的杜比 E 轨整条删去，不再转码。
            var work = setup.Plan.Tracks.Where(track => track.HasWork && !setup.ExcludedPids.Contains(track.Pid)).ToArray();
            // 只删轨道时不分配新 PID，读到全部节目表即可停止预扫描。
            var programs = Prescan(setup.SourcePath, layout, CancellationToken.None, work.Length > 0, out var usedPids,
                out var pmtPids);
            pcrPids.UnionWith(programs.Values.Select(program => program.PcrPid));
            foreach (var pid in setup.ExcludedPids.Where(pid => pid is >= 0 and < 0x2000))
                excluded[pid] = true;
            var atsc = programs.Values.Any(program => program.Streams.Any(stream => stream.StreamType is 0x81 or 0x87));
            foreach (var settings in work)
            {
                var containing = programs.Values.Where(program => program.Streams.Any(stream => stream.Pid == settings.Pid)).ToArray();
                if (containing.Length == 0)
                    continue;
                var track = CreateTrack(settings, containing, usedPids, atsc, setup.OutputPath, work.Length > 1);
                tracks.Add(track);
                setup.SideFiles.AddRange(track.SideFiles);
                track.Transcoder = new DolbyETrackTranscoder(track.Chains);
            }
            if (tracks.Count == 0 && setup.ExcludedPids.Count == 0)
                return;
            byPid = tracks.ToDictionary(track => track.Pid);
            pmt = new PmtRewriter(tracks, setup.ExcludedPids, pcrPids, layout, pmtPids, () => setup.TracksRemoved = true);
            writer = new PacketWriter(EmitBlock, layout);
            partial = new byte[layout.PacketSize];
            // 只删轨道时逐包直接写出，不需要暂存。
            if (tracks.Count > 0)
                held = new byte[layout.PacketSize * 4096];
            active = true;
        }
        catch (Exception exception)
        {
            setup.Error ??= exception;
            throw;
        }
    }

    // ---- 解码端：包写入时立即组装 PES 并解码 ----

    private void Process(ReadOnlySpan<byte> packet)
    {
        // 没有要转码的杜比 E 轨时不必等待解码，直接写出。
        if (tracks.Count == 0)
        {
            WritePacket(packet);
            return;
        }
        var body = layout.GetPacketBody(packet);
        if (body[0] == TsUtil.TsSyncByte && byPid.TryGetValue(((body[1] & 0x1F) << 8) | body[2], out var track))
        {
            var assembler = track.Assembler;
            if (!assembler.IsDuplicate(body))
            {
                if ((body[1] & 0x40) != 0 && TryGetPayloadOffset(body, out _))
                {
                    if (assembler.Index >= 0)
                        Deliver(track);
                    assembler.Start();
                }
                assembler.Append(body);
            }
        }
        Hold(packet);
        Drain();
    }

    private static void Deliver(Track track)
    {
        var assembler = track.Assembler;
        if (assembler.TryGetPayload(out var payload, out var pts))
            track.Transcoder!.Feed(payload, pts, assembler.Index, (unit, slot) => track.Units.Enqueue((unit, slot)));
        track.ProcessedIndex = assembler.Index;
    }

    private void Hold(ReadOnlySpan<byte> packet)
    {
        var size = layout.PacketSize;
        var capacity = held.Length / size;
        if (heldCount == capacity)
        {
            var grown = new byte[held.Length * 2];
            for (var i = 0; i < heldCount; i++)
                held.AsSpan((heldHead + i) % capacity * size, size).CopyTo(grown.AsSpan(i * size));
            held = grown;
            heldHead = 0;
            capacity *= 2;
        }
        packet.CopyTo(held.AsSpan((heldHead + heldCount) % capacity * size, size));
        heldCount++;
    }

    // ---- 写出端：按顺序处理暂存的包，遇到还没解码到的杜比 E PES 起点就停下 ----

    private void Drain()
    {
        var size = layout.PacketSize;
        while (heldCount > 0)
        {
            var capacity = held.Length / size;
            var packet = held.AsSpan(heldHead * size, size);
            if (!finishing && heldCount * (long)size < MaxHeldBytes && !Ready(packet))
                return;
            WritePacket(packet);
            heldHead = (heldHead + 1) % capacity;
            heldCount--;
        }
    }

    /// <summary>杜比 E PES 起点要等解码领先到所需的位置；其他包随时可以写出。</summary>
    private bool Ready(ReadOnlySpan<byte> packet)
    {
        var body = layout.GetPacketBody(packet);
        if (body[0] != TsUtil.TsSyncByte || !byPid.TryGetValue(((body[1] & 0x1F) << 8) | body[2], out var track) ||
            (body[1] & 0x40) == 0 || !TryGetPayloadOffset(body, out _) || IsDuplicate(track, body))
            return true;
        return track.ProcessedIndex >= track.PesIndex + 1 + Ahead(track) + LookaheadPes;
    }

    private void WritePacket(ReadOnlySpan<byte> packet)
    {
        var body = layout.GetPacketBody(packet);
        if (body[0] != TsUtil.TsSyncByte)
        {
            writer!.WriteRaw(packet);
            return;
        }
        var pid = ((body[1] & 0x1F) << 8) | body[2];
        if (excluded[pid])
        {
            setup.TracksRemoved = true;
            // 未选的轨道：只保留它承载的 PCR。
            if (pcrPids.Contains(pid) && HasPcr(body))
                writer!.WriteAdaptationOnly(packet, layout);
        }
        else if (byPid.TryGetValue(pid, out var track))
            WriteDolbyEPacket(track, packet);
        else if (pmt!.Handles(pid, body))
            pmt.Push(pid, packet, writer!);
        else
            writer!.WriteRaw(packet);
    }

    // 原轨 PES 有时只比 PTS 早十几毫秒到达。新包提前一个 PES 放入：保留原轨时插在 PES 起点之前；
    // 替换时新包分散在原包位置上，后面的包到得较晚，再提前一个 PES，保证每个新包都在其 PTS 之前到达。
    private static int Ahead(Track track) => track.KeepOriginal ? 1 : 2;

    private static bool IsDuplicate(Track track, ReadOnlySpan<byte> body) =>
        track.LastPacket is not null && (body[3] & 0x10) != 0 && body.SequenceEqual(track.LastPacket);

    private void WriteDolbyEPacket(Track track, ReadOnlySpan<byte> packet)
    {
        var body = layout.GetPacketBody(packet);
        var duplicate = IsDuplicate(track, body);
        track.LastPacket ??= new byte[PacketBodySize];
        body.CopyTo(track.LastPacket);
        if (!duplicate && (body[1] & 0x40) != 0 && TryGetPayloadOffset(body, out _))
        {
            track.PesIndex++;
            // 上一个 PES 位置没用完的新包先补在这里，再取本 PES 时段的编码帧。
            FlushPending(track, writer!, packet);
            EnqueueUnits(track, track.PesIndex + Ahead(track));
            if (track.KeepOriginal)
                FlushPending(track, writer!, packet);
        }
        if (track.KeepOriginal)
        {
            writer!.WriteRaw(packet);
            return;
        }
        // 替换：PCR 原样保留在只含适配字段的包中；其余位置由新包占用，没有新包时删去。
        if (track.IsPcrPid && HasPcr(body))
        {
            writer!.WriteAdaptationOnly(packet, layout);
            return;
        }
        if (duplicate)
            return;
        if (track.Pending.Count > 0)
            writer!.WriteBody(track.Pending.Dequeue(), packet);
    }

    private static void FlushPending(Track track, PacketWriter writer, ReadOnlySpan<byte> reference)
    {
        while (track.Pending.Count > 0)
            writer.WriteBody(track.Pending.Dequeue(), reference);
    }

    private static void EnqueueUnits(Track track, int slot)
    {
        while (track.Units.Count > 0 && track.Units.Peek().Slot <= slot)
        {
            var (unit, _) = track.Units.Dequeue();
            if (!track.Streams.TryGetValue(unit.OutputIndex, out var stream))
                continue;
            foreach (var body in Packetize(stream, unit))
                track.Pending.Enqueue(body);
        }
    }

    private static Track CreateTrack(DolbyETrackSettings settings, IReadOnlyList<PmtInfo> containing, HashSet<int> usedPids,
        bool atsc, string outputPath, bool multipleTracks)
    {
        var isPcr = containing.Any(program => program.PcrPid == settings.Pid);
        var chains = new List<DolbyEOutputChain>();
        var streams = new Dictionary<int, OutputStream>();
        var sideFiles = new List<(string, string)>();
        var reuseOriginalPid = !settings.KeepOriginal && !isPcr;
        var baseName = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!,
            Path.GetFileNameWithoutExtension(outputPath)) + (multipleTracks ? $".pid{settings.Pid:X4}" : "");
        var flacNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var output in settings.Outputs.Where(output => output.Enabled))
            {
                // 输出声道按设置时读到的声道组配置确定；片中配置变化时由重采样转换到这个布局。
                var program = ProgramTemplate(settings.ProgramConfig, output.Program);
                string? flacPath = null;
                if (output.Codec == DolbyECodec.Flac)
                {
                    var name = $"{baseName}.P{output.Program}.flac";
                    for (var n = 2; !flacNames.Add(name); n++)
                        name = $"{baseName}.P{output.Program}-{n}.flac";
                    flacPath = Path.Combine(Path.GetDirectoryName(name)!, $".tscutter-{Guid.NewGuid():N}.flac.tmp");
                    sideFiles.Add((flacPath, name));
                }
                var index = chains.Count;
                chains.Add(new DolbyEOutputChain(output, index, program, flacPath));
                if (flacPath is not null)
                    continue;
                int pid;
                if (reuseOriginalPid)
                {
                    pid = settings.Pid;
                    reuseOriginalPid = false;
                }
                else
                    pid = AllocatePid(usedPids, settings.Pid);
                streams[index] = CreateStream(output, pid, atsc);
            }
            return new Track
            {
                Pid = settings.Pid, ProgramConfig = settings.ProgramConfig, KeepOriginal = settings.KeepOriginal, IsPcrPid = isPcr,
                Chains = chains, Streams = streams, SideFiles = sideFiles
            };
        }
        catch
        {
            foreach (var chain in chains)
                chain.Dispose();
            throw;
        }
    }

    private static DolbyEProgram ProgramTemplate(int programConfig, int number)
    {
        var programs = DolbyEConfiguration.GetPrograms(programConfig);
        return number >= 1 && number <= programs.Count
            ? programs[number - 1]
            : new DolbyEProgram(number, 1, DolbyEProgramKind.Stereo);
    }

    private static OutputStream CreateStream(DolbyEOutput output, int pid, bool atsc)
    {
        var descriptors = new List<byte>();
        int streamType;
        byte streamId = 0xBD;
        switch (output.Codec)
        {
            case DolbyECodec.Ac3:
                streamType = atsc ? 0x81 : 0x06;
                descriptors.AddRange(atsc ? [0x05, 4, (byte)'A', (byte)'C', (byte)'-', (byte)'3'] : [0x6A, 1, 0x00]);
                break;
            case DolbyECodec.Eac3:
                streamType = atsc ? 0x87 : 0x06;
                descriptors.AddRange(atsc ? [0x05, 4, (byte)'E', (byte)'A', (byte)'C', (byte)'3'] : [0x7A, 1, 0x00]);
                break;
            case DolbyECodec.Aac:
                streamType = 0x0F;
                streamId = 0xC0;
                break;
            default:
                streamType = 0x06;
                descriptors.AddRange([0x05, 4, (byte)'B', (byte)'S', (byte)'S', (byte)'D']);
                break;
        }
        if (output.Language.Length == 3)
        {
            descriptors.AddRange([0x0A, 4]);
            descriptors.AddRange(Encoding.ASCII.GetBytes(output.Language));
            descriptors.Add(0x00);
        }
        return new OutputStream(pid, streamType, descriptors.ToArray(), streamId);
    }

    private static int AllocatePid(HashSet<int> used, int near)
    {
        for (var pid = near + 1; pid <= TsPidAllocator.LastPid; pid++)
            if (used.Add(pid))
                return pid;
        if (TsPidAllocator.TryTakeNext(used, out var next))
            return next;
        throw new InvalidOperationException("No free PID is available for the transcoded audio.");
    }

    // ---- 预扫描：读取开头的 PAT/PMT 与已使用的 PID ----

    /// <param name="allPids">读满预扫描范围以收集已使用的 PID（为新音轨分配 PID 时需要）；否则读到全部节目表即停止。</param>
    private static Dictionary<int, PmtInfo> Prescan(string path, TsPacketLayout layout, CancellationToken token, bool allPids,
        out HashSet<int> usedPids, out HashSet<int> pmtPidSet)
    {
        usedPids = [0x0000, 0x0001, 0x0002, 0x1FFF];
        for (var pid = 0x10; pid < 0x20; pid++)
            usedPids.Add(pid);
        var pmtPids = new Dictionary<int, int>();
        pmtPidSet = [];
        var sections = new Dictionary<int, List<byte>>();
        var programs = new Dictionary<int, PmtInfo>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        stream.Position = layout.PacketOffset;
        var packet = new byte[layout.PacketSize];
        long read = 0;
        while (read < PrescanBytes && stream.ReadAtLeast(packet, packet.Length, throwOnEndOfStream: false) == packet.Length)
        {
            read += packet.Length;
            if ((read & 0xFFFFF) == 0)
                token.ThrowIfCancellationRequested();
            var body = layout.GetPacketBody(packet);
            if (body[0] != TsUtil.TsSyncByte)
                continue;
            var pid = ((body[1] & 0x1F) << 8) | body[2];
            usedPids.Add(pid);
            if (pid != 0 && !pmtPids.ContainsKey(pid))
                continue;
            if (!TryGetPayloadOffset(body, out var payloadOffset))
                continue;
            var payloadStart = (body[1] & 0x40) != 0;
            if (payloadStart)
            {
                var pointer = body[payloadOffset];
                var start = payloadOffset + 1 + pointer;
                if (start >= PacketBodySize)
                    continue;
                sections[pid] = [.. body[start..]];
            }
            else if (sections.TryGetValue(pid, out var partial))
                partial.AddRange(body[payloadOffset..]);
            else
                continue;
            var section = sections[pid];
            if (section.Count < 3)
                continue;
            var length = 3 + (((section[1] & 0x0F) << 8) | section[2]);
            if (section.Count < length)
                continue;
            var bytes = section.GetRange(0, length).ToArray();
            sections.Remove(pid);
            if (!TsPsiSectionBuilder.HasValidCrc(bytes))
                continue;
            if (pid == 0 && bytes[0] == 0x00)
            {
                for (var offset = 8; offset + 4 <= length - 4; offset += 4)
                {
                    var number = (bytes[offset] << 8) | bytes[offset + 1];
                    var pmt = ((bytes[offset + 2] & 0x1F) << 8) | bytes[offset + 3];
                    if (number != 0)
                    {
                        pmtPids[pmt] = number;
                        usedPids.Add(pmt);
                    }
                }
            }
            else if (bytes[0] == 0x02 && ParsePmt(bytes) is { } info)
            {
                programs[info.ProgramNumber] = info;
                usedPids.Add(info.PcrPid);
                foreach (var entry in info.Streams)
                    usedPids.Add(entry.Pid);
                if (!allPids && pmtPids.Values.All(programs.ContainsKey))
                    break;
            }
        }
        pmtPidSet.UnionWith(pmtPids.Keys);
        return programs;
    }

    private static PmtInfo? ParsePmt(ReadOnlySpan<byte> section)
    {
        if (section.Length < 16 || section[0] != 0x02)
            return null;
        var programNumber = (section[3] << 8) | section[4];
        var pcrPid = ((section[8] & 0x1F) << 8) | section[9];
        var infoLength = ((section[10] & 0x0F) << 8) | section[11];
        var offset = 12 + infoLength;
        var end = section.Length - 4;
        var streams = new List<EsEntry>();
        while (offset + 5 <= end)
        {
            var type = section[offset];
            var pid = ((section[offset + 1] & 0x1F) << 8) | section[offset + 2];
            var esLength = ((section[offset + 3] & 0x0F) << 8) | section[offset + 4];
            if (offset + 5 + esLength > end)
                return null;
            streams.Add(new EsEntry(type, pid, section.Slice(offset + 5, esLength).ToArray()));
            offset += 5 + esLength;
        }
        return offset == end ? new PmtInfo(programNumber, pcrPid, streams) : null;
    }

    /// <summary>按设置改写 PMT 的流列表；既不含杜比 E 轨、也不含未选轨道的节目返回 null（原样保留）。</summary>
    private static byte[]? RewritePmt(ReadOnlySpan<byte> section, PmtInfo info, IReadOnlyList<Track> tracks,
        IReadOnlySet<int> excluded)
    {
        if (!info.Streams.Any(stream => excluded.Contains(stream.Pid) || tracks.Any(track => track.Pid == stream.Pid)))
            return null;
        var infoLength = ((section[10] & 0x0F) << 8) | section[11];
        var result = new List<byte>(section[..(12 + infoLength)].ToArray());
        foreach (var entry in info.Streams)
        {
            if (excluded.Contains(entry.Pid))
                continue;
            var track = tracks.FirstOrDefault(item => item.Pid == entry.Pid);
            if (track is null || track.KeepOriginal)
                AppendEntry(result, entry.StreamType, entry.Pid, entry.Info);
            if (track is null)
                continue;
            foreach (var stream in track.Streams.OrderBy(pair => pair.Key).Select(pair => pair.Value))
                AppendEntry(result, stream.StreamType, stream.Pid, stream.Descriptors);
        }
        var sectionLength = result.Count + 4 - 3;
        if (sectionLength > 1021)
            return null;
        result[1] = (byte)((result[1] & 0xF0) | (sectionLength >> 8));
        result[2] = (byte)sectionLength;
        result.AddRange(new byte[4]);
        var bytes = result.ToArray();
        TsPsiSectionBuilder.WriteCrc(bytes);
        return bytes;
    }

    private static void AppendEntry(List<byte> section, int type, int pid, byte[] info)
    {
        section.Add((byte)type);
        section.Add((byte)(0xE0 | (pid >> 8)));
        section.Add((byte)pid);
        section.Add((byte)(0xF0 | (info.Length >> 8)));
        section.Add((byte)info.Length);
        section.AddRange(info);
    }

    /// <summary>组装一个 PID 的 PES，PTS 展开 33 位回绕；连续计数相同且内容相同的重复包跳过（写出端用同一规则计数）。</summary>
    private sealed class PesAssembler
    {
        private readonly List<byte> data = [];
        private readonly byte[] last = new byte[PacketBodySize];
        private bool hasLast;
        private long previousPts = FFmpeg.NativeMethods.AV_NOPTS_VALUE;

        public int Index { get; private set; } = -1;

        public bool IsDuplicate(ReadOnlySpan<byte> body)
        {
            var duplicate = hasLast && (body[3] & 0x10) != 0 && body.SequenceEqual(last);
            body.CopyTo(last);
            hasLast = true;
            return duplicate;
        }

        public void Start()
        {
            Index++;
            data.Clear();
        }

        public void Append(ReadOnlySpan<byte> body)
        {
            if (Index < 0 || !TryGetPayloadOffset(body, out var offset))
                return;
            data.AddRange(body[offset..]);
        }

        public bool TryGetPayload(out ReadOnlySpan<byte> payload, out long pts)
        {
            payload = default;
            pts = FFmpeg.NativeMethods.AV_NOPTS_VALUE;
            var bytes = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(data);
            if (bytes.Length < 9 || bytes[0] != 0 || bytes[1] != 0 || bytes[2] != 1)
                return false;
            var headerLength = bytes[8];
            var start = 9 + headerLength;
            if (start > bytes.Length)
                return false;
            var length = (bytes[4] << 8) | bytes[5];
            var end = length == 0 ? bytes.Length : Math.Min(bytes.Length, 6 + length);
            if ((bytes[7] & 0x80) != 0 && headerLength >= 5)
            {
                var raw = TsTimestampFieldCodec.ReadPesTimestamp(bytes.Slice(9, 5));
                pts = previousPts == FFmpeg.NativeMethods.AV_NOPTS_VALUE ? raw : Unwrap(raw, previousPts);
                previousPts = pts;
            }
            if (end <= start)
                return false;
            payload = bytes[start..end];
            return true;
        }
    }

    /// <summary>按上一个时间戳展开 33 位 PTS 回绕。</summary>
    internal static long Unwrap(long raw, long previous)
    {
        const long wrap = 1L << 33;
        var candidate = previous - (previous % wrap + wrap) % wrap + raw;
        if (candidate - previous > wrap / 2)
            candidate -= wrap;
        else if (previous - candidate > wrap / 2)
            candidate += wrap;
        return candidate;
    }

    /// <summary>一个编码帧打包为一个 PES（只带 PTS），切成 TS 包，最后一包用适配字段填充。</summary>
    private static IEnumerable<byte[]> Packetize(OutputStream stream, DolbyEEncodedUnit unit)
    {
        var pesLength = 3 + 5 + unit.Data.Length;
        var pes = new byte[6 + pesLength];
        pes[0] = 0; pes[1] = 0; pes[2] = 1; pes[3] = stream.StreamId;
        if (pesLength <= 0xFFFF)
        {
            pes[4] = (byte)(pesLength >> 8);
            pes[5] = (byte)pesLength;
        }
        pes[6] = 0x84; // 数据对齐
        pes[7] = 0x80; // 只有 PTS
        pes[8] = 5;
        var pts = unit.Pts90k & ((1L << 33) - 1);
        pes[9] = (byte)(0x21 | ((pts >> 29) & 0x0E));
        pes[10] = (byte)(pts >> 22);
        pes[11] = (byte)(0x01 | ((pts >> 14) & 0xFE));
        pes[12] = (byte)(pts >> 7);
        pes[13] = (byte)(0x01 | ((pts << 1) & 0xFE));
        unit.Data.CopyTo(pes, 14);
        var packets = new List<byte[]>();
        var offset = 0;
        while (offset < pes.Length)
        {
            var body = new byte[PacketBodySize];
            var remaining = pes.Length - offset;
            body[0] = TsUtil.TsSyncByte;
            body[1] = (byte)((offset == 0 ? 0x40 : 0) | (stream.Pid >> 8));
            body[2] = (byte)stream.Pid;
            var cc = stream.Continuity;
            stream.Continuity = (cc + 1) & 0x0F;
            if (remaining >= 184)
            {
                body[3] = (byte)(0x10 | cc);
                pes.AsSpan(offset, 184).CopyTo(body.AsSpan(4));
                offset += 184;
            }
            else
            {
                body[3] = (byte)(0x30 | cc);
                var adaptation = 184 - remaining - 1;
                body[4] = (byte)adaptation;
                if (adaptation > 0)
                {
                    body[5] = 0x00;
                    body.AsSpan(6, adaptation - 1).Fill(0xFF);
                }
                pes.AsSpan(offset, remaining).CopyTo(body.AsSpan(5 + adaptation));
                offset += remaining;
            }
            packets.Add(body);
        }
        return packets;
    }

    private static bool HasPcr(ReadOnlySpan<byte> body) =>
        (body[3] & 0x20) != 0 && body[4] > 0 && (body[5] & 0x10) != 0;

    /// <summary>负载在包体中的起点；没有负载或适配字段越界时返回 false。</summary>
    internal static bool TryGetPayloadOffset(ReadOnlySpan<byte> body, out int offset)
    {
        offset = 4;
        var control = (body[3] >> 4) & 0x03;
        if ((control & 0x01) == 0)
            return false;
        if ((control & 0x02) != 0)
            offset += 1 + body[4];
        return offset < PacketBodySize;
    }

    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Temporary file was not removed: {exception.Message}");
        }
    }

    /// <summary>
    /// 写出包：先攒成大块再写入输出（及检测会话）。188 字节 TS 直接写出包体；192 字节 M2TS 的新包沿用参考包
    /// （所占位置或前一个包）的到达时间戳。
    /// </summary>
    private sealed class PacketWriter(Action<byte[], int> output, TsPacketLayout layout)
    {
        private readonly byte[] prefix = new byte[TsPacketLayout.M2tsHeaderSize];
        private readonly byte[] buffer = new byte[1 << 20];
        private int used;

        public void WriteRaw(ReadOnlySpan<byte> bytes)
        {
            if (layout.IsM2ts && bytes.Length == layout.PacketSize)
                bytes[..TsPacketLayout.M2tsHeaderSize].CopyTo(prefix);
            Put(bytes);
        }

        /// <summary>整块写出连续的原包；M2TS 记下最后一包的到达时间戳，供随后新增的包沿用。</summary>
        public void WriteRun(ReadOnlySpan<byte> packets)
        {
            if (layout.IsM2ts)
                packets[^layout.PacketSize..][..TsPacketLayout.M2tsHeaderSize].CopyTo(prefix);
            while (packets.Length > 0)
            {
                var count = Math.Min(packets.Length, buffer.Length);
                Put(packets[..count]);
                packets = packets[count..];
            }
        }

        public void WriteBody(ReadOnlySpan<byte> body, ReadOnlySpan<byte> reference)
        {
            if (layout.IsM2ts)
            {
                if (reference.Length == layout.PacketSize)
                    reference[..TsPacketLayout.M2tsHeaderSize].CopyTo(prefix);
                Put(prefix);
            }
            Put(body);
        }

        public void Flush()
        {
            if (used == 0)
                return;
            output(buffer, used);
            used = 0;
        }

        private void Put(ReadOnlySpan<byte> bytes)
        {
            if (used + bytes.Length > buffer.Length)
                Flush();
            bytes.CopyTo(buffer.AsSpan(used));
            used += bytes.Length;
        }

        /// <summary>保留包头与适配字段（PCR），去掉负载并以填充字节补满；CC 不变（无负载的包不计数）。</summary>
        public void WriteAdaptationOnly(ReadOnlySpan<byte> packet, TsPacketLayout packetLayout)
        {
            var body = packetLayout.GetPacketBody(packet);
            var result = new byte[PacketBodySize];
            body[..4].CopyTo(result);
            result[1] &= 0xBF;
            result[3] = (byte)((result[3] & 0xCF) | 0x20);
            var adaptationLength = Math.Min((int)body[4], 183);
            body.Slice(5, adaptationLength).CopyTo(result.AsSpan(5));
            result[4] = 183;
            result.AsSpan(5 + adaptationLength).Fill(0xFF);
            WriteBody(result, packet);
        }
    }

    /// <summary>
    /// 改写含杜比 E 轨或未选轨道的 PMT：在 PMT PID 上缓存一个完整段的包，完整且 CRC 正确时按原包位置写入新段，
    /// 否则原样写出。PMT PID 由 PAT 确定，写出时随 PAT 更新；完整的 PMT 同时更新各节目的 PCR PID。
    /// </summary>
    private sealed class PmtRewriter(IReadOnlyList<Track> tracks, IReadOnlySet<int> excluded, HashSet<int> pcrPids,
        TsPacketLayout layout, IEnumerable<int> knownPmtPids, Action onRemoved)
    {
        private readonly Dictionary<int, State> states = [];
        // 按 PID 索引，逐包判断。
        private readonly bool[] pmtPids = PidTable(knownPmtPids);
        private readonly List<byte> patSection = [];

        private sealed class State
        {
            public List<byte[]> Packets { get; } = [];
            public List<byte> Section { get; } = [];
            public int Expected = -1;
            public int CcOffset;
            public int LastCc = -1;
        }

        public bool Handles(int pid, ReadOnlySpan<byte> body)
        {
            if (pid == 0)
            {
                TrackPat(body);
                return false;
            }
            return pmtPids[pid];
        }

        public bool IsPmtPid(int pid) => pmtPids[pid];

        private static bool[] PidTable(IEnumerable<int> pids)
        {
            var table = new bool[0x2000];
            foreach (var pid in pids)
                table[pid & 0x1FFF] = true;
            return table;
        }

        private void TrackPat(ReadOnlySpan<byte> body)
        {
            if (!TryGetPayloadOffset(body, out var offset))
                return;
            if ((body[1] & 0x40) != 0)
            {
                var start = offset + 1 + body[offset];
                if (start >= PacketBodySize)
                    return;
                patSection.Clear();
                patSection.AddRange(body[start..].ToArray());
            }
            else if (patSection.Count > 0)
                patSection.AddRange(body[offset..].ToArray());
            if (patSection.Count < 3)
                return;
            var length = 3 + (((patSection[1] & 0x0F) << 8) | patSection[2]);
            if (patSection.Count < length)
                return;
            var section = patSection.GetRange(0, length).ToArray();
            patSection.Clear();
            if (section[0] != 0x00 || !TsPsiSectionBuilder.HasValidCrc(section))
                return;
            for (var i = 8; i + 4 <= length - 4; i += 4)
            {
                var number = (section[i] << 8) | section[i + 1];
                if (number != 0)
                    pmtPids[((section[i + 2] & 0x1F) << 8) | section[i + 3]] = true;
            }
        }

        public void Push(int pid, ReadOnlySpan<byte> packet, PacketWriter writer)
        {
            if (!states.TryGetValue(pid, out var state))
                states[pid] = state = new State();
            var body = layout.GetPacketBody(packet);
            var hasPayload = TryGetPayloadOffset(body, out var offset);
            if (hasPayload && (body[1] & 0x40) != 0)
            {
                Flush(state, writer);
                var pointer = body[offset];
                var start = offset + 1 + pointer;
                // 段不从负载开头开始（前面接着上一段的尾部）时不改写。
                if (pointer != 0 || start >= PacketBodySize || body[start] != 0x02)
                {
                    WriteShifted(state, packet, writer);
                    return;
                }
                state.Packets.Add(packet.ToArray());
                state.Section.AddRange(body[start..].ToArray());
                state.Expected = 3 + (((body[start + 1] & 0x0F) << 8) | body[start + 2]);
                TryComplete(state, writer);
                return;
            }
            if (state.Packets.Count > 0 && hasPayload)
            {
                state.Packets.Add(packet.ToArray());
                state.Section.AddRange(body[offset..].ToArray());
                TryComplete(state, writer);
                return;
            }
            WriteShifted(state, packet, writer);
        }

        private void TryComplete(State state, PacketWriter writer)
        {
            if (state.Expected < 0 || state.Section.Count < state.Expected)
                return;
            var section = state.Section.GetRange(0, state.Expected).ToArray();
            var tail = state.Section.Skip(state.Expected);
            byte[]? rewritten = null;
            if (tail.All(value => value == 0xFF) && TsPsiSectionBuilder.HasValidCrc(section) && ParsePmt(section) is { } info)
            {
                pcrPids.Add(info.PcrPid);
                rewritten = RewritePmt(section, info, tracks, excluded);
                if (rewritten is not null && info.Streams.Any(stream => excluded.Contains(stream.Pid)))
                    onRemoved();
            }
            if (rewritten is null)
            {
                Flush(state, writer);
                return;
            }
            var packets = state.Packets.ToArray();
            Reset(state);
            var written = 0;
            var position = 0;
            byte[]? lastPacket = null;
            for (var i = 0; i < packets.Length && position < rewritten.Length; i++)
            {
                var packet = packets[i];
                var body = layout.GetPacketBody(packet.AsSpan());
                TryGetPayloadOffset(body, out var offset);
                if (i == 0)
                    body[offset++] = 0;
                var count = Math.Min(PacketBodySize - offset, rewritten.Length - position);
                rewritten.AsSpan(position, count).CopyTo(body[offset..]);
                body[(offset + count)..].Fill(0xFF);
                position += count;
                ShiftCc(state, body);
                writer.WriteRaw(packet);
                lastPacket = packet;
                written++;
            }
            // 原有的包用完后补包；新段较短时多出的原包删去。CC 偏移使该 PID 之后的包接续。
            while (position < rewritten.Length)
            {
                var body = new byte[PacketBodySize];
                var template = layout.GetPacketBody(lastPacket!.AsSpan());
                body[0] = TsUtil.TsSyncByte;
                body[1] = (byte)(template[1] & 0x1F);
                body[2] = template[2];
                var cc = (state.LastCc + 1) & 0x0F;
                body[3] = (byte)(0x10 | cc);
                state.LastCc = cc;
                state.CcOffset++;
                var count = Math.Min(184, rewritten.Length - position);
                rewritten.AsSpan(position, count).CopyTo(body.AsSpan(4));
                body.AsSpan(4 + count).Fill(0xFF);
                position += count;
                writer.WriteBody(body, lastPacket);
            }
            state.CcOffset -= packets.Length - written;
        }

        private static void Reset(State state)
        {
            state.Packets.Clear();
            state.Section.Clear();
            state.Expected = -1;
        }

        private void Flush(State state, PacketWriter writer)
        {
            foreach (var packet in state.Packets)
                WriteShifted(state, packet, writer);
            Reset(state);
        }

        public void FlushAll(PacketWriter writer)
        {
            // 文件尾不完整的段原样写出。
            foreach (var state in states.Values)
                Flush(state, writer);
        }

        private void WriteShifted(State state, ReadOnlySpan<byte> packet, PacketWriter writer)
        {
            if (state.CcOffset == 0)
            {
                var original = layout.GetPacketBody(packet);
                if ((original[3] & 0x10) != 0)
                    state.LastCc = original[3] & 0x0F;
                writer.WriteRaw(packet);
                return;
            }
            var copy = packet.ToArray();
            ShiftCc(state, layout.GetPacketBody(copy.AsSpan()));
            writer.WriteRaw(copy);
        }

        private static void ShiftCc(State state, Span<byte> body)
        {
            if ((body[3] & 0x10) == 0)
                return;
            var cc = ((body[3] & 0x0F) + state.CcOffset) & 0x0F;
            body[3] = (byte)((body[3] & 0xF0) | cc);
            state.LastCc = cc;
        }
    }
}
