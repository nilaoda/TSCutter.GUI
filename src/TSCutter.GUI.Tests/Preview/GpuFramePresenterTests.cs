using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using FFmpeg.AutoGen.Abstractions;
using SkiaSharp;
using TSCutter.GUI.Controls;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Rendering;
using TSCutter.GUI.Rendering.Gpu;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Tests.UI;
using Xunit;

namespace TSCutter.GUI.Tests.Preview;

[Collection("Avalonia UI")]
public sealed class GpuFramePresenterTests
{
    [NativeRuntimeFact]
    public async Task GpuFramesFallBackOnSoftwareSurfacesAndOwnTheirSource()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            unsafe
            {
                foreach (var hlg in new[] { false, true })
                {
                    using var source = Frame.CreateVideo(32, 16, AVPixelFormat.AV_PIX_FMT_YUV420P10LE);
                    AVFrame* raw = source;
                    raw->color_primaries = AVColorPrimaries.AVCOL_PRI_BT2020;
                    raw->color_trc = hlg ? AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67 : AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084;
                    raw->colorspace = AVColorSpace.AVCOL_SPC_BT2020_NCL;
                    raw->color_range = AVColorRange.AVCOL_RANGE_MPEG;
                    raw->chroma_location = AVChromaLocation.AVCHROMA_LOC_LEFT;
                    for (var plane = 0; plane < 3; plane++)
                    {
                        var width = plane == 0 ? 32 : 16;
                        var height = plane == 0 ? 16 : 8;
                        for (var y = 0; y < height; y++)
                        for (var x = 0; x < width; x++)
                            ((ushort*)(raw->data[(uint)plane] + y * raw->linesize[(uint)plane]))[x] = (ushort)(plane == 0
                                ? 64 + x * 876 / (width - 1) : 512 + (x / 4 % 3 - 1) * (plane == 1 ? 100 : -100));
                    }
                    using var expected = Frame.CreateVideo(16, 8, AVPixelFormat.AV_PIX_FMT_BGRA);
                    using var converter = new VideoFrameConverter();
                    converter.ConvertFrame(source, expected);
                    using var presenter = new SkiaVideoFramePresenter();
                    bool? renderedOnGpu = null;
                    var submitted = 0;
                    Assert.True(presenter.TryCreateFrame(source, true, new(16, 8), false,
                        (gpu, _) => renderedOnGpu = gpu, () => { }, out var presentation, () => submitted++));
                    Assert.NotNull(presentation);
                    using var gpuFrame = presentation;
                    source.Dispose();
                    var viewer = new ImageViewer { GpuFrame = gpuFrame, SourcePixelSize = new(32, 16) };
                    viewer.Measure(new Size(16, 8));
                    viewer.Arrange(new Rect(0, 0, 16, 8));
                    using var actual = new RenderTargetBitmap(new(16, 8), new Vector(96, 96));
                    Assert.Equal(0, submitted);
                    actual.Render(viewer);
                    Assert.Equal(1, submitted);
                    actual.Render(viewer);
                    Assert.Equal(1, submitted);
                    Assert.Equal(false, renderedOnGpu);
                    using var encoded = new MemoryStream();
                    actual.Save(encoded);
                    using var pixels = SKBitmap.Decode(encoded.ToArray());
                    AVFrame* reference = expected;
                    for (var y = 0; y < 8; y++)
                    for (var x = 0; x < 16; x++)
                    {
                        var color = pixels.GetPixel(x, y);
                        var value = reference->data[0] + y * reference->linesize[0] + x * 4;
                        Assert.InRange(Math.Abs(color.Red - value[2]), 0, 1);
                        Assert.InRange(Math.Abs(color.Green - value[1]), 0, 1);
                        Assert.InRange(Math.Abs(color.Blue - value[0]), 0, 1);
                    }
                    gpuFrame.Dispose();
                    gpuFrame.Dispose();
                }
            }
        }, CancellationToken.None);
    }

    [NativeRuntimeFact]
    public unsafe void SdrConversionHonorsMatrixAndRangeChanges()
    {
        using var source = Frame.CreateVideo(16, 16, AVPixelFormat.AV_PIX_FMT_YUV420P);
        using var destination = Frame.CreateVideo(16, 16, AVPixelFormat.AV_PIX_FMT_BGRA);
        using var converter = new VideoFrameConverter();
        AVFrame* raw = source;
        AVFrame* output = destination;
        for (var plane = 0; plane < 3; plane++)
        {
            var size = plane == 0 ? 16 : 8;
            for (var row = 0; row < size; row++)
                new Span<byte>(raw->data[(uint)plane] + row * raw->linesize[(uint)plane], size)
                    .Fill((byte)(plane == 0 ? 100 : plane == 1 ? 90 : 170));
        }
        foreach (var matrix in new[] { AVColorSpace.AVCOL_SPC_BT709, AVColorSpace.AVCOL_SPC_SMPTE170M })
        foreach (var full in new[] { false, true })
        {
            raw->colorspace = matrix;
            raw->color_range = full ? AVColorRange.AVCOL_RANGE_JPEG : AVColorRange.AVCOL_RANGE_MPEG;
            converter.ConvertFrame(source, destination);
            var y = full ? 100 : (100 - 16) * 255.0 / 219;
            var cb = -38 * (full ? 1 : 255.0 / 224);
            var cr = 42 * (full ? 1 : 255.0 / 224);
            var kr = matrix == AVColorSpace.AVCOL_SPC_BT709 ? 0.2126 : 0.299;
            var kb = matrix == AVColorSpace.AVCOL_SPC_BT709 ? 0.0722 : 0.114;
            Assert.InRange(Math.Abs(output->data[0][2] - (y + 2 * (1 - kr) * cr)), 0, 2);
            Assert.InRange(Math.Abs(output->data[0][1] - (y - 2 * kb * (1 - kb) / (1 - kr - kb) * cb
                - 2 * kr * (1 - kr) / (1 - kr - kb) * cr)), 0, 2);
            Assert.InRange(Math.Abs(output->data[0][0] - (y + 2 * (1 - kb) * cb)), 0, 2);
        }
    }

    [Fact]
    public void NullPresenter_AlwaysFallsBackWithoutOwningFrame()
    {
        using var presenter = new NullGpuFramePresenter("test");
        var accepted = presenter.TryPresent(null!, out var lease);

        Assert.False(accepted);
        Assert.Null(lease);
        Assert.False(presenter.Capabilities.IsAvailable);
        Assert.Equal("test", presenter.Capabilities.UnavailableReason);
    }
}
