using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.TransportStream.Processing;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Services.TransportStream.Analysis;
using TSCutter.GUI.Utils;
using TSCutter.GUI.Utils.TransportStream.Packets;

namespace TSCutter.GUI.Tests.Performance;

/// <summary>
/// 导出性能基准：用可复现的合成 TS 素材测量开/关导出期 TS 检测的耗时、吞吐与大致分配量。
/// 默认跳过；设置环境变量 TSCUTTER_PERF_BENCHMARK=1 运行（素材写入系统临时目录，不入库）。
/// 异步操作全程 await，避免同步阻塞线程池污染吞吐；复制场景先预热并交替测量，取三轮中位数；过滤和合并运行两轮取第二轮；数字仍受磁盘与 CPU 影响，
/// 只用于量级评估。分配量为进程累计分配差值，不是常驻内存峰值。
/// </summary>
public sealed class ExportTsCheckBenchmarks
{
    private const long TargetBytes = 256L * 1024 * 1024;
    private const int PmtPid = 0x0100;
    private const int VideoPid = 0x0101;
    private const int AudioPid = 0x0102;

    public sealed class PerfBenchmarkFactAttribute : FactAttribute
    {
        public PerfBenchmarkFactAttribute() =>
            Skip = Environment.GetEnvironmentVariable("TSCUTTER_PERF_BENCHMARK") == "1"
                ? null
                : "性能基准默认跳过：设置 TSCUTTER_PERF_BENCHMARK=1 后运行（会写入数百 MB 临时数据）。";
    }

    public ExportTsCheckBenchmarks(ITestOutputHelper output)
    {
        Output = output;
    }

    private ITestOutputHelper Output { get; }

    [PerfBenchmarkFact]
    public async Task MeasureExportChecking()
    {
        await CopyWithAndWithoutCheck();
        await DetectorOnlyFeed();
        await ProgramFilterWithAndWithoutCheck();
        await MergeWithAndWithoutCheck();
    }

    private async Task CopyWithAndWithoutCheck()
    {
        var (source, _) = await CreateSyntheticStreamAsync("copy");
        try
        {
            async Task CopyAsync(bool check)
            {
                var target = Path.Combine(Path.GetTempPath(), $"tscutter-perf-out-{Guid.NewGuid():N}.ts");
                try
                {
                    var session = check ? new ExportTsCheckSession(target) : null;
                    await CommonUtil.CopyFileAsync(new FileInfo(source), target, checkSession: session);
                    session?.Complete();
                    if (session is not null)
                    {
                        Assert.Equal(TsExportCheckState.Completed, session.State);
                        Assert.Equal(new FileInfo(source).Length, session.Result!.FileSize);
                        Assert.Equal(new FileInfo(source).Length / TsStreamAnalyzer.PacketSize, session.Result.PacketCount);
                    }
                }
                finally
                {
                    File.Delete(target);
                }
            }
            // 两种路径均先预热，测量时交换先后次序，减少 JIT 与缓存对比较的影响。
            for (var round = 0; round < 2; round++)
            {
                await CopyAsync(false);
                await CopyAsync(true);
            }
            var withoutRuns = new List<(TimeSpan Elapsed, double Speed, long Allocated, TimeSpan Cpu)>();
            var withRuns = new List<(TimeSpan Elapsed, double Speed, long Allocated, TimeSpan Cpu)>();
            for (var round = 0; round < 3; round++)
            {
                if (round % 2 == 0)
                {
                    withoutRuns.Add(await MeasureAsync(1, () => CopyAsync(false)));
                    withRuns.Add(await MeasureAsync(1, () => CopyAsync(true)));
                }
                else
                {
                    withRuns.Add(await MeasureAsync(1, () => CopyAsync(true)));
                    withoutRuns.Add(await MeasureAsync(1, () => CopyAsync(false)));
                }
            }
            var without = withoutRuns.OrderBy(item => item.Elapsed).ElementAt(1);
            var with = withRuns.OrderBy(item => item.Elapsed).ElementAt(1);
            Report("复制 256 MiB TS", without, with);
        }
        finally
        {
            File.Delete(source);
        }
    }

    private async Task DetectorOnlyFeed()
    {
        var (source, length) = await CreateSyntheticStreamAsync("detector");
        try
        {
            // 素材只在计时前读取一次，分离检测 CPU 开销与磁盘/文件缓存噪声。
            var data = await File.ReadAllBytesAsync(source);
            var runs = new List<TimeSpan>();
            for (var round = 0; round < 5; round++)
            {
                var session = new ExportTsCheckSession("detector-only");
                session.Begin();
                var stopwatch = Stopwatch.StartNew();
                for (var offset = 0; offset < data.Length; offset += 1024 * 1024)
                    session.Append(data.AsSpan(offset, Math.Min(1024 * 1024, data.Length - offset)));
                session.Complete();
                stopwatch.Stop();
                Assert.Equal(TsExportCheckState.Completed, session.State);
                Assert.Equal(data.Length / TsStreamAnalyzer.PacketSize, session.Result!.PacketCount);
                if (round >= 2)
                    runs.Add(stopwatch.Elapsed);
            }
            var elapsed = runs.OrderBy(item => item).ElementAt(1);
            Output.WriteLine($"场景: 纯检测喂入 256 MiB（计时内无文件 IO）: {elapsed.TotalSeconds:F3}s, {length / elapsed.TotalSeconds / (1024 * 1024):F0} MiB/s");
        }
        finally
        {
            File.Delete(source);
        }
    }

    private async Task ProgramFilterWithAndWithoutCheck()
    {
        var (source, length) = await CreateSyntheticStreamAsync("filter");
        var destination = Path.Combine(Path.GetTempPath(), $"tscutter-perf-filter-{Guid.NewGuid():N}.ts");
        try
        {
            var without = await MeasureAsync(2, () => new TsProgramClipExportService().ExportAsync(
                source, destination, 0, length, 1, null, default));
            File.Delete(destination);
            var with = await MeasureAsync(2, async () =>
            {
                var session = new ExportTsCheckSession(destination);
                session.Begin();
                await new TsProgramClipExportService().ExportAsync(
                    source, destination, 0, length, 1, null, default, session);
                session.Complete();
                Assert.Equal(TsExportCheckState.Completed, session.State);
            });
            Report("节目过滤 256 MiB TS", without, with);
        }
        finally
        {
            File.Delete(source);
            File.Delete(destination);
        }
    }

    private async Task MergeWithAndWithoutCheck()
    {
        var (source, length) = await CreateSyntheticStreamAsync("merge");
        var middle = length / 4 / 188 * 188 * 2;
        var destination = Path.Combine(Path.GetTempPath(), $"tscutter-perf-merge-{Guid.NewGuid():N}.ts");
        try
        {
            TsClipMergeRequest CreateRequest() => new()
            {
                SourcePath = source,
                OutputPath = destination,
                VideoPid = VideoPid,
                AudioPids = [AudioPid],
                Ranges =
                [
                    new TsClipMergeRange(0, middle, 0, middle / 188 / 200.0),
                    new TsClipMergeRange(middle, length, middle / 188 / 200.0, length / 188 / 200.0)
                ]
            };
            var without = await MeasureAsync(2, () =>
                new TsClipMergeService().MergeAsync(CreateRequest(), null, default));
            File.Delete(destination);
            var with = await MeasureAsync(2, async () =>
            {
                var session = new ExportTsCheckSession(destination);
                session.Begin();
                await new TsClipMergeService().MergeAsync(CreateRequest(), null, default, false, session);
                session.Complete();
                Assert.Equal(TsExportCheckState.Completed, session.State);
            });
            Report("两段合并 256 MiB TS", without, with);
        }
        finally
        {
            File.Delete(source);
            File.Delete(destination);
        }
    }

    private async Task<(TimeSpan Elapsed, double Speed, long Allocated, TimeSpan Cpu)> MeasureAsync(int rounds, Func<Task> action)
    {
        var result = (TimeSpan.Zero, 0.0, 0L, TimeSpan.Zero);
        using var process = Process.GetCurrentProcess();
        for (var round = 1; round <= rounds; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var cpuBefore = process.TotalProcessorTime;
            var stopwatch = Stopwatch.StartNew();
            await action();
            stopwatch.Stop();
            result = (stopwatch.Elapsed, TargetBytes / stopwatch.Elapsed.TotalSeconds / (1024 * 1024),
                GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore, process.TotalProcessorTime - cpuBefore);
        }
        return result;
    }

    private void Report(string scenario, (TimeSpan Elapsed, double Speed, long Allocated, TimeSpan Cpu) without,
        (TimeSpan Elapsed, double Speed, long Allocated, TimeSpan Cpu) with)
    {
        Output.WriteLine($"场景: {scenario}");
        Output.WriteLine($"  关闭检测: {without.Elapsed.TotalSeconds:F2}s, {without.Speed:F0} MiB/s, 分配 {without.Allocated / (1024 * 1024):F0} MiB, CPU {without.Cpu.TotalSeconds:F3}s");
        Output.WriteLine($"  开启检测: {with.Elapsed.TotalSeconds:F2}s, {with.Speed:F0} MiB/s, 分配 {with.Allocated / (1024 * 1024):F0} MiB, CPU {with.Cpu.TotalSeconds:F3}s");
        Output.WriteLine($"  耗时增幅: {(with.Elapsed.TotalSeconds / Math.Max(0.001, without.Elapsed.TotalSeconds) - 1) * 100:F1}%");
    }

    /// <summary>生成带 PAT/PMT、PCR、PES 的合成 TS；跨组保持 CC 和时间戳连续，避免人为制造错误风暴。</summary>
    private static async Task<(string Path, long Length)> CreateSyntheticStreamAsync(string tag)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tscutter-perf-src-{tag}-{Guid.NewGuid():N}.ts");
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
        {
            for (var groupIndex = 0; stream.Position < TargetBytes; groupIndex++)
                await stream.WriteAsync(BuildPacketGroup(groupIndex));
        }
        return (path, new FileInfo(path).Length);
    }

    private static byte[] BuildPacketGroup(int groupIndex)
    {
        var packets = new List<byte[]>(2 + 200 * 3)
        {
            CreatePsiPacket(0x0000, 0, BuildPatSection(PmtPid)),
            CreatePsiPacket(PmtPid, 0, BuildPmtSection(VideoPid, (0x1B, VideoPid), (0x03, AudioPid)))
        };
        var videoContinuity = 1;
        var audioContinuity = 1 + groupIndex * 200;
        for (var index = 0; index < 200; index++)
        {
            var pts = 90_000L + (groupIndex * 200L + index) * 3_000;
            packets.Add(CreatePesPacket(VideoPid, videoContinuity++ & 0x0F, pts));
            packets.Add(CreatePacket(VideoPid, videoContinuity++ & 0x0F, pcrBase: pts));
            packets.Add(CreatePesPacket(AudioPid, audioContinuity++ & 0x0F, pts + 1_000));
        }
        using var output = new MemoryStream(packets.Count * 188);
        foreach (var packet in packets)
            output.Write(packet);
        return output.ToArray();
    }

    private static byte[] CreatePacket(int pid, int continuityCounter, long? pcrBase = null)
    {
        var packet = new byte[TsUtil.TsPacketSize];
        Array.Fill(packet, (byte)0x55);
        packet[0] = 0x47;
        packet[1] = (byte)((pid >> 8) & 0x1F);
        packet[2] = (byte)pid;
        packet[3] = (byte)(0x10 | (continuityCounter & 0x0F));
        if (pcrBase is not { } pcr)
            return packet;

        packet[3] = (byte)(0x30 | (continuityCounter & 0x0F));
        packet[4] = 7;
        packet[5] = 0x10;
        packet[6] = (byte)(pcr >> 25);
        packet[7] = (byte)(pcr >> 17);
        packet[8] = (byte)(pcr >> 9);
        packet[9] = (byte)(pcr >> 1);
        packet[10] = (byte)(((pcr & 1) << 7) | 0x7E);
        packet[11] = 0;
        return packet;
    }

    private static byte[] CreatePsiPacket(int pid, int continuityCounter, byte[] section)
    {
        var packet = CreatePacket(pid, continuityCounter);
        packet[1] |= 0x40;
        packet[4] = 0;
        section.CopyTo(packet.AsSpan(5));
        packet.AsSpan(5 + section.Length).Fill(0xFF);
        return packet;
    }

    private static byte[] CreatePesPacket(int pid, int continuityCounter, long pts)
    {
        var packet = CreatePacket(pid, continuityCounter);
        packet[1] |= 0x40;
        var payload = packet.AsSpan(4);
        payload[0] = 0;
        payload[1] = 0;
        payload[2] = 1;
        payload[3] = 0xE0;
        payload[4] = 0;
        payload[5] = 0;
        payload[6] = 0x80;
        payload[7] = 0x80;
        payload[8] = 5;
        WritePts(payload.Slice(9, 5), pts);
        return packet;
    }

    private static byte[] BuildPatSection(int pmtPid) => AppendCrc([
        0x00, 0xB0, 0x0D, 0x00, 0x01, 0xC1, 0x00, 0x00,
        0x00, 0x01, (byte)(0xE0 | (pmtPid >> 8)), (byte)pmtPid
    ]);

    private static byte[] BuildPmtSection(int pcrPid, params (int StreamType, int Pid)[] streams)
    {
        var sectionLength = 9 + streams.Length * 5 + 4;
        var section = new List<byte>
        {
            0x02, (byte)(0xB0 | (sectionLength >> 8)), (byte)sectionLength,
            0x00, 0x01, 0xC1, 0x00, 0x00,
            (byte)(0xE0 | (pcrPid >> 8)), (byte)pcrPid, 0xF0, 0x00
        };
        foreach (var stream in streams)
        {
            section.Add((byte)stream.StreamType);
            section.Add((byte)(0xE0 | (stream.Pid >> 8)));
            section.Add((byte)stream.Pid);
            section.Add(0xF0);
            section.Add(0x00);
        }
        return AppendCrc(section.ToArray());
    }

    private static byte[] AppendCrc(byte[] section)
    {
        uint crc = uint.MaxValue;
        foreach (var value in section)
        {
            crc ^= (uint)value << 24;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        }
        return [.. section, (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc];
    }

    private static void WritePts(Span<byte> value, long pts)
    {
        value[0] = (byte)(0x21 | (((pts >> 30) & 0x07) << 1));
        value[1] = (byte)(pts >> 22);
        value[2] = (byte)(0x01 | (((pts >> 15) & 0x7F) << 1));
        value[3] = (byte)(pts >> 7);
        value[4] = (byte)(0x01 | ((pts & 0x7F) << 1));
    }
}
