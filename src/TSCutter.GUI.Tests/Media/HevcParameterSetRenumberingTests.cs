using System.Security.Cryptography;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Utils.Media;
using Xunit;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Tests.Media;

/// <summary>
/// HEVC 参数集改号：用 x265 生成的整段码流（B 帧参考画面集、加权预测、波前并行入口点、多切片、4:2:2 范围扩展、
/// CRA/RASL 与 RADL）作为“编码器输出”，改号后严格模式解码无错误，每帧画面与改号前逐字节相同；
/// 新编号避开已用的编号，带内参数集与构造时不同则报错。
/// </summary>
public sealed class HevcParameterSetRenumberingTests
{
    [NativeMediaTheory("frameaccurate")]
    [InlineData("hevc-hlg.ts")]
    [InlineData("hevc-cra.ts")]
    [InlineData("hevc-cra-slices.ts")]
    [InlineData("hevc-422.ts")]
    public void RenumberedStreamDecodesToTheSamePictures(string name)
    {
        var packets = VideoPackets(Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), name));
        var sets = new List<byte[]>();
        foreach (var packet in packets)
            foreach (var range in VideoBitstream.SplitNals(packet, 0))
                VideoBitstream.AddParameterSet(packet[range], true, sets);
        Assert.Equal(3, sets.Count);

        var renumbering = new HevcParameterSetRenumbering(sets, sets);
        Assert.Equal((1, 1, 1), (renumbering.VpsId, renumbering.SpsId, renumbering.PpsId));
        // 已用 0 与 1 时取 2。
        var next = new HevcParameterSetRenumbering(sets, [.. sets, .. renumbering.ParameterSets]);
        Assert.Equal((2, 2, 2), (next.VpsId, next.SpsId, next.PpsId));

        var rewritten = packets.Select(packet => renumbering.Rewrite(packet)).ToList();
        Assert.All(rewritten, packet => Assert.False(VideoBitstream.Contains(packet, 0, true,
            type => VideoBitstream.ParameterSetOrder(type, true) >= 0)));
        // 改号后的参数集只在开头给一次（样本描述中的参数集对解码器的作用相同）。
        rewritten[0] = [.. VideoBitstream.ToAnnexB([], 0, true, renumbering.ParameterSets), .. rewritten[0]];
        var expected = Decode(packets);
        Assert.True(expected.Count >= packets.Count - 2);
        Assert.Equal(expected, Decode(rewritten));
    }

    [NativeMediaTheory("frameaccurate")]
    [InlineData(32)]
    [InlineData(33)]
    public void ChangedInBandParameterSetsAreRejected(int type)
    {
        var packets = VideoPackets(Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), "hevc-cra.ts"));
        var sets = new List<byte[]>();
        foreach (var range in VideoBitstream.SplitNals(packets[0], 0))
            VideoBitstream.AddParameterSet(packets[0][range], true, sets);
        // 码流中的 VPS 或 SPS 与构造时给出的不同（改动 profile_tier_level 中的一个字节，不影响解析）。
        var target = sets.Single(set => VideoBitstream.NalType(set, true) == type);
        var changed = sets.Select(set => set == target ? [.. set[..5], (byte)(set[5] ^ 1), .. set[6..]] : set).ToList();
        var renumbering = new HevcParameterSetRenumbering(changed, sets);
        Assert.Throws<InvalidDataException>(() => renumbering.Rewrite(packets[0]));
    }

    private static unsafe List<byte[]> VideoPackets(string path)
    {
        using var input = FormatContext.OpenInputUrl(path);
        input.LoadStreamInfo();
        var index = input.GetVideoStream().Index;
        var result = new List<byte[]>();
        foreach (var packet in input.ReadPackets())
        {
            AVPacket* raw = packet;
            if (raw->stream_index == index)
                result.Add(new ReadOnlySpan<byte>(raw->data, raw->size).ToArray());
        }
        return result;
    }

    /// <summary>不带附加数据、严格模式解码 Annex B 包，返回每帧各平面内容的哈希；有错误时失败。</summary>
    private static unsafe List<string> Decode(List<byte[]> packets)
    {
        using var decoder = new CodecContext(Codec.FindDecoderById(AVCodecID.AV_CODEC_ID_HEVC));
        AVCodecContext* context = decoder;
        context->err_recognition = 1 << 3; // AV_EF_EXPLODE
        FFmpegException.Check(FF.avcodec_open2(decoder, FF.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_HEVC), null));
        var hashes = new List<string>();
        using var frame = new Frame();
        using var packet = new Packet();
        void Receive()
        {
            while (FF.avcodec_receive_frame(decoder, frame) >= 0)
            {
                AVFrame* raw = frame;
                Assert.Equal(0, raw->decode_error_flags);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var bytes = raw->format is (int)AVPixelFormat.AV_PIX_FMT_YUV420P or (int)AVPixelFormat.AV_PIX_FMT_YUV422P ? 1 : 2;
                var chromaHeight = raw->format is (int)AVPixelFormat.AV_PIX_FMT_YUV422P or
                    (int)AVPixelFormat.AV_PIX_FMT_YUV422P10LE ? raw->height : (raw->height + 1) / 2;
                for (uint plane = 0; plane < 3; plane++)
                {
                    var width = (plane == 0 ? raw->width : (raw->width + 1) / 2) * bytes;
                    var height = plane == 0 ? raw->height : chromaHeight;
                    for (var row = 0; row < height; row++)
                        hash.AppendData(new ReadOnlySpan<byte>(raw->data[plane] + row * raw->linesize[plane], width));
                }
                hashes.Add(Convert.ToHexString(hash.GetHashAndReset()));
                frame.Unref();
            }
        }
        foreach (var data in packets)
        {
            AVPacket* raw = packet;
            FFmpegException.Check(FF.av_new_packet(raw, data.Length));
            data.CopyTo(new Span<byte>(raw->data, data.Length));
            Assert.True(FF.avcodec_send_packet(decoder, packet) >= 0, "decode error");
            packet.Unref();
            Receive();
        }
        FF.avcodec_send_packet(decoder, null);
        Receive();
        return hashes;
    }
}
