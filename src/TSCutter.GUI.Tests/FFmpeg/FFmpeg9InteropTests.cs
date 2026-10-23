using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.Extensions;
using TSCutter.GUI.FFmpeg;
using Xunit;
using Xunit.Abstractions;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;
using TSCutter.GUI.Models.TransportStream.Analysis;
using TSCutter.GUI.Services.TransportStream.Analysis;
using TSCutter.GUI.Utils.Media;

namespace TSCutter.GUI.Tests.FFmpeg;

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
    public unsafe void UnsupportedHdrLayoutsKeepTheRegularPreviewConversion()
    {
        foreach (var format in new[] { AVPixelFormat.AV_PIX_FMT_GBRP10LE, AVPixelFormat.AV_PIX_FMT_YUVA420P10LE })
        {
            using var source = Frame.CreateVideo(16, 8, format);
            AVFrame* raw = source;
            raw->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084;
            raw->color_primaries = AVColorPrimaries.AVCOL_PRI_BT2020;
            raw->colorspace = format == AVPixelFormat.AV_PIX_FMT_GBRP10LE
                ? AVColorSpace.AVCOL_SPC_RGB : AVColorSpace.AVCOL_SPC_BT2020_NCL;
            var descriptor = FF.av_pix_fmt_desc_get(format);
            for (uint c = 0; c < descriptor->nb_components; c++)
            {
                var info = descriptor->comp[c];
                var chroma = format != AVPixelFormat.AV_PIX_FMT_GBRP10LE && c is 1 or 2;
                var width = source.Width >> (chroma ? descriptor->log2_chroma_w : 0);
                var height = source.Height >> (chroma ? descriptor->log2_chroma_h : 0);
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    *(ushort*)(raw->data[(uint)info.plane] + y * raw->linesize[(uint)info.plane]
                        + x * info.step + info.offset) = (ushort)(128 + c * 256);
            }
            using var expected = Frame.CreateVideo(16, 8, AVPixelFormat.AV_PIX_FMT_BGRA);
            using var actual = Frame.CreateVideo(16, 8, AVPixelFormat.AV_PIX_FMT_BGRA);
            using var original = new VideoFrameConverter { EnableSdrToneMapping = false };
            using var enabled = new VideoFrameConverter();
            original.ConvertFrame(source, expected);
            enabled.ConvertFrame(source, actual);
            for (var y = 0; y < source.Height; y++)
                Assert.True(new ReadOnlySpan<byte>(expected.Data[0] + y * expected.Linesize[0], 16 * 4)
                    .SequenceEqual(new ReadOnlySpan<byte>(actual.Data[0] + y * actual.Linesize[0], 16 * 4)), format.ToString());
        }
    }

    [NativeRuntimeFact]
    public unsafe void HdrConversionRecoversFromInvalidBuffersAndResolutionChanges()
    {
        using var reused = new VideoFrameConverter();
        foreach (var (width, height, format) in new[]
        {
            (641, 401, AVPixelFormat.AV_PIX_FMT_YUV420P10LE),
            (33, 19, AVPixelFormat.AV_PIX_FMT_P010LE),
            (641, 401, AVPixelFormat.AV_PIX_FMT_YUV444P10LE),
            (16, 8, AVPixelFormat.AV_PIX_FMT_YUV420P)
        })
        {
            using var source = Frame.CreateVideo(width, height, format);
            AVFrame* raw = source;
            raw->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084;
            raw->color_primaries = AVColorPrimaries.AVCOL_PRI_BT2020;
            raw->colorspace = AVColorSpace.AVCOL_SPC_BT2020_NCL;
            var descriptor = FF.av_pix_fmt_desc_get(format);
            for (uint component = 0; component < 3; component++)
            {
                var info = descriptor->comp[component];
                var rows = component == 0 ? height : (height + (1 << descriptor->log2_chroma_h) - 1) >> descriptor->log2_chroma_h;
                new Span<byte>(raw->data[(uint)info.plane], raw->linesize[(uint)info.plane] * rows).Fill(128);
            }
            using var expected = Frame.CreateVideo(width, height, AVPixelFormat.AV_PIX_FMT_BGRA);
            using var actual = Frame.CreateVideo(width, height, AVPixelFormat.AV_PIX_FMT_BGRA);
            var stride = source.Linesize[0];
            source.Linesize[0] = 0;
            try { Assert.Throws<InvalidOperationException>(() => reused.ConvertFrame(source, actual)); }
            finally { source.Linesize[0] = stride; }
            using var fresh = new VideoFrameConverter();
            fresh.ConvertFrame(source, expected);
            reused.ConvertFrame(source, actual);
            for (var row = 0; row < height; row++)
                Assert.True(new ReadOnlySpan<byte>(expected.Data[0] + row * expected.Linesize[0], width * 4)
                    .SequenceEqual(new ReadOnlySpan<byte>(actual.Data[0] + row * actual.Linesize[0], width * 4)));
        }
    }

    [NativeRuntimeFact("TSCUTTER_FFMPEG_TEST_SAMPLES")]
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

    [NativeRuntimeFact("TSCUTTER_FFMPEG_TEST_SAMPLES")]
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

    [NativeMediaFact("keyframes")]
    public void InterruptCallbackSurvivesCollectionAndCancelsNativeInput()
    {
        var path = Path.Combine(NativeTestEnvironment.FixtureDirectory("keyframes"), "fixed.ts");
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

    [NativeRuntimeFact]
    public unsafe void Av3aSignalingRejectsGuessedMpegAudioParameters()
    {
        foreach (var tag in new uint[] { 0x61337661, 0xD5 })
        {
            AVCodecParameters raw = new()
            {
                codec_id = AVCodecID.AV_CODEC_ID_MP3,
                codec_type = AVMediaType.AVMEDIA_TYPE_AUDIO,
                codec_tag = tag,
                sample_rate = 22_050,
                bit_rate = 64_000,
                ch_layout = new AVChannelLayout { nb_channels = 2 }
            };
            var parameters = new CodecParameters(&raw);
            Assert.Equal("AV3A", parameters.CodecName);
            Assert.Equal(AVMediaType.AVMEDIA_TYPE_AUDIO, parameters.CodecType);
            Assert.Equal(AVCodecID.AV_CODEC_ID_NONE, parameters.CodecId);
            Assert.False(parameters.HasReliableCodecParameters);
            Assert.Equal(0, parameters.SampleRate);
            Assert.Equal(0, parameters.BitRate);
            Assert.Equal(0, parameters.ChLayout.nb_channels);
        }
    }

    [NativeRuntimeFact]
    public unsafe void UnsupportedAv3aStillAppearsAsAnAudioTrack()
    {
        AVCodecParameters raw = new()
        {
            codec_id = AVCodecID.AV_CODEC_ID_NONE,
            codec_type = AVMediaType.AVMEDIA_TYPE_UNKNOWN,
            codec_tag = 0x61337661
        };
        var parameters = new CodecParameters(&raw);
        Assert.Equal("AV3A", parameters.CodecName);
        Assert.True(ThumbnailSheetInfoBuilder.IsDisplayableStreamType(parameters.CodecType));
        Assert.Equal(AVMediaType.AVMEDIA_TYPE_AUDIO, parameters.CodecType);
        Assert.False(parameters.HasReliableCodecParameters);
    }

    [NativeRuntimeFact]
    public unsafe void NativeAv3aParametersDoNotRequireProgramSignaling()
    {
        // 官方枚举不包含自定义 AV3A；从原生描述符找到实际编码值。
        AVCodecID av3a = AVCodecID.AV_CODEC_ID_NONE;
        for (var offset = 1; offset <= 4; offset++)
        {
            var id = (AVCodecID)((int)AVCodecID.AV_CODEC_ID_APPLE_APAC + offset);
            var descriptor = FF.avcodec_descriptor_get(id);
            if (descriptor != null && System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)descriptor->name) == "av3a")
                av3a = id;
        }
        Assert.NotEqual(AVCodecID.AV_CODEC_ID_NONE, av3a);
        AVCodecParameters raw = new()
        {
            codec_id = av3a,
            codec_type = AVMediaType.AVMEDIA_TYPE_AUDIO,
            codec_tag = 0,
            sample_rate = 48_000,
            bit_rate = 384_000,
            ch_layout = new AVChannelLayout { order = AVChannelOrder.AV_CHANNEL_ORDER_UNSPEC, nb_channels = 10 }
        };
        var parameters = new CodecParameters(&raw);
        Assert.True(parameters.IsAv3a);
        Assert.True(parameters.HasReliableCodecParameters);
        Assert.Equal("AV3A", parameters.CodecName);
        Assert.Equal(48_000, parameters.SampleRate);
        Assert.Equal(384_000, parameters.BitRate);
        Assert.Equal(10, parameters.ChLayout.nb_channels);
    }

    [NativeMediaFact("keyframes")]
    public async Task LateProgramTablesExpandOnlyTheTsProbe()
    {
        var source = Path.Combine(NativeTestEnvironment.FixtureDirectory("keyframes"), "fixed.ts");
        using (var ordinary = FormatContext.OpenInputUrl(source))
        {
            long originalProbeSize;
            unsafe
            {
                AVFormatContext* raw = ordinary;
                Assert.True(raw->nb_programs > 0);
                originalProbeSize = raw->probesize;
            }
            ordinary.LoadStreamInfo();
            unsafe { Assert.Equal(originalProbeSize, ((AVFormatContext*)ordinary)->probesize); }
        }

        var temporary = Path.Combine(Path.GetTempPath(), $"ts-late-psi-{Guid.NewGuid():N}.ts");
        try
        {
            // 模拟剪辑片段直到超过默认 5 MB 探测范围后才遇到原有节目表。
            // 缓冲区固定大小，不把真实素材完整加载到内存。
            var buffer = new byte[188 * 8192];
            Array.Fill(buffer, (byte)0xFF);
            for (var offset = 0; offset < buffer.Length; offset += 188)
            {
                buffer[offset] = 0x47;
                buffer[offset + 1] = 0x1F;
                buffer[offset + 2] = 0xFF;
                buffer[offset + 3] = 0x10;
            }
            await using (var outputFile = File.Create(temporary))
            {
                for (var index = 0; index < 4; index++) await outputFile.WriteAsync(buffer);
                await using var inputFile = File.OpenRead(source);
                var read = await inputFile.ReadAsync(buffer);
                await outputFile.WriteAsync(buffer.AsMemory(0, read));
            }

            // 节目相关工具使用独立逐包扫描，不依赖 FFmpeg 的 5 MB 探测上限。
            foreach (var options in new[]
            {
                new Models.TransportStream.Analysis.TsStreamAnalyzeOptions { InventoryOnly = true, MinimumBytes = Models.TransportStream.Analysis.TsStreamAnalyzeOptions.StandardProbeBytes, MaxBytes = 64L * 1024 * 1024 },
                new Models.TransportStream.Analysis.TsStreamAnalyzeOptions { InventoryOnly = true, IncludeServiceMetadata = true, MaxBytes = 64L * 1024 * 1024 },
                new Models.TransportStream.Analysis.TsStreamAnalyzeOptions { InventoryOnly = true, MaxBytes = 256L * 1024 * 1024 }
            })
            {
                var catalog = await new Services.TransportStream.Analysis.TsStreamAnalyzer().AnalyzeAsync(temporary, options: options);
                Assert.NotEmpty(catalog.Programs);
                Assert.All(catalog.Programs.Values, program => Assert.NotEmpty(program.Streams));
            }

            using var delayed = FormatContext.OpenInputUrl(temporary);
            unsafe { Assert.Equal(0u, ((AVFormatContext*)delayed)->nb_programs); }
            delayed.LoadStreamInfo();
            Assert.NotEmpty(delayed.Programs);
            Assert.Equal(AVCodecID.AV_CODEC_ID_H264, delayed.GetVideoStream().Codecpar!.CodecId);
            unsafe
            {
                Assert.Equal(Models.TransportStream.Analysis.TsStreamAnalyzeOptions.StandardProbeBytes,
                    ((AVFormatContext*)delayed)->probesize);
            }
        }
        finally { File.Delete(temporary); }
    }
}
