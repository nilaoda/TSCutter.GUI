using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.Models.Media;
using TSCutter.GUI.Player.Audio;
using TSCutter.GUI.Services.Editing.AudioRebuild;
using TSCutter.GUI.Services.Editing.DolbyE;
using TSCutter.GUI.Utils.Media;
using TSCutter.GUI.Utils.Media.DolbyE;
using Xunit;

namespace TSCutter.GUI.Tests.Media;

public sealed class DolbyERemuxTests
{
    // 可选真实素材：目录中放带杜比 E 的 TS，参见 README。
    private const string SampleVariable = "TSCUTTER_DOLBY_E_SAMPLES";

    [FFmpeg.NativeMediaTheory(sampleVariable: SampleVariable)]
    [InlineData(false)]
    [InlineData(true)]
    public void RealSampleRemuxAddsPlayableTracksAndKeepsOtherPacketsUnchanged(bool keepOriginal)
    {
        foreach (var source in DolbyETests.RealSamples())
            RemuxSample(source, keepOriginal);
    }

    private static void RemuxSample(string source, bool keepOriginal)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tscutter-dolbye-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            // 截取开头约 24 MB（整包）作为导出结果。
            var input = Path.Combine(directory, "input.ts");
            using (var reader = File.OpenRead(source))
            using (var writer = File.Create(input))
            {
                var buffer = new byte[188 * 133_000];
                var read = reader.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) / 188 * 188;
                InjectTransportErrors(buffer.AsSpan(0, read));
                writer.Write(buffer, 0, read);
            }
            DolbyETrackInfo track;
            using (var probe = InterruptibleInputFormatContext.Open(input, static () => false))
            {
                probe.LoadStreamInfo();
                track = Assert.Single(DolbyEProbe.ProbeCore(probe, CancellationToken.None).Values);
            }
            var output = Path.Combine(directory, "out.ts");
            var plan = new AudioRebuildPlan
            {
                Tracks =
                [
                    new DolbyETrackSettings
                    {
                        Pid = track.Pid, ProgramConfig = track.Header.ProgramConfig, Transcode = true, KeepOriginal = keepOriginal,
                        Outputs = track.Programs.SelectMany(program => new[]
                        {
                            new DolbyEOutput { Program = program.Number, Codec = AudioOutputCodec.Ac3, BitrateKbps = program.ChannelCount > 2 ? 448 : 192, Language = "eng" },
                            new DolbyEOutput { Program = program.Number, Codec = AudioOutputCodec.Aac, Layout = AudioOutputLayout.Stereo, BitrateKbps = 192, Language = "eng" },
                            new DolbyEOutput { Program = program.Number, Codec = AudioOutputCodec.Eac3, BitrateKbps = program.ChannelCount > 2 ? 448 : 192 },
                            new DolbyEOutput { Program = program.Number, Codec = AudioOutputCodec.Pcm },
                            new DolbyEOutput { Program = program.Number, Codec = AudioOutputCodec.Flac }
                        }).ToList()
                    }
                ]
            };
            // 像导出服务一样写入输出流：分块大小不定，包会跨越分块。
            var setup = new AudioRebuildSetup(plan, input, output, null);
            TsOutputFile.Current = setup;
            try
            {
                using var stream = TsOutputFile.Create(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1,
                    FileOptions.SequentialScan);
                var data = File.ReadAllBytes(input);
                var random = new Random(7);
                for (var offset = 0; offset < data.Length;)
                {
                    var count = Math.Min(data.Length - offset, random.Next(1, 300_000));
                    stream.Write(data, offset, count);
                    offset += count;
                }
            }
            finally
            {
                TsOutputFile.Current = null;
            }
            Assert.Null(setup.Error);
            Assert.True(setup.Changed);
            Assert.True(setup.DecodedFrames > 100, $"decoded {setup.DecodedFrames}");
            var sideFiles = setup.Commit();
            Assert.Equal(track.Programs.Count, sideFiles.Count);
            Assert.All(sideFiles, file => Assert.True(new FileInfo(file).Length > 10_000));

            // 新音轨都能被解复用器识别并解码。
            using (var reopened = InterruptibleInputFormatContext.Open(output, static () => false))
            {
                reopened.LoadStreamInfo();
                var codecs = reopened.Streams.Select(stream => stream.Codecpar!.CodecId).ToList();
                var programs = track.Programs.Count;
                Assert.Equal(programs, codecs.Count(id => id == AVCodecID.AV_CODEC_ID_AC3));
                Assert.Equal(programs, codecs.Count(id => id == AVCodecID.AV_CODEC_ID_EAC3));
                Assert.Equal(programs, codecs.Count(id => id == AVCodecID.AV_CODEC_ID_AAC));
                // 302M：新 PCM 每个声道组一条，保留原轨时另有原杜比 E 轨。
                Assert.Equal(programs + (keepOriginal ? 1 : 0), codecs.Count(id => id == AVCodecID.AV_CODEC_ID_S302M));
                var decoders = reopened.Streams.Where(stream => stream.Codecpar!.CodecId is AVCodecID.AV_CODEC_ID_AC3
                        or AVCodecID.AV_CODEC_ID_EAC3 or AVCodecID.AV_CODEC_ID_AAC or AVCodecID.AV_CODEC_ID_S302M
                        && stream.Id != track.Pid)
                    .ToDictionary(stream => stream.Index, stream => AudioPlaybackPipeline.TryOpenDecoder(stream)!);
                var decoded = new Dictionary<int, int>();
                using var frame = new global::TSCutter.GUI.FFmpeg.Frame();
                foreach (var packet in reopened.ReadPackets())
                {
                    if (!decoders.TryGetValue(packet.StreamIndex, out var decoder))
                        continue;
                    foreach (var _ in decoder.DecodePacket(packet, frame))
                        decoded[packet.StreamIndex] = decoded.GetValueOrDefault(packet.StreamIndex) + 1;
                }
                foreach (var decoder in decoders.Values)
                    decoder.Dispose();
                Assert.Equal(decoders.Count, decoded.Count);
                Assert.All(decoded.Values, count => Assert.True(count > 50, $"decoded {count} frames"));
            }

            // 原文件中除杜比 E 与 PMT 以外的 PID：包逐字节不变，顺序也不变。
            var pids = PidsOf(input);
            var pmt = PmtPids(input);
            var compared = pids.Where(pid => pid != track.Pid && !pmt.Contains(pid)).ToHashSet();
            Assert.Equal(PacketsOf(input, compared), PacketsOf(output, compared));
            if (keepOriginal)
                Assert.Equal(PacketsOf(input, new HashSet<int> { track.Pid }), PacketsOf(output, new HashSet<int> { track.Pid }));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// 在截取的导出结果中制造传输错误：每隔一段给包置 TEI、把 CC 改成跳号、用前一个包覆盖成重复包（PAT 除外）。
    /// 重组后这些包必须原样保留。
    /// </summary>
    private static void InjectTransportErrors(Span<byte> data)
    {
        var count = data.Length / 188;
        for (var index = 997; index < count; index += 997)
        {
            var packet = data.Slice(index * 188, 188);
            if ((((packet[1] & 0x1F) << 8) | packet[2]) == 0)
                continue;
            switch (index / 997 % 3)
            {
                case 0:
                    packet[1] |= 0x80;
                    break;
                case 1:
                    packet[3] = (byte)((packet[3] & 0xF0) | ((packet[3] + 5) & 0x0F));
                    break;
                default:
                    data.Slice((index - 1) * 188, 188).CopyTo(packet);
                    break;
            }
        }
    }

    private static HashSet<int> PidsOf(string path)
    {
        var data = File.ReadAllBytes(path);
        var result = new HashSet<int>();
        for (var offset = 0; offset + 188 <= data.Length; offset += 188)
            result.Add(((data[offset + 1] & 0x1F) << 8) | data[offset + 2]);
        return result;
    }

    private static HashSet<int> PmtPids(string path)
    {
        var data = File.ReadAllBytes(path);
        var result = new HashSet<int>();
        for (var offset = 0; offset + 188 <= data.Length; offset += 188)
        {
            var pid = ((data[offset + 1] & 0x1F) << 8) | data[offset + 2];
            if (pid != 0 || (data[offset + 1] & 0x40) == 0)
                continue;
            var payload = offset + 4 + ((data[offset + 3] & 0x20) != 0 ? 1 + data[offset + 4] : 0);
            var start = payload + 1 + data[payload];
            var length = ((data[start + 1] & 0x0F) << 8) | data[start + 2];
            for (var i = start + 8; i + 4 <= start + 3 + length - 4; i += 4)
                result.Add(((data[i + 2] & 0x1F) << 8) | data[i + 3]);
        }
        return result;
    }

    private static List<string> PacketsOf(string path, IReadOnlySet<int> pids)
    {
        var data = File.ReadAllBytes(path);
        var result = new List<string>();
        for (var offset = 0; offset + 188 <= data.Length; offset += 188)
            if (pids.Contains(((data[offset + 1] & 0x1F) << 8) | data[offset + 2]))
                result.Add(Convert.ToHexString(data, offset, 188));
        return result;
    }
}
