using TSCutter.GUI.Rendering.Colors;
using Xunit;
using FFmpeg.AutoGen.Abstractions;

namespace TSCutter.GUI.Tests.Preview;

public sealed class HdrToneMapperTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(0.1f)]
    [InlineData(100)]
    [InlineData(203)]
    [InlineData(1000)]
    [InlineData(10000)]
    public void PqRoundTripPreservesAbsoluteLuminance(float nits)
    {
        var actual = HdrToneMapper.PqToNits(HdrToneMapper.NitsToPq(nits));
        Assert.InRange(Math.Abs(actual - nits), 0, Math.Max(0.001f, nits * 0.0001f));
    }

    [Fact]
    public void PolynomialSelectsUpperPieceAtPivotAndClampsOutput()
    {
        var curve = new ReshapeCurve(3);
        curve.Pivots[1] = 0.5f; curve.Pivots[2] = 1;
        curve.Pieces[0] = Polynomial(0, 1, 0);
        curve.Pieces[1] = Polynomial(0.25f, 1, 0);
        Assert.Equal(0.49f, HdrToneMapper.Reshape(curve, 0.49f, 0, 0, 0.49f));
        Assert.Equal(0.75f, HdrToneMapper.Reshape(curve, 0.5f, 0, 0, 0.5f));
        Assert.Equal(1, HdrToneMapper.Reshape(curve, 1, 0, 0, 1));
    }

    [Fact]
    public void MmrIncludesCrossTermsAndAllThreeOrders()
    {
        var curve = new ReshapeCurve(2); curve.Pivots[1] = 1;
        var piece = new ReshapePiece(1, 3) { Constant = 0.1f };
        piece.Coefficients[0] = 0.5f;
        piece.Coefficients[3] = 0.25f;
        piece.Coefficients[7 + 6] = 0.75f;
        piece.Coefficients[14 + 2] = 0.125f;
        curve.Pieces[0] = piece;
        var expected = 0.1f + 0.5f * 0.2f + 0.25f * (0.2f * 0.3f)
            + 0.75f * MathF.Pow(0.2f * 0.3f * 0.4f, 2) + 0.125f * MathF.Pow(0.4f, 3);
        Assert.Equal(expected, HdrToneMapper.Reshape(curve, 0.2f, 0.3f, 0.4f, 0.2f), 6);
    }

    [Fact]
    public void SimdMatchesScalarWithMixedCurvesAndOddRowWidth()
    {
        var metadata = IdentityMetadata();
        var mmr = new ReshapePiece(1, 3) { Constant = 0.01f };
        mmr.Coefficients[0] = 0.95f;
        mmr.Coefficients[3] = 0.03f;
        mmr.Coefficients[7 + 1] = 0.01f;
        mmr.Coefficients[14 + 6] = -0.01f;
        metadata.Curves![0].Pieces[0] = mmr;
        var piecewise = new ReshapeCurve(3);
        piecewise.Pivots[1] = 0.5f; piecewise.Pivots[2] = 1;
        piecewise.Pieces[0] = Polynomial(0, 1, 0);
        piecewise.Pieces[1] = Polynomial(0.02f, 0.96f, 0.02f);
        metadata.Curves![1] = piecewise;
        var random = new Random(12);
        const int count = 37;
        var y = Enumerable.Range(0, count).Select(_ => random.NextSingle()).ToArray();
        var u = Enumerable.Range(0, count).Select(_ => random.NextSingle()).ToArray();
        var v = Enumerable.Range(0, count).Select(_ => random.NextSingle()).ToArray();
        var scalar = new[] { new float[count], new float[count], new float[count] };
        var simd = new[] { new float[count], new float[count], new float[count] };
        var mapper = new HdrToneMapper();
        mapper.MapSignals(metadata, y, u, v, scalar[0], scalar[1], scalar[2], false);
        mapper.MapSignals(metadata, y, u, v, simd[0], simd[1], simd[2], true);
        for (var c = 0; c < 3; c++)
        for (var i = 0; i < count; i++)
        {
            Assert.InRange(simd[c][i], -0.00001f, 1.00001f);
            Assert.InRange(Math.Abs(scalar[c][i] - simd[c][i]), 0, 0.00001f);
        }
    }

    [Fact]
    public void NeutralRampRemainsNeutralMonotonicAndWithinSdrRange()
    {
        var metadata = IdentityMetadata();
        var signal = new[] { 0, 0.1f, 0.25f, 0.5f, 0.75f, 1 };
        var r = new float[signal.Length]; var g = new float[signal.Length]; var b = new float[signal.Length];
        new HdrToneMapper().MapSignals(metadata, signal, signal, signal, r, g, b, true);
        for (var i = 0; i < signal.Length; i++)
        {
            Assert.InRange(r[i], 0, 1);
            Assert.InRange(Math.Abs(r[i] - g[i]), 0, 0.0001f);
            Assert.InRange(Math.Abs(r[i] - b[i]), 0, 0.0001f);
            if (i > 0) Assert.True(r[i] >= r[i - 1]);
        }
        Assert.Equal(0, r[0]);
        Assert.InRange(r[^1], 0.99f, 1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false, 203)]
    [InlineData(false, false, 10000)]
    [InlineData(false, true, 4000)]
    public void HdrYuvRampPreservesBlackWhiteAndSimdParity(bool hlg, bool fullRange, float peakNits = 1000)
    {
        var metadata = HdrFrameMetadata.Create(
            hlg ? AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67 : AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084,
            AVColorPrimaries.AVCOL_PRI_BT2020, AVColorSpace.AVCOL_SPC_BT2020_NCL,
            fullRange ? AVColorRange.AVCOL_RANGE_JPEG : AVColorRange.AVCOL_RANGE_MPEG, 10, peakNits);
        const int count = 257;
        var signal = Enumerable.Range(0, count).Select(i =>
            fullRange ? i / (float)(count - 1) : (64 + 876f * i / (count - 1)) / 1023).ToArray();
        var chroma = Enumerable.Repeat(512f / 1023, count).ToArray();
        var scalar = new[] { new float[count], new float[count], new float[count] };
        var simd = new[] { new float[count], new float[count], new float[count] };
        var mapper = new HdrToneMapper();
        mapper.MapSignals(metadata, signal, chroma, chroma, scalar[0], scalar[1], scalar[2], false);
        mapper.MapSignals(metadata, signal, chroma, chroma, simd[0], simd[1], simd[2], true);
        for (var c = 0; c < 3; c++)
        for (var i = 0; i < count; i++)
        {
            Assert.InRange(simd[c][i], -0.00001f, 1.00001f);
            Assert.InRange(Math.Abs(scalar[c][i] - simd[c][i]), 0, 0.00001f);
            Assert.InRange(Math.Abs(simd[c][i] - simd[0][i]), 0, 0.0001f);
            if (i > 0) Assert.True(simd[c][i] >= simd[c][i - 1]);
        }
        Assert.InRange(simd[0][0], 0, 0.00001f);
        Assert.InRange(simd[0][^1], 0.99f, 1);
    }

    [Fact]
    public void HlgInverseOetfMatchesReferencePoints()
    {
        Assert.Equal(0, HdrToneMapper.HlgToLinear(0));
        Assert.Equal(1f / 12, HdrToneMapper.HlgToLinear(0.5f), 6);
        Assert.Equal(1, HdrToneMapper.HlgToLinear(1), 6);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BrightColoredHighlightsKeepTheirColorInPqAndHlg(bool hlg)
    {
        var identity = new ColorMatrix(1, 0, 0, 0, 1, 0, 0, 0, 1);
        var metadata = new HdrFrameMetadata
        {
            Nonlinear = identity, Linear = identity, Offset = [0, 0, 0],
            BitDepth = 10, PeakNits = 1000, Transfer = hlg ? HdrTransfer.Hlg : HdrTransfer.Pq
        };
        var redSignal = Enumerable.Repeat(1f, 37).ToArray();
        var greenSignal = Enumerable.Repeat(0.8f, 37).ToArray();
        var blueSignal = Enumerable.Repeat(0.05f, 37).ToArray();
        var scalar = new[] { new float[37], new float[37], new float[37] };
        var simd = new[] { new float[37], new float[37], new float[37] };
        var mapper = new HdrToneMapper();
        mapper.MapSignals(metadata, redSignal, greenSignal, blueSignal, scalar[0], scalar[1], scalar[2], false);
        mapper.MapSignals(metadata, redSignal, greenSignal, blueSignal, simd[0], simd[1], simd[2], true);
        for (var index = 0; index < 37; index++)
        {
            Assert.InRange(simd[0][index], 0.99f, 1);
            Assert.True(simd[0][index] > simd[1][index]);
            Assert.True(simd[1][index] > simd[2][index]);
            Assert.True(simd[0][index] - simd[2][index] > 0.5f);
            for (var channel = 0; channel < 3; channel++)
            {
                Assert.InRange(simd[channel][index], 0, 1);
                Assert.InRange(Math.Abs(simd[channel][index] - scalar[channel][index]), 0, 0.00001f);
            }
        }
    }

    [Fact]
    public void HlgMidtonesStayBrightWithoutClippingSaturatedRed()
    {
        var metadata = HdrFrameMetadata.Create(AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67,
            AVColorPrimaries.AVCOL_PRI_BT2020, AVColorSpace.AVCOL_SPC_BT2020_NCL, AVColorRange.AVCOL_RANGE_MPEG, 10);
        var y = Enumerable.Repeat(400f / 1023, 37).ToArray();
        var u = Enumerable.Repeat(450f / 1023, 37).ToArray();
        var v = Enumerable.Repeat(700f / 1023, 37).ToArray();
        var red = new float[37]; var green = new float[37]; var blue = new float[37];
        new HdrToneMapper().MapSignals(metadata, y, u, v, red, green, blue, true);
        // 合成 HLG 红色色块：中间调保持亮度和通道余量，色相保持正确的分量顺序。
        Assert.InRange(red[0], 0.65f, 0.85f);
        Assert.InRange(green[0], 0.02f, 0.08f);
        Assert.InRange(blue[0], 0.025f, 0.12f);
        Assert.True(red[0] > blue[0] && blue[0] > green[0]);
    }

    [Fact]
    public void PqMidtonesKeepBrightnessWhileBlackAndPeakRemainAnchored()
    {
        var metadata = HdrFrameMetadata.Create(AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084,
            AVColorPrimaries.AVCOL_PRI_BT709, AVColorSpace.AVCOL_SPC_BT709, AVColorRange.AVCOL_RANGE_JPEG, 10);
        var signal = new[] { 0f, 20, 50, 100, 1000 }.Select(HdrToneMapper.NitsToPq).ToArray();
        var chroma = Enumerable.Repeat(512f / 1023, signal.Length).ToArray();
        var red = new float[signal.Length]; var green = new float[signal.Length]; var blue = new float[signal.Length];
        new HdrToneMapper().MapSignals(metadata, signal, chroma, chroma, red, green, blue, true);
        Assert.InRange(red[0], 0, 0.000001f);
        // 50/100 nit 中间调接近原始亮度，高光压缩不应使整个画面明显变暗。
        Assert.InRange(red[2] * 203, 45, 55);
        Assert.InRange(red[3] * 203, 90, 105);
        Assert.InRange(red[^1], 0.99f, 1);
        for (var index = 1; index < red.Length; index++)
            Assert.True(red[index] > red[index - 1]);
    }

    [Fact]
    public void WarmSignalMappingReusesBuffersWithoutManagedAllocations()
    {
        var metadata = IdentityMetadata();
        var mapper = new HdrToneMapper();
        var signal = Enumerable.Repeat(0.5f, 37).ToArray();
        var red = new float[37]; var green = new float[37]; var blue = new float[37];
        mapper.MapSignals(metadata, signal, signal, signal, red, green, blue, true);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 32; i++)
            mapper.MapSignals(metadata, signal, signal, signal, red, green, blue, true);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void ReusedMapperHandlesSizeTransferAndPeakChanges()
    {
        var mapper = new HdrToneMapper();
        foreach (var (width, hlg, peak) in new[] { (37, false, 1000), (8, true, 1000), (41, false, 4000), (37, false, 1000) })
        {
            var metadata = HdrFrameMetadata.Create(
                hlg ? AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67 : AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084,
                AVColorPrimaries.AVCOL_PRI_BT2020, AVColorSpace.AVCOL_SPC_BT2020_NCL, AVColorRange.AVCOL_RANGE_MPEG, 10, peak);
            var y = Enumerable.Range(0, width).Select(i => (64 + 876f * i / (width - 1)) / 1023).ToArray();
            var uv = Enumerable.Repeat(512f / 1023, width).ToArray();
            var expected = new[] { new float[width], new float[width], new float[width] };
            var actual = new[] { new float[width], new float[width], new float[width] };
            new HdrToneMapper().MapSignals(metadata, y, uv, uv, expected[0], expected[1], expected[2], false);
            mapper.MapSignals(metadata, y, uv, uv, actual[0], actual[1], actual[2], true);
            for (var channel = 0; channel < 3; channel++)
            for (var x = 0; x < width; x++)
                Assert.InRange(Math.Abs(expected[channel][x] - actual[channel][x]), 0, 0.00001f);
        }
    }

    private static ReshapePiece Polynomial(float a, float b, float c)
    {
        var piece = new ReshapePiece(0, 2);
        piece.Coefficients[0] = a; piece.Coefficients[1] = b; piece.Coefficients[2] = c;
        return piece;
    }

    private static HdrFrameMetadata IdentityMetadata() => HdrFrameMetadata.FromDolby(new DolbyVisionMetadata()
    {
        Curves = Enumerable.Range(0, 3).Select(_ =>
        {
            var curve = new ReshapeCurve(2); curve.Pivots[1] = 1;
            curve.Pieces[0] = Polynomial(0, 1, 0);
            return curve;
        }).ToArray(),
        Nonlinear = new ColorMatrix(1, 0, 0, 0, 1, 0, 0, 0, 1),
        Linear = new ColorMatrix(1, 0, 0, 0, 1, 0, 0, 0, 1),
        Offset = [0, 0, 0], BitDepth = 10, PeakNits = 1000
    });
}
