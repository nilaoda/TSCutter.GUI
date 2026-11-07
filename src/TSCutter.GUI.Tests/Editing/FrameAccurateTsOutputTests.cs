using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.TransportStream.Analysis;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Services.TransportStream.Analysis;
using TSCutter.GUI.Tests.FFmpeg;
using Xunit;

namespace TSCutter.GUI.Tests.Editing;

/// <summary>
/// TS → TS 智能剪辑按源传输包写出：原片的传输层异常在输出中原样出现（TSCutter 检测得到同样的事件），
/// 剪接、改写、补包与删包本身不产生任何异常。
/// </summary>
public sealed class FrameAccurateTsOutputTests
{
    private static string Fixture(string name) => Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), name);

    private static string Temporary(string extension) =>
        Path.Combine(Path.GetTempPath(), $"tscutter-ts-output-{Guid.NewGuid():N}{extension}");

    /// <summary>
    /// 接收中断的录制：中断处的 CC 跳号（视频、PAT、PMT、SDT）、视频与音频 PTS 跳变、PCR 跳变，
    /// 跨中断剪切与几乎整段剪切后，输出检测到的事件与源一一对应（类型、PID、缺失包数、跳变量）。
    /// </summary>
    [NativeMediaFact("frameaccurate")]
    public async Task KeepsTheSignalLossOfTheSource()
    {
        var source = Fixture("h264-422-loss.ts");
        var frames = FrameAccurateExportTests.Decode(source, out _, explode: false).OrderBy(frame => frame.Pts).ToList();
        var gap = Enumerable.Range(1, frames.Count - 1).Single(i => frames[i].Pts - frames[i - 1].Pts > 2 * 3600);
        var expected = Signature(await new TsStreamAnalyzer().AnalyzeAsync(source));
        Assert.Contains(expected, item => item.StartsWith("ContinuityGap/256/"));
        Assert.Contains(expected, item => item.StartsWith("PcrJump/256/"));
        foreach (var (first, last) in new[] { (gap - 30, gap + 40), (5, frames.Count - 5) })
        {
            var output = Temporary(".ts");
            try
            {
                await Export(source, output, frames[first].Pts, frames[last].Pts);
                Assert.Equal(expected, Signature(await new TsStreamAnalyzer().AnalyzeAsync(output)));
            }
            finally { File.Delete(output); }
        }
    }

    /// <summary>
    /// 在干净素材的副本中注入传输错误标志（TEI）、重复包与丢包，位置都在复制区中间：
    /// 闭合 GOP（复制区原样写出）与只有普通 I 帧的开放 GOP（复制区每个画面的切片头都要改写、写回原包位置）
    /// 剪切后，输出检测到同样的 TEI、重复包与 CC 跳号，且没有其他事件。
    /// </summary>
    [NativeMediaTheory("frameaccurate")]
    [InlineData("h264.ts")]
    [InlineData("open-gop-flat.ts")]
    [InlineData("h264-422-10bit.ts")]
    public async Task KeepsInjectedTransportErrors(string name)
    {
        var source = Temporary(".ts");
        var output = Temporary(".ts");
        try
        {
            Inject(Fixture(name), source);
            var expected = Signature(await new TsStreamAnalyzer().AnalyzeAsync(source));
            Assert.Contains(expected, item => item.StartsWith("TransportError/256"));
            Assert.Contains(expected, item => item.StartsWith("DuplicatePacket/256"));
            Assert.Contains(expected, item => item.StartsWith("ContinuityGap/256"));
            var frames = FrameAccurateExportTests.Decode(source, out _, explode: false).OrderBy(frame => frame.Pts).ToList();
            var service = new FrameAccurateExportService();
            await Export(source, output, frames[17].Pts, frames[^23].Pts, service);
            var report = Assert.Single(service.Reports);
            Assert.True(report.EncodedFrames > 0 && report.CopiedPackets > 0);
            Assert.Equal(expected, Signature(await new TsStreamAnalyzer().AnalyzeAsync(output)));
        }
        finally
        {
            File.Delete(source);
            File.Delete(output);
        }
    }

    /// <summary>
    /// 干净素材（闭合/开放 GOP、隔行 MBAFF、MPEG-2、HEVC（含 CRA 开放 GOP）、10 位 4:2:2、33 位 PTS 回绕、M2TS）剪切后检测无任何错误与警告：
    /// 重编码画面填入原画面位置、改写 PES 写回、补包与删包后，CC、PCR、时间戳与 PES 长度都连续合规。
    /// 起点落在开放 GOP I 帧的前导画面上时，片头画面在源中排在 I 帧之后、没有可用位置，全部在复制起点前补包写出。
    /// </summary>
    [NativeMediaTheory("frameaccurate")]
    [InlineData("h264.ts", 17)]
    [InlineData("h264.m2ts", 17)]
    [InlineData("open-gop-flat.ts", 17)]
    [InlineData("open-gop-flat.ts", 74)]
    [InlineData("open-gop-mbaff.ts", 17)]
    [InlineData("mpeg2.ts", 17)]
    [InlineData("hevc-hlg.ts", 17)]
    [InlineData("hevc-422.ts", 17)]
    [InlineData("hevc-cra.ts", 17)]
    [InlineData("h264-422-10bit.ts", 17)]
    [InlineData("h264-422-10bit.ts", 149)]
    [InlineData("h264-422-interlaced.ts", 17)]
    [InlineData("wrap.ts", 17)]
    public async Task AddsNoTransportErrors(string name, int start)
    {
        var source = Fixture(name);
        var reference = await new TsStreamAnalyzer().AnalyzeAsync(source);
        Assert.Equal(0, reference.TotalErrorCount + reference.TotalWarningCount);
        var frames = FrameAccurateExportTests.Decode(source, out _, explode: false).OrderBy(frame => frame.Pts).ToList();
        var output = Temporary(Path.GetExtension(name));
        try
        {
            await Export(source, output, frames[start].Pts, frames[^23].Pts);
            var result = await new TsStreamAnalyzer().AnalyzeAsync(output);
            Assert.Empty(Signature(result));
            Assert.Equal(reference.PacketSize, result.PacketSize);
        }
        finally { File.Delete(output); }
    }

    /// <summary>
    /// 双节目 TS：只输出预览视频所在的节目 1，节目表按该节目重建（与关键帧剪辑的按节目导出相同），节目 2 的 PID 不出现；
    /// 帧号连续、检测无错误；节目 1 视频上注入的 TEI、重复包、丢包与填充包在输出中同样检出。
    /// </summary>
    [NativeMediaTheory("frameaccurate")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WritesOnlyTheSelectedProgram(bool inject)
    {
        var source = Fixture("two-programs.ts");
        var copy = inject ? Temporary(".ts") : null;
        var output = Temporary(".ts");
        try
        {
            if (copy is not null)
            {
                Inject(source, copy);
                source = copy;
            }
            var reference = await new TsStreamAnalyzer().AnalyzeAsync(source);
            Assert.Equal(2, reference.Programs.Count);
            var frames = FrameAccurateExportTests.Decode(source, out _, explode: false).OrderBy(frame => frame.Pts).ToList();
            var start = frames[17].Pts;
            var end = frames[^23].Pts;
            var service = new FrameAccurateExportService();
            await service.ExportAsync(new FrameAccurateExportRequest(output, [new FrameAccurateExportSegment(source, start, end,
                new FrameAccurateExportSettings { Container = "ts", VideoIndex = 0, StreamIndices = [0, 1] })]));
            var report = Assert.Single(service.Reports);
            Assert.True(report.EncodedFrames > 0 && report.CopiedPackets > 0);
            var result = await new TsStreamAnalyzer().AnalyzeAsync(output);
            var program = Assert.Single(result.Programs).Value;
            Assert.Equal(reference.Programs.OrderBy(pair => pair.Key).First().Value.PmtPid, program.PmtPid);
            Assert.DoesNotContain(0x102, result.Pids.Keys);
            Assert.DoesNotContain(0x103, result.Pids.Keys);
            Assert.Equal(Signature(reference), Signature(result));
            var numbers = FrameAccurateExportTests.Decode(output, out _, explode: false).OrderBy(frame => frame.Pts)
                .Select(frame => frame.Number).ToList();
            Assert.Equal(Enumerable.Range(17, frames.Count - 23 - 17 + 1), numbers);
        }
        finally
        {
            if (copy is not null)
                File.Delete(copy);
            File.Delete(output);
        }
    }

    private static Task Export(string source, string output, long start, long end, FrameAccurateExportService? service = null) =>
        (service ?? new FrameAccurateExportService()).ExportAsync(new FrameAccurateExportRequest(output,
            [new FrameAccurateExportSegment(source, start, end, FrameAccurateExportTests.Settings(source))]));

    /// <summary>检测事件的可比较摘要：类型、PID 与关键参数（CC 缺失包数、跳变量取 0.1 秒）。</summary>
    private static List<string> Signature(TsCheckResult result) => result.Events
        .Select(item => $"{item.Type}/{item.Pid}/" + item.Type switch
        {
            TsCheckEventType.ContinuityGap => item.MessageArguments[^1].ToString(),
            TsCheckEventType.PcrJump or TsCheckEventType.PtsJump =>
                Math.Round(Convert.ToDouble(item.MessageArguments[0]), 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => ""
        })
        .Order(StringComparer.Ordinal)
        .ToList();

    /// <summary>
    /// 复制素材并在视频 PID 上注入异常：约 45% 处置 TEI，约 52% 处的包重复一次，约 60% 处的包删去（CC 跳号）。
    /// 另外每隔 40 个视频包插入一个只有适配字段的填充包（不带负载、CC 不变，合规；广播素材中常见），
    /// 覆盖复制、改写、重编码与删去的各段。
    /// </summary>
    private static void Inject(string from, string to)
    {
        var data = File.ReadAllBytes(from);
        var packets = Enumerable.Range(0, data.Length / 188).Select(i => data.AsSpan(i * 188, 188).ToArray()).ToList();
        var videoPackets = 0;
        for (var i = 0; i < packets.Count; i++)
        {
            var p = packets[i];
            if ((((p[1] & 0x1F) << 8) | p[2]) != 256 || (p[3] & 0x10) == 0 || ++videoPackets % 40 != 0)
                continue;
            var stuffing = new byte[188];
            stuffing.AsSpan().Fill(0xFF);
            stuffing[0] = 0x47;
            stuffing[1] = 0x01;
            stuffing[2] = 0x00;
            stuffing[3] = (byte)(0x20 | (p[3] & 0x0F));
            stuffing[4] = 183;
            stuffing[5] = 0x00;
            packets.Insert(++i, stuffing);
        }
        int Find(double fraction)
        {
            for (var i = (int)(packets.Count * fraction); i < packets.Count; i++)
            {
                var p = packets[i];
                if ((((p[1] & 0x1F) << 8) | p[2]) == 256 && (p[1] & 0x40) == 0 && (p[3] & 0x30) == 0x10)
                    return i;
            }
            throw new InvalidOperationException("no video packet");
        }
        var error = Find(0.45);
        packets[error][1] |= 0x80;
        var duplicate = Find(0.52);
        packets.Insert(duplicate + 1, (byte[])packets[duplicate].Clone());
        packets.RemoveAt(Find(0.60));
        File.WriteAllBytes(to, packets.SelectMany(packet => packet).ToArray());
    }
}
