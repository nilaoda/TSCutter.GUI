using Avalonia.Headless;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Models.TransportStream.Analysis;
using TSCutter.GUI.Services.TransportStream.Analysis;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Tests.UI;
using TSCutter.GUI.Utils.TransportStream.Packets;
using Xunit;

namespace TSCutter.GUI.Tests.TransportStream.Analysis;

/// <summary>
/// 加扰探测只计入节目表列出的基本流：有些录制只解密了节目使用的流，
/// 节目表之外的 PID 仍带加扰标志，不应因此拒绝预览。
/// </summary>
public sealed class TsScramblingProgramTableTests
{
    private const int PmtPid = 0x101;
    private const int VideoPid = 0x111;
    private const int AudioPid = 0x112;
    private const int UnlistedPid = 0x125;
    private const int OtherUnlistedPid = 0x135;

    [Fact]
    public void ScramblingOnPidsOutsideTheProgramTablesIsIgnored()
    {
        var packets = new List<byte[]> { Scrambled(UnlistedPid) };
        packets.AddRange(Tables([(1, PmtPid)], (1, PmtPid, [VideoPid, AudioPid])));
        for (var i = 0; i < 20; i++)
        {
            packets.Add(Clear(VideoPid, i));
            packets.Add(Scrambled(i % 2 == 0 ? UnlistedPid : OtherUnlistedPid));
        }
        Assert.False(ProbeFile(packets).HasScrambledPayload);
    }

    [Theory]
    [InlineData(true, VideoPid)]
    [InlineData(false, VideoPid)]
    [InlineData(false, AudioPid)]
    public void ScramblingOnProgramStreamsIsStillDetected(bool beforeTables, int scrambledPid)
    {
        var packets = new List<byte[]>();
        if (beforeTables)
            packets.Add(Scrambled(scrambledPid));
        packets.AddRange(Tables([(1, PmtPid)], (1, PmtPid, [VideoPid, AudioPid])));
        packets.Add(Clear(VideoPid, 0));
        if (!beforeTables)
            packets.Add(Scrambled(scrambledPid));
        Assert.True(ProbeFile(packets).HasScrambledPayload);
    }

    [Fact]
    public void ScramblingCountsWhileAProgramMapIsMissing()
    {
        // PAT 列出的第二个节目没有 PMT，无法确认加扰 PID 不属于它。
        var packets = Tables([(1, PmtPid), (2, 0x102)], (1, PmtPid, [VideoPid])).ToList();
        packets.Add(Scrambled(UnlistedPid));
        Assert.True(ProbeFile(packets).HasScrambledPayload);
    }

    [Fact]
    public void ScramblingCountsWithoutProgramTables()
    {
        Assert.True(ProbeFile([Clear(VideoPid, 0), Scrambled(UnlistedPid), Clear(VideoPid, 1)]).HasScrambledPayload);
    }

    [Fact]
    public void ScrambledProgramMapIsNotTrusted()
    {
        var packets = new List<byte[]>
        {
            Section(0, TsPsiSectionBuilder.BuildPat(1, 0, [new(1, PmtPid)]), 0),
            Section(PmtPid, BuildPmt(1, [VideoPid]), 0)
        };
        packets[1][3] = (byte)(0x90 | (packets[1][3] & 0x0f));
        packets.Add(Scrambled(UnlistedPid));
        Assert.True(ProbeFile(packets).HasScrambledPayload);
    }

    internal static void InsertScrambledPackets(string source, string output, int pid)
    {
        var data = File.ReadAllBytes(source);
        Assert.Equal(0, data.Length % TsUtil.TsPacketSize);
        Assert.Equal(TsUtil.TsSyncByte, data[0]);
        using var stream = File.Create(output);
        for (var offset = 0; offset < data.Length; offset += TsUtil.TsPacketSize)
        {
            stream.Write(data, offset, TsUtil.TsPacketSize);
            if (offset / TsUtil.TsPacketSize % 20 == 10)
                stream.Write(Scrambled(pid));
        }
    }

    private static TsScramblingProbe.ProbeResult ProbeFile(IEnumerable<byte[]> packets)
    {
        var path = Temporary();
        try
        {
            // 末尾补空包，保证短输入也能建立包同步。
            var padding = Enumerable.Range(0, 16).Select(_ => Header(0x1fff, 0, 0x10));
            File.WriteAllBytes(path, packets.Concat(padding).SelectMany(packet => packet).ToArray());
            var result = TsScramblingProbe.Probe(path);
            Assert.True(result.Is188ByteTransportStream);
            return result;
        }
        finally { File.Delete(path); }
    }

    internal static string Temporary() => Path.Combine(Path.GetTempPath(), $"ts-scrambling-{Guid.NewGuid():N}.ts");

    private static IEnumerable<byte[]> Tables((int Number, int Pid)[] programs,
        (int Number, int Pid, int[] Streams) map)
    {
        yield return Section(0, TsPsiSectionBuilder.BuildPat(1, 0,
            programs.Select(program => new TsPsiSectionBuilder.PatProgram(program.Number, program.Pid)).ToList()), 0);
        yield return Section(map.Pid, BuildPmt(map.Number, map.Streams), 0);
    }

    private static byte[] BuildPmt(int number, int[] streams) =>
        TsPsiSectionBuilder.BuildPmt(number, 0, streams[0], [], streams
            .Select(pid => new TsPsiSectionBuilder.PmtStream(pid,
                new TsStreamDefinition { StreamType = pid == VideoPid ? (byte)0x02 : (byte)0x0f }))
            .ToList());

    private static byte[] Section(int pid, byte[] section, int continuity)
    {
        var packet = Header(pid, 0x40, 0x10 | continuity);
        packet[4] = 0;
        section.CopyTo(packet, 5);
        return packet;
    }

    private static byte[] Clear(int pid, int continuity)
    {
        var packet = Header(pid, 0, 0x10 | (continuity & 0x0f));
        packet.AsSpan(4).Fill(0x5a);
        return packet;
    }

    private static byte[] Scrambled(int pid)
    {
        var packet = Header(pid, 0, 0x90);
        // 固定内容即可：探测只看包头的加扰标志。
        new Random(pid).NextBytes(packet.AsSpan(4));
        return packet;
    }

    private static byte[] Header(int pid, int flags, int control)
    {
        var packet = new byte[TsUtil.TsPacketSize];
        packet.AsSpan().Fill(0xff);
        packet[0] = TsUtil.TsSyncByte;
        packet[1] = (byte)(flags | (pid >> 8));
        packet[2] = (byte)pid;
        packet[3] = (byte)control;
        return packet;
    }
}

/// <summary>节目表之外的加扰 PID 不影响真实视频的预览；预览的视频流加扰时仍提示先解密。</summary>
[Collection("Avalonia UI")]
public sealed class TsScramblingPreviewTests
{
    [NativeMediaFact("frameaccurate")]
    public async Task VideoOpensWithScrambledPidsOutsideTheProgramTables()
    {
        var source = Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), "mpeg2.ts");
        var unlisted = TsScramblingProgramTableTests.Temporary();
        var listed = TsScramblingProgramTableTests.Temporary();
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        try
        {
            await headless.Dispatch<bool>(async () =>
            {
                int videoPid;
                using (var reference = new VideoInstance(source))
                {
                    await reference.InitVideoAsync();
                    videoPid = reference.VideoPid;
                }
                Assert.NotEqual(0x125, videoPid);

                // 节目表之外的 PID 全程带加扰标志：预览照常打开并解码首帧。
                TsScramblingProgramTableTests.InsertScrambledPackets(source, unlisted, 0x125);
                using (var video = new VideoInstance(unlisted))
                {
                    await video.InitVideoAsync();
                    Assert.True(video.Inited);
                    Assert.True(video.CanBinaryClip);
                    var frame = await video.DecodeAtTimeAsync(TimeSpan.Zero);
                    Assert.NotNull(frame);
                    Assert.True(frame.Bitmap is not null || frame.BitmapLease is not null || frame.GpuFrame is not null);
                    frame.BitmapLease?.Dispose();
                    if (frame.BitmapLease is null)
                        frame.Bitmap?.Dispose();
                    frame.GpuFrame?.Dispose();
                }

                // 预览的视频流加扰时仍提示先解密。
                TsScramblingProgramTableTests.InsertScrambledPackets(source, listed, videoPid);
                using (var video = new VideoInstance(listed))
                {
                    await Assert.ThrowsAsync<ScrambledTsException>(() => video.InitVideoAsync());
                    Assert.False(video.Inited);
                }
                return true;
            }, CancellationToken.None);
        }
        finally
        {
            File.Delete(unlisted);
            File.Delete(listed);
        }
    }
}
