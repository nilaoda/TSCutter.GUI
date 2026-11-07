using TSCutter.GUI.Utils.Media;
using Xunit;

namespace TSCutter.GUI.Tests.Media;

/// <summary>帧精确复制区的码流转换：原地改写、直接写入目标缓冲区与原先的数组转换结果一致，且不合法时不改动数据。</summary>
public sealed class VideoBitstreamTests
{
    // H.264：AUD、SPS、IDR 切片，四字节长度前缀。
    private static readonly byte[][] Nals = [[0x09, 0xF0], [0x67, 0x64, 0x00, 0x28], [0x65, 0x88, 0x84, 0x00, 0x21]];

    private static byte[] LengthPrefixed(int lengthSize) =>
        Nals.SelectMany(nal => BitConverter.GetBytes(nal.Length).Take(lengthSize).Reverse().Concat(nal)).ToArray();

    private static byte[] AnnexB(params byte[][] nals) =>
        nals.SelectMany(nal => new byte[] { 0, 0, 0, 1 }.Concat(nal)).ToArray();

    [Fact]
    public void InPlaceConversionMatchesArrayConversion()
    {
        var data = LengthPrefixed(4);
        var expected = VideoBitstream.ToAnnexB(data, 4, false);
        Assert.Equal(AnnexB(Nals), expected);
        VideoBitstream.ConvertToAnnexBInPlace(data);
        Assert.Equal(expected, data);
    }

    [Fact]
    public void InvalidLengthThrowsWithoutModifyingData()
    {
        var data = LengthPrefixed(4);
        data[data.Length - Nals[2].Length - 1] = 0x7F; // 最后一个 NAL 的长度超出包大小
        var copy = data.ToArray();
        Assert.Throws<InvalidDataException>(() => VideoBitstream.ConvertToAnnexBInPlace(data));
        Assert.Equal(copy, data);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void WritesAnnexBWithParameterSetsAfterAud(int lengthSize)
    {
        var data = LengthPrefixed(lengthSize);
        byte[][] prefix = [[0x67, 0x42], [0x68, 0xCE]];
        var size = VideoBitstream.AnnexBSize(data, lengthSize, prefix);
        var destination = new byte[size];
        VideoBitstream.WriteAnnexB(data, lengthSize, false, prefix, destination);
        // 插入的参数集位于访问单元分隔符之后。
        Assert.Equal(AnnexB(Nals[0], prefix[0], prefix[1], Nals[1], Nals[2]), destination);
        Assert.Equal(destination, VideoBitstream.ToAnnexB(data, lengthSize, false, prefix));
        Assert.Throws<ArgumentException>(() => VideoBitstream.WriteAnnexB(data, lengthSize, false, prefix, new byte[size + 1]));
    }

    [Fact]
    public void EnumeratesAnnexBWithoutTrailingZeros()
    {
        byte[] data = [0, 0, 1, 0x09, 0xF0, 0, 0, 0, 1, 0x65, 0x88, 0, 0, 1, 0x41, 0x9A];
        Assert.Equal([new Range(3, 5), new Range(9, 11), new Range(14, 16)], VideoBitstream.SplitNals(data, 0));
        Assert.True(VideoBitstream.ContainsIdr(data, 0, false));
        Assert.False(VideoBitstream.ContainsSps(data, 0, false));
        Assert.Throws<InvalidDataException>(() => VideoBitstream.SplitNals([0, 0, 0, 9, 1], 4));
    }
}
