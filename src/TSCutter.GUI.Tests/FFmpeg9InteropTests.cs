using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.Extensions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Utils;
using Xunit;
using Xunit.Abstractions;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Tests;

public sealed class NativeRuntimeFactAttribute : FactAttribute
{
    public NativeRuntimeFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TSCUTTER_FFMPEG_TEST_SAMPLES")))
            Skip = "需要设置 TSCUTTER_FFMPEG_TEST_SAMPLES 和 FFmpeg 9 运行时目录。";
    }
}

public sealed class FFmpeg9InteropTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(188L)]
    [InlineData(5_000_000_000L)]
    [InlineData(long.MaxValue - 1)]
    public void PositionTokenPreservesFileOffsets(long position)
        => Assert.Equal(position, FramePacketPosition.Decode(FramePacketPosition.Encode(position)));

    [Fact]
    public void InvalidPositionsRemainUnknown()
    {
        Assert.Equal(-1, FramePacketPosition.Decode(FramePacketPosition.Encode(long.MaxValue)));
        Assert.Equal(-1, FramePacketPosition.Decode(FramePacketPosition.Encode(-200)));
    }

    [Fact]
    public unsafe void AsyncDecoderPositionsNeverFallBackToOpaque()
    {
        AVFrame frame = default;
        frame.opaque = (void*)FramePacketPosition.Encode(188);
        Assert.Equal(188, FramePacketPosition.Resolve(&frame));
        Assert.Equal(-1, FramePacketPosition.Resolve(&frame, FramePacketPositionSource.DecoderMetadata));
        Assert.Equal(-1, FramePacketPosition.Resolve(&frame, FramePacketPositionSource.Unknown));
    }

    [NativeRuntimeFact]
    public unsafe void MissingPositionExtensionStillDecodesButCannotProvideCutPositions()
    {
        var directory = Environment.GetEnvironmentVariable("TSCUTTER_FFMPEG_TEST_SAMPLES")!;
        foreach (var name in new[] { "AVS2.ts", "AVS3.ts" })
        {
            using var input = FormatContext.OpenInputUrl(Path.Combine(directory, name));
            input.LoadStreamInfo();
            var stream = input.GetVideoStream();
            using var decoder = new CodecContext(Codec.FindDecoderById(stream.Codecpar!.CodecId));
            decoder.FillParameters(stream.Codecpar);
            decoder.PacketTimeBase = stream.TimeBase;
            // 使用不存在的选项名触发真实的 OPTION_NOT_FOUND，模拟普通运行时。
            // 不修改原生结构，私有位置扩展保持默认关闭。
            decoder.Open("tscutter.nonexistent_packet_position_option");

            using var frame = new Frame();
            var decodedCount = 0;
            var packetCount = 0;
            foreach (var packet in input.ReadPackets())
            {
                if (packet.StreamIndex != stream.Index) continue;
                foreach (var decoded in decoder.DecodePacket(packet, frame)) Verify(decoded);
                if (decodedCount > 0 || ++packetCount >= 80) break;
            }
            foreach (var decoded in decoder.DecodePacket(null, frame)) Verify(decoded);
            Assert.True(decodedCount > 0, name);

            void Verify(Frame decoded)
            {
                Assert.True(decoded.Width > 0 && decoded.Height > 0, name);
                Assert.Equal(-1, decoded.PacketPosition);
                // 即使帧包含貌似有效的位置提示，未经支持的来源仍必须保持未知。
                AVFrame* rawFrame = decoded;
                rawFrame->opaque = (void*)FramePacketPosition.Encode(188);
                FFmpegException.Check(FF.av_dict_set(&rawFrame->metadata, "tscutter.packet_pos", "188", 0));
                Assert.Equal(-1, decoded.PacketPosition);
                decodedCount++;
            }
        }
    }

    [NativeRuntimeFact]
    public unsafe void ReorderedFramesRetainPacketPositionAcrossSeekAndDrain()
    {
        var directory = Environment.GetEnvironmentVariable("TSCUTTER_FFMPEG_TEST_SAMPLES")!;
        foreach (var name in new[] { "1080i.ts", "4K.ts", "AVS+.ts", "AVS+_2.ts", "AVS+_P.ts", "AVS2.ts", "AVS3.ts" })
        {
            using var input = FormatContext.OpenInputUrl(Path.Combine(directory, name));
            input.LoadStreamInfo();
            var stream = input.GetVideoStream();
            using var decoder = new CodecContext(Codec.FindDecoderById(stream.Codecpar!.CodecId));
            decoder.FillParameters(stream.Codecpar);
            decoder.PacketTimeBase = stream.TimeBase;
            var isCavs = stream.Codecpar.CodecId == AVCodecID.AV_CODEC_ID_CAVS;
            if (isCavs)
            {
                AVCodecContext* rawDecoder = decoder;
                FFmpegException.Check(FF.av_opt_set_int(rawDecoder->priv_data, "export_packet_pos", 1, 0));
            }
            decoder.Open();
            using var frame = new Frame();
            var positions = new Dictionary<long, long>();
            var knownPositions = new HashSet<long>();
            var keyPositions = new HashSet<long>();
            var count = 0;
            var delayed = 0;
            for (var pass = 0; pass < 2; pass++)
            {
                positions.Clear();
                knownPositions.Clear();
                keyPositions.Clear();
                var packets = 0;
                foreach (var packet in input.ReadPackets())
                {
                    if (packet.StreamIndex != stream.Index) continue;
                    positions[packet.Pts] = packet.Position;
                    knownPositions.Add(packet.Position);
                    if ((packet.Flags & 1) != 0) keyPositions.Add(packet.Position);
                    foreach (var decoded in decoder.DecodePacket(packet, frame)) Validate(decoded);
                    if (++packets >= 80) break;
                }
                // 模拟画面搜索在 EOF 取到第一帧后返回；下次必须仍能取到余下帧。
                foreach (var decoded in decoder.DecodePacket(null, frame, preserveRemainingOutput: true))
                {
                    Validate(decoded);
                    delayed++;
                    break;
                }
                foreach (var decoded in decoder.ReceiveFrames(frame)) { Validate(decoded); delayed++; }
                input.SeekFrame(stream.StartTime, stream.Index);
                decoder.FlushBuffers();
            }
            Assert.True(count > 0, name);
            Assert.True(delayed > 0, name);
            output.WriteLine($"{name}: {count} frames, {delayed} drained frames, position/PTS matched.");

            void Validate(Frame decoded)
            {
                Assert.True(decoded.PacketPosition >= 0, $"{name}: PTS {decoded.Pts}, missing position");
                Assert.Contains(decoded.PacketPosition, knownPositions);
                if ((decoded.Flags & FF.AV_FRAME_FLAG_KEY) != 0)
                    Assert.Contains(decoded.PacketPosition, keyPositions);
                if (isCavs)
                {
                    // CAVS 对重复或倒退 PTS 做单调化修正；位置令牌必须与解码器来源元数据一致。
                    AVFrame* rawFrame = decoded;
                    Assert.Equal(decoded.PacketPosition, FramePacketPosition.Decode((nint)rawFrame->opaque));
                }
                else
                {
                    Assert.True(positions.TryGetValue(decoded.Pts, out var expected), $"{name}: unknown PTS {decoded.Pts}");
                    Assert.Equal(expected, decoded.PacketPosition);
                }
                count++;
            }
        }
    }

    [NativeRuntimeFact]
    public unsafe void FrameMetadataOverridesOpaqueAndRejectsInvalidPositions()
    {
        using var frame = new Frame();
        AVFrame* raw = frame;
        Assert.Equal(-1, frame.PacketPosition);
        raw->opaque = (void*)FramePacketPosition.Encode(188);
        Assert.Equal(188, frame.PacketPosition);
        foreach (var (value, expected) in new (string, long)[]
                 { ("0", 0), ("5000000000", 5_000_000_000), ("-1", -1), ("", -1),
                   ("9223372036854775808", -1), ("188x", -1) })
        {
            FFmpegException.Check(FF.av_dict_set(&raw->metadata, "tscutter.packet_pos", value, 0));
            Assert.Equal(expected, frame.PacketPosition);
        }
        frame.Unref();
        Assert.Equal(-1, frame.PacketPosition);
    }

    [NativeRuntimeFact]
    public void InterruptCallbackSurvivesCollectionAndCancelsNativeInput()
    {
        var path = Path.Combine(Environment.GetEnvironmentVariable("TSCUTTER_FFMPEG_TEST_SAMPLES")!, "1080i.ts");
        var callbackCount = 0;
        using (var input = InterruptibleInputFormatContext.Open(path, () => { callbackCount++; return false; }))
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            input.LoadStreamInfo();
            Assert.True(callbackCount > 0);
        }
        var interrupted = false;
        Assert.Throws<FFmpegException>(() =>
        {
            using var input = InterruptibleInputFormatContext.Open(path, () => { interrupted = true; return true; });
            input.LoadStreamInfo();
        });
        Assert.True(interrupted);
    }

    [NativeRuntimeFact]
    public unsafe void ConversionReusesContextWithoutPerFrameManagedArrays()
    {
        using var source = Frame.CreateVideo(64, 48, AVPixelFormat.AV_PIX_FMT_YUV420P);
        using var destination = Frame.CreateVideo(32, 24, AVPixelFormat.AV_PIX_FMT_BGRA);
        using var converter = new VideoFrameConverter();
        converter.ConvertFrame(source, destination);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) converter.ConvertFrame(source, destination);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}
