using System;
using System.Threading;
using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using FFmpeg.AutoGen.Abstractions;
using SkiaSharp;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Rendering.Colors;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Rendering.Gpu;

/// <summary>向 Skia GPU 提交原始 YUV 平面；转色、缩放和 HDR 映射在片元着色器中完成。</summary>
internal sealed unsafe class SkiaVideoFramePresenter : IDisposable
{
    private static readonly Lazy<LookupTable> TransferImage = new(() => CreateTable(
        HdrToneMapper.CreateGpuTransferTable(), HdrToneMapper.GpuTableWidth, HdrToneMapper.GpuTableHeight));
    private static int rendererAvailable;
    private readonly HdrMetadataReader metadataReader = new();
    private readonly HdrToneMapper tableBuilder = new(1);
    private LookupTable? toneImage, gamutImage;
    private float previousPeak;
    private ColorMatrix? previousGamut;
    private ColorMatrix fromLms;
    private int failed, validated;
    private readonly Action<string> disable;
    private readonly Action markValidated;

    internal SkiaVideoFramePresenter()
    {
        disable = Disable;
        markValidated = () => Volatile.Write(ref validated, 1);
    }

    internal static bool IsRendererAvailable => Volatile.Read(ref rendererAvailable) != 0;

    internal static void Probe(DrawingContext context, Rect bounds) => context.Custom(new ProbeOperation(bounds));

    internal bool TryPresent(Frame source, bool toneMapping, PixelSize fallbackSize,
        bool renderAtRequestedSize, Action<bool, bool> rendered, Action dropped, out SkiaVideoFrame? presentation,
        Action? submitted = null)
    {
        presentation = null;
        if (Volatile.Read(ref rendererAvailable) == 0 || Volatile.Read(ref failed) != 0)
            return false;
        return TryCreateFrame(source, toneMapping, fallbackSize, renderAtRequestedSize, rendered, dropped, out presentation, submitted);
    }

    // 准备与 GPU 后端无关的帧资源，实际绘制仍需通过 GPU 探测。
    internal bool TryCreateFrame(Frame source, bool toneMapping, PixelSize fallbackSize,
        bool renderAtRequestedSize, Action<bool, bool> rendered, Action dropped, out SkiaVideoFrame? presentation,
        Action? submitted = null)
    {
        presentation = null;
        AVFrame* raw = source;
        var metadataFrame = *raw;
        var hardware = source.HasHardwareFramesContext;
        if (hardware)
        {
            if (!MacOSVideoTexture.IsAvailable || !MacOSVideoTexture.TryDescribe(source, out var format, out var range))
                return false;
            metadataFrame.format = (int)format;
            metadataFrame.color_range = range;
        }
        var descriptor = FF.av_pix_fmt_desc_get((AVPixelFormat)metadataFrame.format);
        if (!hardware && !Supports(raw, descriptor))
            return false;
        ShaderParameters? parameters = null;
        ShaderPair? shaders = null;
        try
        {
            var hdrMetadata = toneMapping ? metadataReader.Read(&metadataFrame) : null;
            var colorSpace = VideoColorSpace.ResolveMatrix(&metadataFrame);
            var metadata = hdrMetadata ?? HdrFrameMetadata.Create(AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084,
                AVColorPrimaries.AVCOL_PRI_BT709, colorSpace, VideoColorSpace.IsFullRange(&metadataFrame)
                    ? AVColorRange.AVCOL_RANGE_JPEG : AVColorRange.AVCOL_RANGE_MPEG, descriptor->comp[0].depth);
            PrepareTables(metadata);
            var uniforms = new SKRuntimeEffectUniforms(VideoColorShader.Effect);
            FillUniforms(uniforms, metadata, &metadataFrame, descriptor, hdrMetadata is not null);
            parameters = CreateParameters(uniforms);
            if (!hardware)
            {
                using var y = CreatePlane(source, descriptor, 0);
                using var u = CreatePlane(source, descriptor, 1);
                // NV12/P010 的两个色度分量共享纹理，不重复上传同一块交错平面。
                using var v = descriptor->comp[1].plane == descriptor->comp[2].plane
                    ? null : CreatePlane(source, descriptor, 2);
                shaders = parameters.Create(y, u, v ?? u, Volatile.Read(ref validated) == 0);
                parameters.Dispose();
                parameters = null;
            }
            presentation = new SkiaVideoFrame(shaders, parameters, source, toneMapping, fallbackSize,
                renderAtRequestedSize, rendered, dropped, disable, markValidated, submitted);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            parameters?.Dispose();
            shaders?.Dispose();
            Disable(exception.Message);
            return false;
        }
    }

    private ShaderParameters CreateParameters(SKRuntimeEffectUniforms uniforms)
    {
        var images = new[] { TransferImage.Value.High, toneImage!.High, gamutImage!.High,
            TransferImage.Value.Fine, toneImage.Fine, gamutImage.Fine };
        var lookups = new SKShader[images.Length];
        try
        {
            for (var index = 0; index < images.Length; index++)
                lookups[index] = images[index].ToShader();
            return new ShaderParameters(uniforms, lookups, Volatile.Read(ref validated) == 0);
        }
        catch
        {
            foreach (var lookup in lookups)
                lookup?.Dispose();
            throw;
        }
    }

    private void Disable(string reason)
    {
        if (Interlocked.Exchange(ref failed, 1) == 0)
            Console.WriteLine("GPU video presentation unavailable; using CPU conversion: " + reason);
    }

    private static bool Supports(AVFrame* frame, AVPixFmtDescriptor* descriptor)
    {
        const ulong unsupported = ffmpeg.AV_PIX_FMT_FLAG_BE | ffmpeg.AV_PIX_FMT_FLAG_PAL
            | ffmpeg.AV_PIX_FMT_FLAG_BITSTREAM | ffmpeg.AV_PIX_FMT_FLAG_HWACCEL | ffmpeg.AV_PIX_FMT_FLAG_RGB
            | ffmpeg.AV_PIX_FMT_FLAG_BAYER | ffmpeg.AV_PIX_FMT_FLAG_FLOAT;
        if (descriptor == null || descriptor->nb_components != 3 || (descriptor->flags & unsupported) != 0
            || frame->width <= 0 || frame->height <= 0)
            return false;
        var depth = descriptor->comp[0].depth;
        if (depth is < 8 or > 16)
            return false;
        var bytes = depth <= 8 ? 1 : 2;
        var interleaved = descriptor->comp[1].plane == descriptor->comp[2].plane;
        for (var component = 0; component < 3; component++)
        {
            var info = descriptor->comp[(uint)component];
            var expectedStep = component > 0 && interleaved ? bytes * 2 : bytes;
            var expectedOffset = component == 2 && interleaved ? bytes : 0;
            var subX = component == 0 ? 0 : descriptor->log2_chroma_w;
            var width = (frame->width + (1 << subX) - 1) >> subX;
            if (info.depth != depth || info.step != expectedStep || info.offset != expectedOffset
                || info.shift < 0 || info.shift + depth > bytes * 8 || info.plane is < 0 or > 3
                || frame->data[(uint)info.plane] == null || frame->linesize[(uint)info.plane] < width * expectedStep)
                return false;
        }
        return true;
    }

    internal static SKImage CreatePlane(Frame source, AVPixFmtDescriptor* descriptor, int component)
    {
        AVFrame* frame = source;
        var info = descriptor->comp[(uint)component];
        var subX = component == 0 ? 0 : descriptor->log2_chroma_w;
        var subY = component == 0 ? 0 : descriptor->log2_chroma_h;
        var wide = info.depth > 8;
        var interleaved = component > 0 && descriptor->comp[1].plane == descriptor->comp[2].plane;
        var color = interleaved ? wide ? SKColorType.Rg1616 : SKColorType.Rg88
            : wide ? SKColorType.Alpha16 : SKColorType.Gray8;
        var imageInfo = new SKImageInfo((frame->width + (1 << subX) - 1) >> subX,
            (frame->height + (1 << subY) - 1) >> subY, color,
            wide && !interleaved ? SKAlphaType.Premul : SKAlphaType.Opaque);
        using var pixmap = new SKPixmap(imageInfo, (nint)frame->data[(uint)info.plane], frame->linesize[(uint)info.plane]);
        var retained = source.CloneReference();
        try
        {
            // SKImage 的原生引用也持有这一帧，GPU 上传及在途绘制结束前不归还解码缓冲。
            return SKImage.FromPixels(pixmap, static (_, context) => ((Frame)context).Dispose(), retained)
                ?? throw new NotSupportedException("Unsupported GPU image plane.");
        }
        catch { retained.Dispose(); throw; }
    }

    private void PrepareTables(HdrFrameMetadata metadata)
    {
        if (toneImage is null || metadata.PeakNits != previousPeak)
        {
            var next = CreateTable(tableBuilder.CreateGpuToneTable(metadata.PeakNits),
                HdrToneMapper.GpuTableWidth, HdrToneMapper.GpuTableHeight);
            toneImage?.Dispose();
            toneImage = next;
            previousPeak = metadata.PeakNits;
        }
        if (gamutImage is null || previousGamut != metadata.GamutTo709)
        {
            var next = CreateTable(HdrToneMapper.CreateGpuGamutTable(metadata.GamutTo709, out fromLms), 360, 1);
            gamutImage?.Dispose();
            gamutImage = next;
            previousGamut = metadata.GamutTo709;
        }
    }

    // 双半精度纹理保留低位残差；兼容 Skia 2.x，同时避免直接上传 F32 时被量化成 8 位。
    private static LookupTable CreateTable(float[] values, int width, int height)
    {
        var high = new Half[values.Length];
        var fine = new Half[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            high[index] = (Half)values[index];
            fine[index] = index % 4 == 3 ? (Half)1 : (Half)((values[index] - (float)high[index]) * 2048 + 0.5f);
        }
        var image = CreateHalfImage(high, width, height);
        try { return new LookupTable(image, CreateHalfImage(fine, width, height)); }
        catch { image.Dispose(); throw; }
    }

    private static SKImage CreateHalfImage(Half[] values, int width, int height)
    {
        fixed (Half* pointer = values)
            return SKImage.FromPixelCopy(new SKImageInfo(width, height, SKColorType.RgbaF16, SKAlphaType.Opaque),
                (nint)pointer, width * 4 * sizeof(Half));
    }

    private sealed record LookupTable(SKImage High, SKImage Fine) : IDisposable
    {
        public void Dispose() { High.Dispose(); Fine.Dispose(); }
    }

    private void FillUniforms(SKRuntimeEffectUniforms uniforms, HdrFrameMetadata metadata,
        AVFrame* frame, AVPixFmtDescriptor* descriptor, bool hdr)
    {
        var scales = new float[3];
        var alpha = new float[3];
        var interleaved = descriptor->comp[1].plane == descriptor->comp[2].plane;
        for (var component = 0; component < 3; component++)
        {
            var info = descriptor->comp[(uint)component];
            scales[component] = (info.depth <= 8 ? 255f : 65535f) / (((1 << info.depth) - 1f) * (1 << info.shift));
            alpha[component] = info.depth > 8 && (component == 0 || !interleaved) ? 1 : 0;
        }
        var location = frame->chroma_location;
        uniforms["sampleScale"] = scales;
        uniforms["sampleAlpha"] = alpha;
        uniforms["subsampling"] = new float[] { 1 << descriptor->log2_chroma_w, 1 << descriptor->log2_chroma_h };
        uniforms["chromaOffset"] = new float[]
        {
            descriptor->log2_chroma_w > 0 && location is AVChromaLocation.AVCHROMA_LOC_CENTER
                or AVChromaLocation.AVCHROMA_LOC_TOP or AVChromaLocation.AVCHROMA_LOC_BOTTOM ? 0.5f : 0,
            descriptor->log2_chroma_h == 0 || location is AVChromaLocation.AVCHROMA_LOC_TOPLEFT
                or AVChromaLocation.AVCHROMA_LOC_TOP ? 0 : location is AVChromaLocation.AVCHROMA_LOC_BOTTOMLEFT
                or AVChromaLocation.AVCHROMA_LOC_BOTTOM ? 1 : 0.5f
        };
        uniforms["interleaved"] = interleaved ? 1f : 0;
        uniforms["validation"] = 0f;
        uniforms["hdr"] = hdr ? 1f : 0;
        uniforms["hlg"] = metadata.Transfer == HdrTransfer.Hlg ? 1f : 0;
        uniforms["reshape"] = metadata.Curves is null ? 0f : 1;
        uniforms["perceptual"] = metadata.GamutTo709 == new ColorMatrix(1, 0, 0, 0, 1, 0, 0, 0, 1) ? 0f : 1;
        uniforms["signalOffset"] = metadata.Offset;
        SetMatrix(uniforms, "nonlinear", metadata.Nonlinear);
        SetMatrix(uniforms, "linear", metadata.Linear);
        SetMatrix(uniforms, "fromLms", fromLms);
        var counts = new float[3]; var pivots = new float[27]; var methods = new float[24];
        var orders = new float[24]; var constants = new float[24]; var coefficients = new float[504];
        if (metadata.Curves is { } curves)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                var curve = curves[channel];
                counts[channel] = curve.Pieces.Length;
                curve.Pivots.CopyTo(pivots, channel * 9);
                for (var piece = 0; piece < curve.Pieces.Length; piece++)
                {
                    var slot = channel * 8 + piece;
                    var value = curve.Pieces[piece];
                    methods[slot] = value.Method; orders[slot] = value.Order; constants[slot] = value.Constant;
                    value.Coefficients.CopyTo(coefficients, slot * 21);
                }
            }
        }
        uniforms["curveCounts"] = counts; uniforms["pivots"] = pivots; uniforms["methods"] = methods;
        uniforms["orders"] = orders; uniforms["constants"] = constants; uniforms["coefficients"] = coefficients;
    }

    private static void SetMatrix(SKRuntimeEffectUniforms uniforms, string prefix, ColorMatrix matrix)
    {
        uniforms[prefix + "0"] = new float[] { matrix.M00, matrix.M01, matrix.M02 };
        uniforms[prefix + "1"] = new float[] { matrix.M10, matrix.M11, matrix.M12 };
        uniforms[prefix + "2"] = new float[] { matrix.M20, matrix.M21, matrix.M22 };
    }

    public void Dispose()
    {
        toneImage?.Dispose(); toneImage = null;
        gamutImage?.Dispose(); gamutImage = null;
    }

    internal sealed class ShaderParameters(SKRuntimeEffectUniforms uniforms, SKShader[] lookups, bool needsValidation) : IDisposable
    {
        internal bool NeedsValidation { get; } = needsValidation;
        internal ShaderPair Create(SKImage y, SKImage u, SKImage v, bool validate)
        {
            var effect = VideoColorShader.Effect;
            using var yShader = y.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
            using var uShader = u.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
            using var vShader = v.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
            var children = new SKRuntimeEffectChildren(effect)
            {
                ["planeY"] = yShader, ["planeU"] = uShader, ["planeV"] = vShader,
                ["transferTable"] = lookups[0], ["toneTable"] = lookups[1], ["gamutTable"] = lookups[2],
                ["transferFine"] = lookups[3], ["toneFine"] = lookups[4], ["gamutFine"] = lookups[5]
            };
            SKShader? validation = null;
            try
            {
                if (validate)
                {
                    uniforms["validation"] = 1f;
                    validation = effect.ToShader(true, uniforms, children)
                        ?? throw new NotSupportedException("Cannot create the GPU validation shader.");
                }
                uniforms["validation"] = 0f;
                return new ShaderPair(effect.ToShader(true, uniforms, children)
                    ?? throw new NotSupportedException("Cannot create the video shader."), validation);
            }
            catch
            {
                validation?.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            foreach (var lookup in lookups)
                lookup.Dispose();
        }
    }

    internal sealed record ShaderPair(SKShader Shader, SKShader? Validation) : IDisposable
    {
        public void Dispose()
        {
            Shader.Dispose();
            Validation?.Dispose();
        }
    }

    private sealed class ProbeOperation(Rect bounds) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point point) => false;
        public bool Equals(ICustomDrawOperation? other) => other is ProbeOperation operation && Bounds == operation.Bounds;
        public void Dispose() { }
        public void Render(ImmediateDrawingContext context)
        {
            try
            {
                var feature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
                using var lease = feature?.Lease();
                Volatile.Write(ref rendererAvailable, lease?.GrContext is { IsAbandoned: false } ? 1 : 0);
                MacOSVideoTexture.Probe(lease?.GrContext);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Volatile.Write(ref rendererAvailable, 0);
            }
        }
    }
}
