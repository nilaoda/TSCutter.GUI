using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TSCutter.GUI.Services;

namespace TSCutter.GUI.Utils;

// 只读取有界的头尾窗口；加密载荷不送入解码器探测，也不以首尾 PCR 差推断时长。
internal sealed class TsMediaInfoProbe
{
    internal const int WindowBytes = 8 * 1024 * 1024;
    public int? TransportStreamId { get; private set; }
    public Dictionary<int, ProgramInfo> Programs { get; } = [];
    public Dictionary<int, StreamInfo> Streams { get; } = [];
    public HashSet<string> NetworkNames { get; } = [];
    public HashSet<int> ScrambledPids { get; } = [];
    public bool IsEncrypted => Streams.Keys.Any(ScrambledPids.Contains);

    public static TsMediaInfoProbe? Read(string path)
    {
        using var file = File.OpenRead(path);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long sync = -1;
            int stride = 0;
            // 不依赖文件后缀；允许损坏前缀以及 192/204 字节传输包。
            while (file.Position < Math.Min(file.Length, TsScramblingProbe.MaximumProbeBytes))
            {
                var start = file.Position;
                var length = file.ReadAtLeast(buffer.AsSpan(0, 64 * 1024), 64 * 1024, false);
                var layout = TsScramblingProbe.FindPacketLayout(buffer.AsSpan(0, length));
                if (layout.PacketSize != 0)
                {
                    sync = start + layout.SyncOffset;
                    stride = layout.PacketSize;
                    break;
                }
                if (length < 64 * 1024) break;
                file.Position -= 204 * 4;
            }
            if (sync < 0) return null;
            var result = new TsMediaInfoProbe();
            var states = new Dictionary<int, PsiState>();
            var headEnd = Math.Min(file.Length, sync + (WindowBytes / stride) * stride);
            result.Scan(file, sync, headEnd, stride, states);
            var tailStart = sync + Math.Max(0, (file.Length - sync - WindowBytes) / stride) * stride;
            if (tailStart >= headEnd)
            {
                if (tailStart > headEnd) states.Clear();
                result.Scan(file, tailStart, file.Length, stride, states);
            }
            else if (headEnd < file.Length)
                result.Scan(file, headEnd, file.Length, stride, states);
            return result;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private void Scan(FileStream file, long start, long end, int stride, Dictionary<int, PsiState> states)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(stride * 1024);
        file.Position = start;
        try
        {
            while (file.Position < end)
            {
                var requested = (int)Math.Min(stride * 1024, end - file.Position);
                var length = file.ReadAtLeast(buffer.AsSpan(0, requested), requested, false);
                for (var offset = 0; offset + 188 <= length; offset += stride)
                {
                    var packet = buffer.AsSpan(offset, 188);
                    var info = TsPacketParser.Parse(packet);
                    if (!info.IsValid || info.TransportError) continue;
                    var pid = info.Pid;
                    if (info.HasPayload && info.ScramblingControl >= 2 && pid != 0x1fff)
                        ScrambledPids.Add(pid);
                    if (!info.HasPayload || info.ScramblingControl != 0) continue;
                    if (pid != 0 && pid != 0x10 && pid != 0x11 && !Programs.Values.Any(p => p.PmtPid == pid))
                        continue;
                    if (!states.TryGetValue(pid, out var state))
                        states[pid] = state = new PsiState();
                    if (info.Discontinuity)
                    {
                        state.Assembler.DiscardUntilPayloadStart();
                        state.Continuity = -1;
                    }
                    if (state.Continuity == info.ContinuityCounter) continue;
                    if (state.Continuity >= 0 && ((state.Continuity + 1) & 15) != info.ContinuityCounter)
                        state.Assembler.DiscardUntilPayloadStart();
                    state.Continuity = info.ContinuityCounter;
                    state.Assembler.Push(packet[info.PayloadOffset..], info.PayloadStart,
                        section => ReadSection(pid, section));
                }
                if (length < requested) break;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private void ReadSection(int pid, ReadOnlySpan<byte> section)
    {
        if (section.Length < 12 || (section[5] & 1) == 0 || !TsPsiSectionBuilder.HasValidCrc(section)) return;
        var end = section.Length - 4;
        if (pid == 0 && section[0] == 0)
        {
            TransportStreamId = (section[3] << 8) | section[4];
            for (var i = 8; i + 4 <= end; i += 4)
            {
                var number = (section[i] << 8) | section[i + 1];
                if (number == 0) continue;
                GetProgram(number).PmtPid = ((section[i + 2] & 31) << 8) | section[i + 3];
            }
        }
        else if (section[0] == 2 && Programs.Values.Any(p => p.PmtPid == pid) && section.Length >= 16)
        {
            var number = (section[3] << 8) | section[4];
            if (!Programs.TryGetValue(number, out var program) || program.PmtPid != pid) return;
            var i = 12 + (((section[10] & 15) << 8) | section[11]);
            while (i + 5 <= end)
            {
                var type = section[i];
                var streamPid = ((section[i + 1] & 31) << 8) | section[i + 2];
                var n = ((section[i + 3] & 15) << 8) | section[i + 4];
                if (i + 5 + n > end) return;
                if (!Streams.TryGetValue(streamPid, out var stream)) Streams[streamPid] = stream = new StreamInfo();
                stream.Types.Add(type);
                stream.Programs.Add(number);
                var resolvedType = type;
                var languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ReadDescriptors(section.Slice(i + 5, n), (tag, body) =>
                {
                    if (type == 0x06)
                        resolvedType = tag switch
                        {
                            0x6a => 0x81, 0x7a => 0x84, 0x7b => 0x82, _ => resolvedType
                        };
                    if (tag == 0x0a)
                        for (var j = 0; j + 4 <= body.Length; j += 4)
                            languages.Add(Encoding.ASCII.GetString(body.Slice(j, 3)));
                    else if (tag == 0x28 && body.Length >= 4)
                    {
                        var profile = body[0] switch
                        {
                            66 => "Baseline", 77 => "Main", 88 => "Extended", 100 => "High",
                            110 => "High 10", 122 => "High 4:2:2", 244 => "High 4:4:4", _ => null
                        };
                        if (profile is not null)
                            stream.Profiles.Add($"{profile}@L{body[2] / 10}.{body[2] % 10}");
                    }
                });
                stream.Languages.UnionWith(languages);
                if (languages.Count > 0)
                    stream.LanguageSets.Add(string.Join(" / ", languages.Order(StringComparer.OrdinalIgnoreCase)));
                stream.ResolvedTypes.Add(resolvedType);
                i += 5 + n;
            }
        }
        else if (pid == 0x10 && section[0] == 0x40 && section.Length >= 16)
        {
            var n = ((section[8] & 15) << 8) | section[9];
            if (10 + n > end) return;
            ReadDescriptors(section.Slice(10, n), (tag, body) =>
            {
                if (tag == 0x40) NetworkNames.Add(TsDvbTextCodec.Decode(body));
            });
        }
        else if (pid == 0x11 && section[0] == 0x42 && section.Length >= 15)
        {
            for (var i = 11; i + 5 <= end;)
            {
                var number = (section[i] << 8) | section[i + 1];
                var n = ((section[i + 3] & 15) << 8) | section[i + 4];
                if (i + 5 + n > end) return;
                ReadDescriptors(section.Slice(i + 5, n), (tag, body) =>
                {
                    if (tag != 0x48 || body.Length < 3) return;
                    var providerLength = body[1];
                    if (providerLength + 2 >= body.Length) return;
                    var nameLength = body[providerLength + 2];
                    if (providerLength + 3 + nameLength > body.Length) return;
                    var program = GetProgram(number);
                    program.Names.Add(TsDvbTextCodec.Decode(body.Slice(providerLength + 3, nameLength)));
                    program.Providers.Add(TsDvbTextCodec.Decode(body.Slice(2, providerLength)));
                    program.ServiceTypes.Add(body[0]);
                });
                i += 5 + n;
            }
        }
    }

    private ProgramInfo GetProgram(int number)
    {
        if (!Programs.TryGetValue(number, out var program)) Programs[number] = program = new ProgramInfo();
        return program;
    }

    private static void ReadDescriptors(ReadOnlySpan<byte> descriptors, Action<byte, ReadOnlySpan<byte>> read)
    {
        for (var i = 0; i + 2 <= descriptors.Length;)
        {
            var n = descriptors[i + 1];
            if (i + 2 + n > descriptors.Length) return;
            read(descriptors[i], descriptors.Slice(i + 2, n));
            i += 2 + n;
        }
    }

    private sealed class PsiState
    {
        public PsiState() => Assembler.DiscardUntilPayloadStart();
        public TsPsiSectionAssembler Assembler { get; } = new();
        public int Continuity { get; set; } = -1;
    }

    internal sealed class StreamInfo
    {
        public HashSet<byte> Types { get; } = [];
        public HashSet<byte> ResolvedTypes { get; } = [];
        public HashSet<int> Programs { get; } = [];
        public HashSet<string> Languages { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> LanguageSets { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool LanguagesChanged => LanguageSets.Count > 1;
        public HashSet<string> Profiles { get; } = [];
    }

    internal sealed class ProgramInfo
    {
        public int PmtPid { get; set; } = -1;
        public HashSet<string> Names { get; } = [];
        public HashSet<string> Providers { get; } = [];
        public HashSet<byte> ServiceTypes { get; } = [];
    }
}
