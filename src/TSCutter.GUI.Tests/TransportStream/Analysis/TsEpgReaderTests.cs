using System.Text;
using TSCutter.GUI.Services.TransportStream.Analysis;
using TSCutter.GUI.Utils.TransportStream.Packets;
using Xunit;

namespace TSCutter.GUI.Tests.TransportStream.Analysis;

public sealed class TsEpgReaderTests
{
    private const int VideoPid = 0x100;
    private const int ServiceId = 0x0101;
    private const int OtherServiceId = 0x0202;
    private const long FirstFramePts = 900_000;
    private const int Seconds = 3000;
    // 每秒：一个视频 PES、两个 EIT（或空包）与 40 个空包。
    private const int PacketsPerSecond = 43;

    [Fact]
    public void SparseTablesAndAGapAtTheMidpointKeepEveryChangeOfTheService()
    {
        // 50 分钟的录制，现在节目表每 8 秒一次（低于规范要求的频率），1300–1700 秒没有 EIT。
        // 本节目在 120 秒和 2700 秒切换，另一个节目在 1500 秒切换，不应影响结果；
        // 二分的中点读不到节目表时改在其他位置探测，不把整段并成一次切换。
        using var stream = Recording(second => second % 8 == 0 && (second < 1300 || second >= 1700));

        var guide = TsEpgReader.Read(stream, VideoPid, ServiceId, FirstFramePts, Seconds, 0, CancellationToken.None);

        AssertPrograms(guide, ("Program 1", 0, 120, true, false), ("Program 2", 120, 2704, false, false),
            ("Program 3", 2704, 3000, false, true));
        Assert.DoesNotContain(guide.Events, value => value.Title == "Other");
        Assert.Equal(new DateTimeOffset(2021, 9, 21, 6, 45, 0, TimeSpan.Zero), guide.Programs[1].Event.Start);
        Assert.Equal(TimeSpan.FromMinutes(45), guide.Programs[1].Event.Duration);
    }

    [Fact]
    public void MissingPresentTableAtTheAnchorStopsAfterOneProbeWindow()
    {
        // 前 1500 秒没有 EIT：从开头读取只探测一个窗口即视为没有 EPG；跳转到 2000 秒后从该处读到节目，
        // 之前未知的部分不属于任何节目。
        using var stream = new CountingStream(Recording(second => second >= 1500));

        Assert.False(TsEpgReader.Read(stream, VideoPid, ServiceId, FirstFramePts, Seconds, 0, CancellationToken.None)
            .HasPrograms);
        // 识别包结构读 64 KB，探测窗口按整包对齐。
        Assert.InRange(stream.BytesRead, 1, TsEpgReader.ProbeWindowBytes(stream.Length, Seconds) + (64 << 10) + 188);

        var guide = TsEpgReader.Read(stream, VideoPid, ServiceId, FirstFramePts, Seconds,
            2000L * PacketsPerSecond * 188, CancellationToken.None);
        AssertPrograms(guide, ("Program 2", 2000, 2700, false, false), ("Program 3", 2700, 3000, false, true));
    }

    private static MemoryStream Recording(Func<int, bool> hasEit)
    {
        var stream = new MemoryStream();
        for (var second = 0; second < Seconds; second++)
        {
            WritePacket(stream, VideoPid, PesWithPts(FirstFramePts + second * 90_000L));
            if (hasEit(second))
            {
                WritePacket(stream, TsEpgReader.EitPid, PresentSection(OtherServiceId, second < 1500 ? 900 : 901, "Other"));
                var eventId = second < 120 ? 1 : second < 2700 ? 2 : 3;
                WritePacket(stream, TsEpgReader.EitPid, PresentSection(ServiceId, eventId, $"Program {eventId}"));
            }
            for (var filler = hasEit(second) ? 0 : -2; filler < 40; filler++)
                WritePacket(stream, 0x1FFF, []);
        }
        return stream;
    }

    // 探测落在两个视频 PES 之间时，节目表按随后的 PTS 计时；合成素材每秒只有一个 PES，时刻允许相差 1 秒。
    private static void AssertPrograms(Models.Preview.EpgGuide guide,
        params (string Title, double Start, double End, bool FromStart, bool ToEnd)[] expected)
    {
        Assert.Equal(expected.Select(item => (item.Title, item.FromStart, item.ToEnd)),
            guide.Programs.Select(program => (program.Title, program.StartsAtRecordingStart, program.EndsAtRecordingEnd)));
        foreach (var (item, program) in expected.Zip(guide.Programs))
        {
            Assert.InRange(program.StartSeconds, item.Start, item.Start + 1);
            Assert.InRange(program.EndSeconds, item.End, item.End + 1);
        }
    }

    private static void WritePacket(Stream stream, int pid, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[188];
        Array.Fill(packet, (byte)0xFF);
        packet[0] = 0x47;
        packet[1] = (byte)((payload.IsEmpty ? 0 : 0x40) | (pid >> 8));
        packet[2] = (byte)pid;
        packet[3] = 0x10;
        payload.CopyTo(packet.AsSpan(4));
        stream.Write(packet);
    }

    private static byte[] PesWithPts(long pts) =>
    [
        0x00, 0x00, 0x01, 0xE0, 0x00, 0x00, 0x80, 0x80, 0x05,
        (byte)(0x21 | ((pts >> 29) & 0x0E)), (byte)(pts >> 22), (byte)(0x01 | ((pts >> 14) & 0xFE)),
        (byte)(pts >> 7), (byte)(0x01 | ((pts << 1) & 0xFE))
    ];

    // pointer_field 加一张只含现在节目的 EIT 现在/下一个节目表（2021-09-21 06:45 UTC 起，45 分钟）。
    private static byte[] PresentSection(int serviceId, int eventId, string title)
    {
        var name = Encoding.ASCII.GetBytes(title);
        byte[] descriptor = [0x4D, (byte)(5 + name.Length), (byte)'e', (byte)'n', (byte)'g', (byte)name.Length, .. name, 0];
        byte[] header = [0x4E, 0, 0, (byte)(serviceId >> 8), (byte)serviceId, 0xC1, 0, 1, 0, 1, 0, 1, 1, 0x4E];
        // MJD 59478 = 2021-09-21。
        byte[] eventData = [(byte)(eventId >> 8), (byte)eventId, 0xE8, 0x56, 0x06, 0x45, 0x00, 0x00, 0x45, 0x00,
            (byte)(0x80 | (descriptor.Length >> 8)), (byte)descriptor.Length];
        byte[] section = [.. header, .. eventData, .. descriptor, 0, 0, 0, 0];
        var length = section.Length - 3;
        section[1] = (byte)(0xF0 | (length >> 8));
        section[2] = (byte)length;
        TsPsiSectionBuilder.WriteCrc(section);
        return [0, .. section];
    }

    /// <summary>统计实际读取的字节数。</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        private int Count(int read)
        {
            BytesRead += read;
            return read;
        }
    }
}
