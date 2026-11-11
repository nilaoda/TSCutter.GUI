using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Utils.TransportStream.Packets;

namespace TSCutter.GUI.Services.TransportStream.Analysis;

/// <summary>
/// 读取录制 TS 中的 EPG：EIT 现在/下一个节目表（table 0x4E）周期性重复，按“现在播出”节目的切换把录制分成节目段。
/// 不扫描整个文件：先在锚点（文件开头，或用户跳转到的位置）读一个探测窗口，读不到现在节目表就视为没有 EPG，不再读其他位置；
/// 读到后再读开头和结尾，两处不同时在中间探测，只在仍不相同的一段继续查找，直到确定切换前后相邻的两张表；
/// 能按排期预测切换时刻时直接在预测处附近探测。表每秒重复的录制通常只读几十 MB，表间隔较长时读取量相应增加。
/// </summary>
internal static class TsEpgReader
{
    public const int EitPid = 0x0012;
    private const byte PresentFollowingTableId = 0x4E;
    private const byte TotTableId = 0x73;
    private const int ReadChunkBytes = 1 << 20;
    // 每个探测点最多按码率读约这么多秒的内容，读不到就放弃该点（例如没有 EPG、信号中断或已去掉 EIT 的片段）。
    // 规范要求现在节目表至少每 2 秒重复，但实际有频道间隔 8–10 秒，窗口需覆盖这类间隔；读到第一张表即停止，
    // 有 EPG 时通常只读到窗口的一小部分。按字节限定上下限，避免高码率文件读得过多。
    private const double ProbeSeconds = 20;
    private const long MinProbeBytes = 1L << 20;
    private const long MaxProbeBytes = 64L << 20;
    // 按排期预测切换时，相对预测时刻依次探测的位置（秒）：先在之前由近及远，再在之后。
    private static readonly double[] PredictionProbeSeconds = [-15, -45, -120, -300, 15, 45, 120, 300];
    // 范围小于此值时连续读取以确定切换时刻，不再二分。
    private const long RefineBytes = 4L << 20;
    private const long PtsWrap = 1L << 33;

    /// <param name="videoPid">用来确定切换时刻的视频 PID。</param>
    /// <param name="serviceId">节目号（service_id）；为 null 时取读到的第一个节目。</param>
    /// <param name="firstFramePts">时间轴起点画面的 PTS，切换时刻按它换算成时间轴秒数。</param>
    /// <param name="anchorOffset">先探测的字节位置：0 为文件开头；用户跳转后可取当前画面的位置。</param>
    public static Task<EpgGuide> ReadAsync(string path, int videoPid, int? serviceId, long firstFramePts,
        double durationSeconds, long anchorOffset, CancellationToken token) => Task.Run(() =>
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            1, FileOptions.RandomAccess);
        return Read(stream, videoPid, serviceId, firstFramePts, durationSeconds, anchorOffset, token);
    }, token);

    internal static EpgGuide Read(Stream stream, int videoPid, int? serviceId, long firstFramePts,
        double durationSeconds, long anchorOffset, CancellationToken token)
    {
        // 跳转后的探测会反复读取，读取缓冲区从共享池租用。
        var buffer = ArrayPool<byte>.Shared.Rent(ReadChunkBytes);
        try
        {
            if (!TryDetectLayout(stream, buffer, out var layout))
                return EpgGuide.Empty;
            var reader = new Reader(stream, layout, buffer, videoPid, serviceId, firstFramePts, durationSeconds,
                ProbeWindowBytes(stream.Length, durationSeconds), token);
            return reader.Run(Math.Clamp(anchorOffset, 0, stream.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>每个探测点读取的字节数：按平均码率约 <see cref="ProbeSeconds"/> 秒的内容。</summary>
    internal static long ProbeWindowBytes(long length, double durationSeconds) => durationSeconds > 0
        ? Math.Clamp((long)(length / durationSeconds * ProbeSeconds), MinProbeBytes, MaxProbeBytes)
        : MaxProbeBytes;

    /// <summary>每个 TS 包在文件中的步长（188/192/204）以及同步字节在步长内的偏移。</summary>
    private readonly record struct PacketLayout(int Stride, int SyncOffset);

    private static bool TryDetectLayout(Stream stream, byte[] buffer, out PacketLayout layout)
    {
        layout = default;
        stream.Position = 0;
        var length = stream.ReadAtLeast(buffer.AsSpan(0, 64 * 1024), 64 * 1024, throwOnEndOfStream: false);
        foreach (var stride in new[] { 188, 192, 204 })
        {
            for (var start = 0; start < stride && start + stride * 8 < length; start++)
            {
                var ok = true;
                for (var index = 0; index < 8 && ok; index++)
                    ok = buffer[start + index * stride] == 0x47;
                if (!ok)
                    continue;
                layout = new PacketLayout(stride, start % stride);
                return true;
            }
        }
        return false;
    }

    /// <summary>一次读到的现在/下一个节目表；Seconds 为表出现时的视频时间轴秒数。</summary>
    private sealed record PresentSample(long Offset, double Seconds, EpgEvent Present, EpgEvent? Following);

    private sealed class Reader(Stream stream, PacketLayout layout, byte[] buffer, int videoPid, int? serviceId,
        long firstFramePts, double durationSeconds, long probeBytes, CancellationToken token)
    {
        private int? _serviceId = serviceId;
        private readonly Dictionary<int, EpgEvent> _events = [];
        private readonly List<(double Seconds, EpgEvent Event)> _transitions = [];
        private TimeSpan? _localOffset;
        // 相邻两张现在节目表的字节间距估计：取各次探测从探测位置到读到的表的最大距离（不超过约 12 秒的内容），
        // 探测越多越接近实际间距；未知时为 0。
        private long _tableSpacing;
        private readonly long _maxTableSpacing = durationSeconds > 0 ? (long)(stream.Length / durationSeconds * 12) : 0;
        // 时间轴起点对应的播出时间：先取读到的 TDT/TOT，找到切换后按该节目的排期开始校正，用来预测下一次切换。
        private DateTimeOffset? _clockAtZero;

        public EpgGuide Run(long anchorOffset)
        {
            var anchor = Probe(anchorOffset);
            if (anchor is null)
                return EpgGuide.Empty;
            var head = anchorOffset == 0 ? anchor : Probe(0);
            var samples = new[] { head, anchor, LastSample() }.OfType<PresentSample>().DistinctBy(item => item.Offset)
                .OrderBy(item => item.Offset).ToList();
            for (var index = 0; index + 1 < samples.Count; index++)
                Bisect(samples[index], samples[index + 1], samples[index + 1].Offset);
            // 开头读不到节目表时，最早读到的位置之前的部分不属于任何已知节目。
            return Build(samples[0], fromRecordingStart: ReferenceEquals(samples[0], head));
        }

        private PresentSample? Probe(long offset)
        {
            var sample = Scan(offset, probeBytes, stopAtFirst: true).FirstOrDefault();
            if (sample is not null && sample.Offset > offset)
                _tableSpacing = Math.Max(_tableSpacing, Math.Min(sample.Offset - offset, _maxTableSpacing));
            return sample;
        }

        // 从文件末尾向前分段读取（每段四分之一窗口），取最后一次读到的现在节目表；最多读一个探测窗口。
        private PresentSample? LastSample()
        {
            var step = Math.Max(MinProbeBytes, probeBytes / 4);
            for (var end = stream.Length; end > 0 && stream.Length - end < probeBytes; end -= step)
            {
                var start = Math.Max(0, end - step);
                var samples = Scan(start, end - start, stopAtFirst: false);
                if (samples.Count > 0)
                    return samples[^1];
            }
            return null;
        }

        /// <summary>在 low 与 high 两次读到的表之间找出节目切换。</summary>
        /// <param name="limit">已知从 limit 到 high 之间没有其他节目表，只需在 low 到 limit 之间查找。</param>
        private void Bisect(PresentSample low, PresentSample high, long limit)
        {
            token.ThrowIfCancellationRequested();
            if (low.Present.EventId == high.Present.EventId || high.Offset <= low.Offset)
                return;
            // 剩下的范围不超过一个表间距、容不下其他表：切换时刻取后一次读到的表，不再为更精确的位置读取。
            if (limit - low.Offset <= _tableSpacing)
            {
                AddTransition(high.Seconds, high.Present);
                return;
            }
            if (limit - low.Offset <= RefineBytes)
            {
                Refine(low, high, limit);
                return;
            }
            var narrowed = limit;
            foreach (var position in SplitPositions(low, high, limit))
            {
                if (position <= low.Offset || position >= narrowed)
                    continue;
                // 探测窗口内读不到表（例如信号中断）时换个位置。
                if (Probe(position) is not { } sample)
                    continue;
                if (sample.Offset < high.Offset)
                {
                    Bisect(low, sample, sample.Offset);
                    Bisect(sample, high, narrowed);
                    return;
                }
                // 读到的就是 high：此处到 high 之间没有其他表，之后不再探测这一段。
                narrowed = position;
            }
            if (narrowed < limit)
                Bisect(low, high, narrowed);
            else
                // 各处都读不到节目表，切换时刻只能取后一次读到的位置。
                AddTransition(high.Seconds, high.Present);
        }

        private void Refine(PresentSample low, PresentSample high, long limit)
        {
            var previous = low.Present.EventId;
            foreach (var sample in Scan(low.Offset, limit - low.Offset + layout.Stride, stopAtFirst: false))
            {
                if (sample.Present.EventId == previous || sample.Offset >= high.Offset)
                    continue;
                AddTransition(sample.Seconds, sample.Present);
                previous = sample.Present.EventId;
            }
            if (previous != high.Present.EventId)
                AddTransition(high.Seconds, high.Present);
        }

        /// <summary>
        /// 分割位置。现在节目表通常按排期切换：知道播出时间与时间轴的对应后，按当前节目的排期结束预测切换时刻，
        /// 由近及远先在预测之前、再在之后探测；读到的表都能用来分割，预测不准只是多探测几次。
        /// 之后（或不能预测时）取剩余范围的中点、四分之一、四分之三处，避免一处读不到就把整段并成一次切换。
        /// </summary>
        private IEnumerable<long> SplitPositions(PresentSample low, PresentSample high, long limit)
        {
            if (_clockAtZero is { } clock && low.Present is { Start: { } start, Duration: var duration }
                && duration > TimeSpan.Zero && high.Seconds > low.Seconds)
            {
                var predicted = (start + duration - clock).TotalSeconds;
                foreach (var delta in PredictionProbeSeconds)
                {
                    var target = predicted + delta;
                    if (target > low.Seconds && target < high.Seconds)
                        yield return low.Offset + (long)((target - low.Seconds) / (high.Seconds - low.Seconds)
                            * (high.Offset - low.Offset));
                }
            }
            var span = limit - low.Offset;
            yield return low.Offset + span / 2;
            yield return low.Offset + span / 4;
            yield return low.Offset + span * 3 / 4;
        }

        private void AddTransition(double seconds, EpgEvent present)
        {
            if (_transitions.Any(item => item.Event.EventId == present.EventId))
                return;
            _transitions.Add((seconds, present));
            if (present.Start is { } start)
                _clockAtZero = start - TimeSpan.FromSeconds(seconds);
        }

        private EpgGuide Build(PresentSample first, bool fromRecordingStart)
        {
            var transitions = _transitions.OrderBy(item => item.Seconds).ToList();
            var programs = new List<EpgProgram>();
            var current = first.Present;
            var start = fromRecordingStart ? 0.0 : first.Seconds;
            foreach (var (seconds, next) in transitions)
            {
                var at = Math.Clamp(seconds, start, durationSeconds);
                if (next.EventId == current.EventId)
                    continue;
                if (at > start)
                    programs.Add(new EpgProgram(Latest(current), start, at, fromRecordingStart && programs.Count == 0, false));
                current = next;
                start = at;
            }
            if (durationSeconds > start)
                programs.Add(new EpgProgram(Latest(current), start, durationSeconds,
                    fromRecordingStart && programs.Count == 0, true));
            else if (programs.Count > 0)
                programs[^1] = programs[^1] with { EndsAtRecordingEnd = true };
            var events = _events.Values.OrderBy(item => item.Start ?? DateTimeOffset.MaxValue).ThenBy(item => item.EventId)
                .ToArray();
            return new EpgGuide(programs, events, _localOffset);
        }

        // 同一节目在不同时间读到的标题与简介以最后一次为准（广播可能中途补全简介）。
        private EpgEvent Latest(EpgEvent value) => _events.GetValueOrDefault(value.EventId, value);

        /// <summary>
        /// 从 offset 起读取最多 maxBytes 字节，收集其中的现在节目表。stopAtFirst 时读到第一张即返回。
        /// 节目表出现在视频 PTS 之前时，以随后的第一个视频 PTS 作为它的时刻。
        /// </summary>
        private List<PresentSample> Scan(long offset, long maxBytes, bool stopAtFirst)
        {
            var samples = new List<PresentSample>();
            var assembler = new TsPsiSectionAssembler();
            var totAssembler = new TsPsiSectionAssembler();
            var pending = new List<(long Offset, EpgEvent Present, EpgEvent? Following)>();
            EpgEvent? following = null;
            double? lastSeconds = null;
            // 从同步字节处读取，第 i 个包位于缓冲区的 i * 步长处（192/204 字节的包含前缀或校验字节）。
            var position = AlignToPacket(offset);
            var end = Math.Min(stream.Length, offset + maxBytes);
            var chunk = ReadChunkBytes - ReadChunkBytes % layout.Stride;
            while (position < end)
            {
                token.ThrowIfCancellationRequested();
                stream.Position = position;
                var wanted = (int)Math.Min(chunk, (end - position + layout.Stride - 1) / layout.Stride * layout.Stride);
                var read = stream.ReadAtLeast(buffer.AsSpan(0, wanted), wanted, throwOnEndOfStream: false);
                var packets = read < 188 ? 0 : (read - 188) / layout.Stride + 1;
                if (packets == 0)
                    break;
                for (var index = 0; index < packets; index++)
                {
                    var packet = buffer.AsSpan(index * layout.Stride, 188);
                    var packetOffset = position + index * layout.Stride;
                    if (packet[0] != 0x47 || (packet[1] & 0x80) != 0)
                        continue;
                    var pid = ((packet[1] & 0x1F) << 8) | packet[2];
                    var payloadStart = (packet[1] & 0x40) != 0;
                    var payload = Payload(packet);
                    if (pid == videoPid && payloadStart && TryReadPts(payload, out var pts))
                    {
                        lastSeconds = ToSeconds(pts);
                        foreach (var item in pending)
                            samples.Add(new PresentSample(item.Offset, lastSeconds.Value, item.Present, item.Following));
                        pending.Clear();
                        if (stopAtFirst && samples.Count > 0)
                            return samples;
                    }
                    else if (pid == EitPid && !payload.IsEmpty)
                    {
                        EpgEvent? present = null;
                        assembler.Push(payload, payloadStart, section =>
                        {
                            if (!TryParsePresentFollowing(section, out var number, out var value))
                                return;
                            if (number == 0)
                                present = value;
                            else
                                following = value;
                        });
                        if (present is not null)
                        {
                            if (lastSeconds is { } seconds)
                                samples.Add(new PresentSample(packetOffset, seconds, present, following));
                            else
                                pending.Add((packetOffset, present, following));
                            if (stopAtFirst && lastSeconds is not null)
                                return samples;
                        }
                    }
                    else if (pid == TsDvbTimeTableParser.Pid && (_localOffset is null || _clockAtZero is null)
                             && !payload.IsEmpty)
                    {
                        totAssembler.Push(payload, payloadStart, section =>
                        {
                            ReadLocalOffset(section);
                            if (_clockAtZero is null && lastSeconds is { } now
                                && TsDvbTimeTableParser.TryParseUtc(section, out var utc))
                                _clockAtZero = utc - TimeSpan.FromSeconds(now);
                        });
                    }
                }
                position += packets * layout.Stride;
            }
            return samples;
        }

        // 不早于 offset 的第一个同步字节位置。
        private long AlignToPacket(long offset)
        {
            if (offset <= layout.SyncOffset)
                return layout.SyncOffset;
            return layout.SyncOffset + (offset - layout.SyncOffset + layout.Stride - 1) / layout.Stride * layout.Stride;
        }

        private double ToSeconds(long pts)
        {
            var difference = ((pts - firstFramePts) % PtsWrap + PtsWrap) % PtsWrap;
            // 起点之前的画面（差值超过半个回绕周期）按负数处理。
            if (difference > PtsWrap / 2)
                difference -= PtsWrap;
            return Math.Clamp(difference / 90000.0, 0, Math.Max(0, durationSeconds));
        }

        private bool TryParsePresentFollowing(ReadOnlySpan<byte> section, out int sectionNumber, out EpgEvent value)
        {
            sectionNumber = 0;
            value = default!;
            if (section.Length < 18 || section[0] != PresentFollowingTableId || (section[1] & 0x80) == 0)
                return false;
            var total = 3 + (((section[1] & 0x0F) << 8) | section[2]);
            if (total > section.Length || total < 18 || (section[5] & 0x01) == 0
                || !TsDvbTimeTableParser.HasValidMpegCrc(section[..total]))
                return false;
            var service = (section[3] << 8) | section[4];
            _serviceId ??= service;
            if (service != _serviceId)
                return false;
            sectionNumber = section[6];
            if (sectionNumber > 1 || !TryParseFirstEvent(section[14..(total - 4)], out value))
                return false;
            _events[value.EventId] = value;
            return true;
        }

        private static bool TryParseFirstEvent(ReadOnlySpan<byte> data, out EpgEvent value)
        {
            value = default!;
            if (data.Length < 12)
                return false;
            var eventId = (data[0] << 8) | data[1];
            DateTimeOffset? start = TsDvbTimeTableParser.TryParseMjdUtc(data.Slice(2, 5), out var utc) ? utc : null;
            var duration = TsDvbTimeTableParser.TryReadBcd(data[7], out var hours)
                           && TsDvbTimeTableParser.TryReadBcd(data[8], out var minutes)
                           && TsDvbTimeTableParser.TryReadBcd(data[9], out var seconds)
                ? new TimeSpan(hours, minutes, seconds) : TimeSpan.Zero;
            var loopLength = ((data[10] & 0x0F) << 8) | data[11];
            var descriptors = data.Slice(12, Math.Min(loopLength, data.Length - 12));
            string title = "", description = "";
            while (descriptors.Length >= 2)
            {
                var tag = descriptors[0];
                var length = Math.Min(descriptors[1], descriptors.Length - 2);
                var body = descriptors.Slice(2, length);
                // short_event_descriptor：ISO 639 语言、节目名、简介。
                if (tag == 0x4D && body.Length >= 4 && title.Length == 0)
                {
                    var nameLength = Math.Min(body[3], body.Length - 4);
                    title = TsDvbTextCodec.Decode(body.Slice(4, nameLength));
                    var textIndex = 4 + nameLength;
                    if (textIndex < body.Length)
                        description = TsDvbTextCodec.Decode(body.Slice(textIndex + 1,
                            Math.Min(body[textIndex], body.Length - textIndex - 1)));
                }
                descriptors = descriptors[(2 + length)..];
            }
            value = new EpgEvent(eventId, title.Length > 0 ? title : $"#{eventId}", description, start, duration);
            return true;
        }

        // TOT 的 local_time_offset_descriptor：取第一个地区的本地时差，用于显示排期时间。
        private void ReadLocalOffset(ReadOnlySpan<byte> section)
        {
            if (section.Length < 14 || section[0] != TotTableId)
                return;
            var total = 3 + (((section[1] & 0x0F) << 8) | section[2]);
            if (total > section.Length || !TsDvbTimeTableParser.HasValidMpegCrc(section[..total]))
                return;
            var descriptors = section[10..Math.Min(total - 4, 10 + (((section[8] & 0x0F) << 8) | section[9]))];
            while (descriptors.Length >= 2)
            {
                var length = Math.Min(descriptors[1], descriptors.Length - 2);
                var body = descriptors.Slice(2, length);
                if (descriptors[0] == 0x58 && body.Length >= 6
                    && TsDvbTimeTableParser.TryReadBcd(body[4], out var hours)
                    && TsDvbTimeTableParser.TryReadBcd(body[5], out var minutes))
                {
                    var offset = new TimeSpan(hours, minutes, 0);
                    _localOffset = (body[3] & 0x01) != 0 ? -offset : offset;
                    return;
                }
                descriptors = descriptors[(2 + length)..];
            }
        }
    }

    private static ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> packet)
    {
        var adaptation = (packet[3] >> 4) & 0x03;
        if ((adaptation & 0x01) == 0)
            return [];
        var start = 4;
        if ((adaptation & 0x02) != 0)
            start += 1 + packet[4];
        return start < packet.Length ? packet[start..] : [];
    }

    private static bool TryReadPts(ReadOnlySpan<byte> payload, out long pts)
    {
        pts = 0;
        if (payload.Length < 14 || payload[0] != 0 || payload[1] != 0 || payload[2] != 1 || (payload[7] & 0x80) == 0)
            return false;
        var p = payload.Slice(9, 5);
        pts = ((long)(p[0] & 0x0E) << 29) | ((long)p[1] << 22) | ((long)(p[2] & 0xFE) << 14)
              | ((long)p[3] << 7) | ((long)p[4] >> 1);
        return true;
    }
}
