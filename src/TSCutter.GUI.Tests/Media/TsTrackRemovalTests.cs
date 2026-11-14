using TSCutter.GUI.Services.Editing.DolbyE;
using TSCutter.GUI.Utils.TransportStream.Packets;
using Xunit;

namespace TSCutter.GUI.Tests.Media;

/// <summary>重组输出流删去未选的轨道：用构造的 TS 检查删包、PMT 改写与 PCR 保留。</summary>
public sealed class TsTrackRemovalTests
{
    private const int PmtPid = 0x1000;
    private const int VideoPid = 0x100;
    private const int AudioPid = 0x101;
    private const int SubtitlePid = 0x102;

    /// <summary>删去一条字幕轨：它的包全部消失，PMT 只少这一项且 CRC 正确，其他 PID 的包逐字节不变、顺序不变。</summary>
    [Fact]
    public void RemovesUnselectedTrackAndRewritesProgramTable()
    {
        var input = BuildStream(pcrPid: VideoPid);
        var (output, setup) = Rebuild(input, [SubtitlePid]);

        Assert.Null(setup.Error);
        Assert.True(setup.TracksRemoved);
        Assert.False(setup.Changed);
        Assert.Empty(PacketsOf(output, SubtitlePid));
        foreach (var pid in new[] { 0, VideoPid, AudioPid })
            Assert.Equal(PacketsOf(input, pid), PacketsOf(output, pid));
        var pmt = ReadPmt(output);
        Assert.True(TsPsiSectionBuilder.HasValidCrc(pmt));
        Assert.Equal([VideoPid, AudioPid], StreamPids(pmt));
        Assert.Equal(VideoPid, ((pmt[8] & 0x1F) << 8) | pmt[9]);
    }

    /// <summary>删去的音轨承载节目的 PCR：带 PCR 的包改为只含适配字段的包，PCR 原样保留，其余包删去。</summary>
    [Fact]
    public void KeepsPcrOfRemovedTrack()
    {
        var input = BuildStream(pcrPid: AudioPid);
        var (output, setup) = Rebuild(input, [AudioPid]);

        Assert.Null(setup.Error);
        var original = PacketsOf(input, AudioPid).Where(HasPcr).ToList();
        var kept = PacketsOf(output, AudioPid);
        Assert.Equal(original.Count, kept.Count);
        Assert.NotEmpty(kept);
        for (var i = 0; i < kept.Count; i++)
        {
            // 无负载、无 PES 起点标志，适配字段标志与 PCR 不变，其余位置为填充。
            Assert.Equal(0x20, kept[i][3] & 0x30);
            Assert.Equal(0, kept[i][1] & 0x40);
            Assert.Equal(183, kept[i][4]);
            Assert.Equal(original[i].AsSpan(5, 7).ToArray(), kept[i].AsSpan(5, 7).ToArray());
        }
        Assert.Equal([VideoPid, SubtitlePid], StreamPids(ReadPmt(output)));
        Assert.Equal(PacketsOf(input, VideoPid), PacketsOf(output, VideoPid));
    }

    private static (byte[] Output, DolbyERemuxSetup Setup) Rebuild(byte[] input, int[] excluded)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tscutter-tracks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "input.ts");
            var target = Path.Combine(directory, "output.ts");
            File.WriteAllBytes(source, input);
            var setup = new DolbyERemuxSetup(null, source, target, null, excluded);
            TsOutputFile.Current = setup;
            try
            {
                // 像导出服务一样分块写入，包会跨越分块。
                using var stream = TsOutputFile.Create(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1,
                    FileOptions.SequentialScan);
                var random = new Random(3);
                for (var offset = 0; offset < input.Length;)
                {
                    var count = Math.Min(input.Length - offset, random.Next(1, 5000));
                    stream.Write(input, offset, count);
                    offset += count;
                }
            }
            finally
            {
                TsOutputFile.Current = null;
            }
            Assert.Equal(1, setup.StreamCount);
            return (File.ReadAllBytes(target), setup);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// 构造的单节目 TS：每 20 个包重复一次 PAT/PMT，视频、音频、字幕的包交替出现；
    /// <paramref name="pcrPid"/> 的 PES 起点包带 PCR。
    /// </summary>
    private static byte[] BuildStream(int pcrPid)
    {
        var packets = new List<byte[]>();
        var continuity = new Dictionary<int, int>();
        long pcr = 0;
        for (var round = 0; round < 40; round++)
        {
            packets.Add(Packet(0, true, Next(continuity, 0), Section(Pat()), null));
            packets.Add(Packet(PmtPid, true, Next(continuity, PmtPid), Section(Pmt(pcrPid)), null));
            for (var i = 0; i < 18; i++)
            {
                var pid = (i % 3) switch { 0 => VideoPid, 1 => AudioPid, _ => SubtitlePid };
                var start = i < 3;
                var payload = Enumerable.Range(0, 184).Select(value => (byte)(value + round + i)).ToArray();
                if (start)
                    payload[..4].AsSpan().Clear();
                if (start)
                    payload[2] = 1;
                packets.Add(Packet(pid, start, Next(continuity, pid), payload,
                    start && pid == pcrPid ? pcr += 3600 * 300 : null));
            }
        }
        return packets.SelectMany(packet => packet).ToArray();
    }

    private static int Next(Dictionary<int, int> continuity, int pid)
    {
        var value = continuity.GetValueOrDefault(pid, -1) + 1 & 0x0F;
        continuity[pid] = value;
        return value;
    }

    private static byte[] Packet(int pid, bool start, int cc, byte[] payload, long? pcr)
    {
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = (byte)((start ? 0x40 : 0) | pid >> 8);
        packet[2] = (byte)pid;
        var offset = 4;
        if (pcr is { } value)
        {
            packet[3] = (byte)(0x30 | cc);
            packet[4] = 7;
            packet[5] = 0x10;
            var basePart = value / 300;
            var extension = value % 300;
            packet[6] = (byte)(basePart >> 25);
            packet[7] = (byte)(basePart >> 17);
            packet[8] = (byte)(basePart >> 9);
            packet[9] = (byte)(basePart >> 1);
            packet[10] = (byte)((int)(basePart & 1) << 7 | 0x7E | (int)(extension >> 8));
            packet[11] = (byte)extension;
            offset = 12;
        }
        else
            packet[3] = (byte)(0x10 | cc);
        payload.AsSpan(0, 188 - offset).CopyTo(packet.AsSpan(offset));
        return packet;
    }

    /// <summary>指针字段 0 + 段 + 填充 0xFF，凑满一个包的负载。</summary>
    private static byte[] Section(byte[] section)
    {
        var payload = Enumerable.Repeat((byte)0xFF, 184).ToArray();
        payload[0] = 0;
        section.CopyTo(payload, 1);
        return payload;
    }

    private static byte[] Pat()
    {
        byte[] section = [0x00, 0xB0, 13, 0x00, 0x01, 0xC1, 0x00, 0x00, 0x00, 0x01, (byte)(0xE0 | PmtPid >> 8), PmtPid & 0xFF, 0, 0, 0, 0];
        TsPsiSectionBuilder.WriteCrc(section);
        return section;
    }

    private static byte[] Pmt(int pcrPid)
    {
        var body = new List<byte> { 0x02, 0, 0, 0x00, 0x01, 0xC1, 0x00, 0x00, (byte)(0xE0 | pcrPid >> 8), (byte)pcrPid, 0xF0, 0x00 };
        foreach (var (type, pid, info) in new[]
                 {
                     (0x1B, VideoPid, Array.Empty<byte>()),
                     (0x03, AudioPid, new byte[] { 0x0A, 4, (byte)'e', (byte)'n', (byte)'g', 0 }),
                     (0x06, SubtitlePid, new byte[] { 0x59, 8, (byte)'e', (byte)'n', (byte)'g', 0x10, 0, 1, 0, 1 })
                 })
        {
            body.AddRange([(byte)type, (byte)(0xE0 | pid >> 8), (byte)pid, (byte)(0xF0 | info.Length >> 8), (byte)info.Length]);
            body.AddRange(info);
        }
        body.AddRange([0, 0, 0, 0]);
        var section = body.ToArray();
        var length = section.Length - 3;
        section[1] = (byte)(0xB0 | length >> 8);
        section[2] = (byte)length;
        TsPsiSectionBuilder.WriteCrc(section);
        return section;
    }

    private static bool HasPcr(byte[] packet) => (packet[3] & 0x20) != 0 && packet[4] > 0 && (packet[5] & 0x10) != 0;

    private static List<byte[]> PacketsOf(byte[] data, int pid)
    {
        var result = new List<byte[]>();
        for (var offset = 0; offset + 188 <= data.Length; offset += 188)
            if ((((data[offset + 1] & 0x1F) << 8) | data[offset + 2]) == pid)
                result.Add(data.AsSpan(offset, 188).ToArray());
        return result;
    }

    /// <summary>输出中第一个 PMT 段（构造的 PMT 只占一个包）。</summary>
    private static byte[] ReadPmt(byte[] data)
    {
        var packet = PacketsOf(data, PmtPid).First(item => (item[1] & 0x40) != 0);
        var start = 5 + packet[4];
        var length = 3 + (((packet[start + 1] & 0x0F) << 8) | packet[start + 2]);
        return packet.AsSpan(start, length).ToArray();
    }

    private static List<int> StreamPids(byte[] pmt)
    {
        var result = new List<int>();
        var offset = 12 + (((pmt[10] & 0x0F) << 8) | pmt[11]);
        while (offset + 5 <= pmt.Length - 4)
        {
            result.Add(((pmt[offset + 1] & 0x1F) << 8) | pmt[offset + 2]);
            offset += 5 + (((pmt[offset + 3] & 0x0F) << 8) | pmt[offset + 4]);
        }
        return result;
    }
}
