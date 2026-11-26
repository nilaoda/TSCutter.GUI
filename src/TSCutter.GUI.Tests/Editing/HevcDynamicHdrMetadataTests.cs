using System.Buffers.Binary;
using System.IO;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Tests.FFmpeg;
using Xunit;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Tests.Editing;

public sealed class HevcDynamicHdrMetadataTests
{
    [Fact]
    public void ExtractsBothFormatsWithoutCopyingTimingAndUnrelatedMessages()
    {
        byte[] vivid = [0x26, 0, 4, 0, 5, 1, .. new byte[300]];
        byte[] plus = [0xb5, 0, 0x3c, 0, 1, 4, 1, .. new byte[24]];
        var packet = Sei((128, []), (1, [1, 2]), (4, vivid), (5, [9, 9]), (4, plus));
        var metadata = HevcDynamicHdrMetadata.Read(packet, 0)!;
        Assert.Equal(3, metadata.Flags);
        Assert.Equal(Sei((4, vivid), (4, plus)), metadata.Sei);
        var prefixed = new byte[packet.Length + 1];
        BinaryPrimitives.WriteInt32BigEndian(prefixed, packet.Length - 3);
        packet.AsSpan(3).CopyTo(prefixed.AsSpan(4));
        Assert.Equal(metadata.Sei, HevcDynamicHdrMetadata.Read(prefixed, 4)!.Sei);
        Assert.Null(HevcDynamicHdrMetadata.Read(Sei((4, [0x26, 0, 3, 0, 5, 1])), 0));
    }

    [Fact]
    public void LargeUnrelatedSeiDoesNotConsumeTheDynamicMetadataBudget()
    {
        byte[] vivid = [0x26, 0, 4, 0, 5, 1, .. new byte[7]];
        var expected = Sei((4, vivid));
        var unrelated = Sei((5, new byte[70_000]), (4, vivid));
        Assert.Equal(expected, HevcDynamicHdrMetadata.Read(unrelated, 0)!.Sei);
        Assert.Null(HevcDynamicHdrMetadata.Read(Sei((5, new byte[70_000])), 0));
        var oversized = Sei((4, (byte[])[.. vivid, .. new byte[70_000]]));
        Assert.Throws<InvalidDataException>(() => HevcDynamicHdrMetadata.Read(oversized, 0));
    }

    [Fact]
    public void RefusesTruncatedMetadataInsteadOfQuietlyDroppingIt()
    {
        Assert.Throws<InvalidDataException>(() => HevcDynamicHdrMetadata.Read([0, 0, 1, 0x4e, 1, 4, 200, 0x80], 0));
        Assert.Throws<InvalidDataException>(() => HevcDynamicHdrMetadata.Read([0, 0, 1, 0x4e, 1, 4, 0, 1], 0));
        Assert.Throws<InvalidDataException>(() => HevcDynamicHdrMetadata.Read([0, 0, 0, 40, 0x4e, 1], 4));
    }

    [NativeMediaTheory]
    [InlineData(AVFrameSideDataType.AV_FRAME_DATA_DYNAMIC_HDR_PLUS)]
    [InlineData(AVFrameSideDataType.AV_FRAME_DATA_DYNAMIC_HDR_VIVID)]
    public void MissingOriginalMetadataRefusesReencoding(AVFrameSideDataType type)
    {
        var error = Assert.Throws<FrameAccurateExportException>(() => ReadWithoutOriginal(type));
        Assert.Equal(FrameAccurateSupport.DynamicHdr, error.Support);
    }

    private static unsafe void ReadWithoutOriginal(AVFrameSideDataType type)
    {
        AVFrameSideData side = new() { type = type };
        var pointer = &side;
        AVFrame frame = new() { side_data = &pointer, nb_side_data = 1 };
        HevcDynamicHdrMetadata.ReadFrame(&frame, AVCodecID.AV_CODEC_ID_HEVC);
    }

    [NativeRuntimeFact]
    public unsafe void MetadataSurvivesPacketReuseAndFrameCloning()
    {
        var bytes = Sei((4, [0x26, 0, 4, 0, 5, 1, .. new byte[7]]));
        using var source = new Packet();
        AVPacket* raw = source;
        FFmpegException.Check(FF.av_new_packet(raw, bytes.Length));
        bytes.CopyTo(new Span<byte>(raw->data, raw->size));
        using var destination = new Packet();
        var attached = HevcDynamicHdrMetadata.Attach(raw, destination, 0);
        using var frame = Frame.CreateVideo(16, 16, AVPixelFormat.AV_PIX_FMT_YUV420P);
        AVFrame* decoded = frame;
        decoded->opaque_ref = attached->opaque_ref;
        attached->opaque_ref = null;
        using var clone = frame.CloneReference();
        source.Unref();
        destination.Unref();
        frame.Unref();
        Assert.Equal(bytes, HevcDynamicHdrMetadata.ReadFrame(clone, AVCodecID.AV_CODEC_ID_HEVC).ToArray());
    }

    [NativeRuntimeFact]
    public unsafe void InsertsMetadataAfterHeadersAndBeforeThePicture()
    {
        byte[] headers = [0, 0, 1, 0x46, 1, 0x50, 0, 0, 1, 0x40, 1, 0x80];
        byte[] picture = [0, 0, 1, 0x26, 1, 0x80];
        byte[] original = [.. headers, .. picture];
        var sei = Sei((4, [0x26, 0, 4, 0, 5, 1, .. new byte[7]]));
        using var packet = new Packet();
        AVPacket* raw = packet;
        FFmpegException.Check(FF.av_new_packet(raw, original.Length));
        original.CopyTo(new Span<byte>(raw->data, raw->size));
        raw->pts = 123;
        HevcDynamicHdrMetadata.Insert(raw, sei);
        Assert.Equal((byte[])[.. headers, .. sei, .. picture], new ReadOnlySpan<byte>(raw->data, raw->size).ToArray());
        Assert.Equal(123, raw->pts);
    }

    private static byte[] Sei(params (int Type, byte[] Payload)[] messages)
    {
        var rbsp = new List<byte>();
        foreach (var (type, payload) in messages)
        {
            Value(type);
            Value(payload.Length);
            rbsp.AddRange(payload);
        }
        rbsp.Add(0x80);
        var result = new List<byte> { 0, 0, 1, 0x4e, 1 };
        var zeros = 0;
        foreach (var value in rbsp)
        {
            if (zeros == 2 && value <= 3)
            {
                result.Add(3);
                zeros = 0;
            }
            result.Add(value);
            zeros = value == 0 ? zeros + 1 : 0;
        }
        return result.ToArray();

        void Value(int value)
        {
            while (value >= 255)
            {
                rbsp.Add(255);
                value -= 255;
            }
            rbsp.Add((byte)value);
        }
    }
}
