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

    /// <summary>
    /// CRA 改为 BLA_W_LP：每个 CRA 切片的 NAL 类型改为 16，并清零 no_output_of_prior_pics_flag；其他 NAL 不变。
    /// 清零会形成起始码仿冒（00 00 0x）的非首个切片段保留原标志。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void ConvertsCraToBlaAndClearsNoOutputOfPriorPictures(int lengthSize)
    {
        byte[][] source =
        [
            [0x40, 0x01, 0x0C],                   // VPS
            [0x2A, 0x01, 0xD0, 0x12, 0x34],       // CRA 首个切片，标志为 1
            [0x2A, 0x01, 0x40, 0x00, 0x05, 0x80], // CRA 非首个切片段，清零后为 00 00 05，仍合法
            [0x2A, 0x01, 0x40, 0x00, 0x02, 0x80], // 清零后为 00 00 02：保留标志
            [0x2A, 0x01, 0x90, 0x55],             // 标志本为 0
            [0x02, 0x01, 0xD0, 0x55]              // TRAIL_R：不改
        ];
        byte[][] expected =
        [
            [0x40, 0x01, 0x0C],
            [0x20, 0x01, 0x90, 0x12, 0x34],
            [0x20, 0x01, 0x00, 0x00, 0x05, 0x80],
            [0x20, 0x01, 0x40, 0x00, 0x02, 0x80],
            [0x20, 0x01, 0x90, 0x55],
            [0x02, 0x01, 0xD0, 0x55]
        ];
        byte[] Pack(byte[][] nals) => lengthSize == 0 ? AnnexB(nals)
            : nals.SelectMany(nal => new byte[] { 0, 0, 0, (byte)nal.Length }.Concat(nal)).ToArray();
        var data = Pack(source);
        VideoBitstream.ConvertCraToBla(data, lengthSize);
        Assert.Equal(Pack(expected), data);
        Assert.False(VideoBitstream.ContainsOpenIrap(Pack([source[0], source[5]]), lengthSize));
        Assert.True(VideoBitstream.ContainsOpenIrap(data, lengthSize));
    }
}
