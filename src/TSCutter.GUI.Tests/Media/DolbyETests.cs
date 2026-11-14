using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Utils.Media;
using TSCutter.GUI.Utils.Media.DolbyE;
using Xunit;

namespace TSCutter.GUI.Tests.Media;

public sealed class DolbyETests
{
    // 可选真实素材：目录中放带杜比 E 的 TS，参见 README。
    private const string SampleVariable = "TSCUTTER_DOLBY_E_SAMPLES";

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    public void DeframerRecoversBurstPayloadForEveryWordSize(int bits)
    {
        var words = Enumerable.Range(1, 9).Select(value => (uint)(value * 0x111) & ((1u << bits) - 1)).ToArray();
        var samples = S337mDeframer.Compose(bits, S337mDeframer.DolbyEDataType, words);
        var deframer = new S337mDeframer();
        S337mBurst? burst = null;
        // 前面放几对静音，突发起点的时间戳应是 Pa 所在的样本对。
        for (var i = 0; i < 3; i++)
            Assert.Null(deframer.Push(0, 0, i));
        for (var i = 0; i < samples.Length; i += 2)
            burst ??= deframer.Push(samples[i], samples[i + 1], 3 + i / 2);
        Assert.NotNull(burst);
        Assert.Equal(bits, burst.WordBits);
        Assert.Equal(S337mDeframer.DolbyEDataType, burst.DataType);
        Assert.Equal(3, burst.Timestamp);
        var bytes = (bits + 7) / 8;
        Assert.Equal(words.Length * bytes, burst.Payload.Length);
        for (var i = 0; i < words.Length; i++)
        {
            uint value = 0;
            for (var b = 0; b < bytes; b++)
                value = (value << 8) | burst.Payload[i * bytes + b];
            Assert.Equal(bits == 20 ? words[i] << 4 : words[i], value);
        }
    }

    [Fact]
    public void BurstSplitByLostDataRestartsAtNextSync()
    {
        var first = S337mDeframer.Compose(20, 28, [1, 2, 3, 4, 5, 6]);
        var second = S337mDeframer.Compose(20, 28, [7, 8]);
        var deframer = new S337mDeframer();
        // 第一个突发只收到一部分负载，接着就是第二个突发。
        for (var i = 0; i < 6; i += 2)
            Assert.Null(deframer.Push(first[i], first[i + 1], 0));
        S337mBurst? burst = null;
        for (var i = 0; i < second.Length; i += 2)
            burst ??= deframer.Push(second[i], second[i + 1], 100 + i);
        Assert.NotNull(burst);
        Assert.Equal(100, burst.Timestamp);
        Assert.Equal(2 * 3, burst.Payload.Length);
    }

    [Theory]
    [InlineData(16, false)]
    [InlineData(20, false)]
    [InlineData(24, false)]
    [InlineData(20, true)]
    public void HeaderParserReadsProgramConfigurationAndFrameRate(int bits, bool scrambled)
    {
        var payload = BuildHeader(bits, programConfig: 0, frameRate: 3, key: scrambled ? 0x5A5u & ((1u << bits) - 1) : null);
        Assert.True(DolbyEFrameHeader.TryParse(payload, out var header));
        Assert.Equal(bits, header.WordBits);
        Assert.Equal(scrambled, header.KeyPresent);
        Assert.Equal(0, header.ProgramConfig);
        Assert.Equal(3, header.FrameRateCode);
        Assert.Equal(44_800, header.SampleRate);
        Assert.Equal(8, header.ChannelCount);
        Assert.False(DolbyEFrameHeader.TryParse(payload.AsSpan(0, 2), out _));
    }

    [Fact]
    public void ConfigurationTableAssignsChannelsInOrder()
    {
        for (var config = 0; config <= DolbyEConfiguration.MaxProgramConfig; config++)
        {
            var programs = DolbyEConfiguration.GetPrograms(config);
            Assert.NotEmpty(programs);
            Assert.Equal(1, programs[0].FirstChannel);
            for (var i = 1; i < programs.Count; i++)
                Assert.Equal(programs[i - 1].LastChannel + 1, programs[i].FirstChannel);
            Assert.Equal(DolbyEConfiguration.ChannelCount(config), programs[^1].LastChannel);
            foreach (var program in programs)
                Assert.Equal(program.ChannelCount, (int)ulong.PopCount(program.Mask));
        }
        Assert.Equal("5.1 + 2.0", DolbyEConfiguration.Name(0));
        Assert.Equal("5.1", DolbyEConfiguration.Name(11));
        Assert.Equal(DolbyEProgramKind.Surround71Screen, DolbyEConfiguration.GetPrograms(23)[0].Kind);
        // 只有一个声道组的 8 声道配置按 FFmpeg 7.1 顺序输出，其余配置按声道号。
        Assert.Equal([0, 1, 2, 3, 6, 7, 4, 5], Enumerable.Range(1, 8).Select(channel => DolbyEConfiguration.DecoderIndex(22, channel)));
        Assert.Equal(Enumerable.Range(0, 8), Enumerable.Range(1, 8).Select(channel => DolbyEConfiguration.DecoderIndex(0, channel)));
    }

    [FFmpeg.NativeRuntimeFact(SampleVariable)]
    public void RealSamplesAreDetectedAndDecodedPerProgram()
    {
        foreach (var file in RealSamples())
        {
            using var input = InterruptibleInputFormatContext.Open(file, static () => false);
            input.LoadStreamInfo();
            var tracks = DolbyEProbe.ProbeCore(input, CancellationToken.None);
            var track = Assert.Single(tracks.Values);
            using var reopened = InterruptibleInputFormatContext.Open(file, static () => false);
            reopened.LoadStreamInfo();
            var stream = reopened.Streams.Single(item => item.Id == track.Pid);
            using var decoder = DolbyEDecoder.TryCreate(stream);
            Assert.NotNull(decoder);
            var frames = 0;
            var extracted = 0;
            using var program = new global::TSCutter.GUI.FFmpeg.Frame();
            long previous = long.MinValue;
            foreach (var packet in reopened.ReadPackets())
            {
                if (packet.StreamIndex != stream.Index)
                    continue;
                decoder.Decode(packet, (frame, header) =>
                {
                    frames++;
                    Assert.Equal(track.Header.ProgramConfig, header.ProgramConfig);
                    unsafe
                    {
                        AVFrame* raw = frame;
                        Assert.Equal(1792, raw->nb_samples);
                        Assert.Equal(44_800, raw->sample_rate);
                        Assert.Equal(header.ChannelCount, raw->ch_layout.nb_channels);
                        Assert.True(raw->pts > previous);
                        previous = raw->pts;
                    }
                    foreach (var item in track.Programs)
                        if (DolbyEDecoder.TryExtractProgram(frame, header, item.Number, program))
                            extracted++;
                });
                if (frames >= 50)
                    break;
            }
            Assert.True(frames >= 50, $"{Path.GetFileName(file)} decoded {frames} frames");
            Assert.Equal(frames * track.Programs.Count, extracted);
        }
    }

    /// <summary>素材目录中能识别出杜比 E 的 TS（目录里可能还有导出结果等其他文件），至少一个。</summary>
    internal static IReadOnlyList<string> RealSamples()
    {
        var samples = Directory.GetFiles(Environment.GetEnvironmentVariable(SampleVariable)!, "*.ts").Where(file =>
        {
            using var input = InterruptibleInputFormatContext.Open(file, static () => false);
            input.LoadStreamInfo();
            return DolbyEProbe.ProbeCore(input, CancellationToken.None).Count > 0;
        }).ToArray();
        Assert.NotEmpty(samples);
        return samples;
    }

    /// <summary>构造只含同步字、可选密钥和元数据开头的杜比 E 帧头。</summary>
    internal static byte[] BuildHeader(int bits, int programConfig, int frameRate, uint? key)
    {
        var sync = bits switch { 16 => 0x078E, 20 => 0x0788E, _ => 0x07888E } | (key is null ? 0 : 1);
        var words = new List<uint> { (uint)sync };
        if (key is { } value)
            words.Add(value);
        // 元数据：4 位跳过、10 位元数据长度、prog_conf 6 位、两个帧率代码各 4 位，后面补零。
        var bitsList = new List<int>();
        void Put(uint field, int count)
        {
            for (var i = count - 1; i >= 0; i--)
                bitsList.Add((int)((field >> i) & 1));
        }
        Put(0, 4);
        Put(8, 10);
        Put((uint)programConfig, 6);
        Put((uint)frameRate, 4);
        Put((uint)frameRate, 4);
        while (bitsList.Count % bits != 0 || bitsList.Count < bits * 8)
            bitsList.Add(0);
        for (var i = 0; i < bitsList.Count; i += bits)
        {
            uint word = 0;
            for (var b = 0; b < bits; b++)
                word = (word << 1) | (uint)bitsList[i + b];
            words.Add(word ^ (key ?? 0));
        }
        var bytes = (bits + 7) / 8;
        var result = new byte[words.Count * bytes];
        for (var i = 0; i < words.Count; i++)
        {
            var word = bits == 20 ? words[i] << 4 : words[i];
            for (var b = 0; b < bytes; b++)
                result[i * bytes + b] = (byte)(word >> (8 * (bytes - 1 - b)));
        }
        return result;
    }
}
