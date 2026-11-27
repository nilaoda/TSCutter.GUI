using TSCutter.GUI.Services.TransportStream.Analysis;
using TSCutter.GUI.Utils.Media;
using Xunit;

namespace TSCutter.GUI.Tests.TransportStream.Analysis;

public class TsBroadcastTimecodeProbeTests
{
    [Fact]
    public void ReadsTdtTimeFromSyntheticTransportStream()
    {
        var expected = new DateTimeOffset(2026, 11, 27, 12, 34, 56, TimeSpan.Zero);
        var mjd = (expected.DateTime - new DateTime(1858, 11, 17)).Days;
        var section = new byte[]
        {
            0x70, 0x70, 0x05,
            (byte)(mjd >> 8), (byte)mjd,
            Bcd(expected.Hour), Bcd(expected.Minute), Bcd(expected.Second)
        };
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = 0x40;
        packet[2] = 0x14;
        packet[3] = 0x10;
        packet[4] = 0x00; // pointer_field
        section.CopyTo(packet, 5);
        for (var i = 5 + section.Length; i < packet.Length; i++)
            packet[i] = 0xFF;

        var path = Path.Combine(Path.GetTempPath(), "tscutter-tdt-" + Guid.NewGuid().ToString("N") + ".ts");
        File.WriteAllBytes(path, packet);
        try
        {
            var utc = TsBroadcastTimecodeProbe.TryReadFirstUtc(path);
            Assert.Equal(expected, utc);
            Assert.Equal("12:34:56:00", TimecodeUtil.FromUtc(utc!.Value, 25).StartText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte Bcd(int value) => (byte)(((value / 10) << 4) | (value % 10));
}
