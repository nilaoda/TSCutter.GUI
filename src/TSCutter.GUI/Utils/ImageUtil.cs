using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Swscales;
using Sdcb.FFmpeg.Utils;
using SkiaSharp;

namespace TSCutter.GUI.Utils;

public static class ImageUtil
{
    // 每次都创建一张纯黑图片作为音频的图
    public static Bitmap BlankImage => CreateAudioBitmap();

    // 缓存 VideoFrameConverter，避免每帧重新初始化 swscale 上下文
    private static VideoFrameConverter? _cachedSws;
    private static PixelSize _cachedSourceSize;
    private static PixelSize _cachedDestinationSize;

    public static unsafe Bitmap CreateBitmapFromFrame(Frame frame, int dpi = 96)
        => CreateBitmapFromFrame(frame, new PixelSize(frame.Width, frame.Height), dpi);

    public static unsafe Bitmap CreateScaledBitmapFromFrame(
        Frame frame,
        int maxWidth,
        int maxHeight,
        int dpi = 96)
    {
        var sourceSize = new PixelSize(frame.Width, frame.Height);
        var destinationSize = CalculateDecodeSize(sourceSize, new PixelSize(maxWidth, maxHeight));
        return CreateBitmapFromFrame(frame, destinationSize, dpi);
    }

    private static unsafe Bitmap CreateBitmapFromFrame(Frame frame, PixelSize destinationSize, int dpi)
    {
        var sourceSize = new PixelSize(frame.Width, frame.Height);
        var width = destinationSize.Width;
        var height = destinationSize.Height;

        // swscale 直接输出 BGRA 格式，消除逐像素 BGR→BGRA 转换
        if (_cachedSws == null || _cachedSourceSize != sourceSize || _cachedDestinationSize != destinationSize)
        {
            _cachedSws?.Dispose();
            _cachedSws = new VideoFrameConverter();
            _cachedSourceSize = sourceSize;
            _cachedDestinationSize = destinationSize;
        }

        using Frame dest = Frame.CreateVideo(width, height, AVPixelFormat.Bgra);
        _cachedSws.ConvertFrame(frame, dest);

        var writableBitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(dpi, dpi),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        using var buffer = writableBitmap.Lock();
        var srcData = dest.Data[0];
        var srcStride = dest.Linesize[0];
        var destRowBytes = buffer.RowBytes;

        // swscale 已输出 BGRA，整行 MemoryCopy 替代逐字节 Marshal 操作
        var copyBytesPerRow = Math.Min(srcStride, destRowBytes);
        for (var y = 0; y < height; y++)
        {
            var srcPtr = srcData + y * srcStride;
            var destPtr = buffer.Address + y * destRowBytes;
            Buffer.MemoryCopy((void*)srcPtr, (void*)destPtr, destRowBytes, copyBytesPerRow);
        }

        return writableBitmap;
    }

    internal static PixelSize CalculateDecodeSize(PixelSize sourceSize, PixelSize maximumSize)
    {
        if (sourceSize.Width <= 0 || sourceSize.Height <= 0 ||
            maximumSize.Width <= 0 || maximumSize.Height <= 0)
        {
            return sourceSize;
        }

        var scale = Math.Min(1d, Math.Min(
            maximumSize.Width / (double)sourceSize.Width,
            maximumSize.Height / (double)sourceSize.Height));
        return new PixelSize(
            Math.Min(maximumSize.Width, Math.Max(1, (int)Math.Round(sourceSize.Width * scale))),
            Math.Min(maximumSize.Height, Math.Max(1, (int)Math.Round(sourceSize.Height * scale))));
    }

    public static Bitmap CreateThumbnail(
        Bitmap source,
        int width = 160,
        int height = 90,
        double sourceDisplayAspectRatio = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        // 解码结果尺寸与目标一致且无需应用 SAR 时，可以直接复制。
        // 这里在 CPU 上复制，避免创建 RenderTargetBitmap；其后端资源缓存
        // 可能在长列表滚动时保留大量已经 Dispose 的纹理。
        var codedAspectRatio = source.PixelSize.Width / (double)source.PixelSize.Height;
        var hasDisplayAspectRatio = sourceDisplayAspectRatio > 0
            && double.IsFinite(sourceDisplayAspectRatio);
        if (source.PixelSize == new PixelSize(width, height)
            && (!hasDisplayAspectRatio || Math.Abs(sourceDisplayAspectRatio - codedAspectRatio) < 0.0001))
        {
            var copy = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Opaque);
            try
            {
                using var framebuffer = copy.Lock();
                source.CopyPixels(framebuffer, AlphaFormat.Opaque);
                return copy;
            }
            catch
            {
                copy.Dispose();
                throw;
            }
        }

        var thumbnail = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        try
        {
            using var context = thumbnail.CreateDrawingContext(true);
            context.FillRectangle(Brushes.Black, new Rect(0, 0, width, height));

            var sourceSize = source.Size;
            if (sourceSize.Width <= 0 || sourceSize.Height <= 0)
                return thumbnail;

            var targetRect = CalculateAspectFitRect(
                sourceSize,
                new Size(width, height),
                sourceDisplayAspectRatio);
            // 缩小位图时默认插值会明显软化，这里使用高质量插值。
            // 该路径只用于生成静态缩略图，不承担逐帧渲染的性能压力。
            using (context.PushRenderOptions(new RenderOptions
            {
                BitmapInterpolationMode = BitmapInterpolationMode.HighQuality
            }))
            {
                context.DrawImage(source, new Rect(sourceSize), targetRect);
            }
            return thumbnail;
        }
        catch
        {
            thumbnail.Dispose();
            throw;
        }
    }

    internal static Rect CalculateAspectFitRect(
        Size sourceSize,
        Size targetSize,
        double sourceDisplayAspectRatio = 0)
    {
        if (sourceSize.Width <= 0 || sourceSize.Height <= 0 ||
            targetSize.Width <= 0 || targetSize.Height <= 0)
        {
            return default;
        }

        // 缩略图按显示比例居中；SAR 有效时先还原变形像素对应的显示比例。
        var displayWidth = sourceDisplayAspectRatio > 0 && double.IsFinite(sourceDisplayAspectRatio)
            ? sourceSize.Height * sourceDisplayAspectRatio
            : sourceSize.Width;
        var scale = Math.Min(
            targetSize.Width / displayWidth,
            targetSize.Height / sourceSize.Height);
        var width = displayWidth * scale;
        var height = sourceSize.Height * scale;
        return new Rect(
            (targetSize.Width - width) / 2,
            (targetSize.Height - height) / 2,
            width,
            height);
    }

    /// <summary>
    /// Creates a bitmap with square display pixels for a non-square-pixel frame.
    /// Square-pixel frames return null so callers can use the source directly
    /// without introducing another resampling pass.
    /// </summary>
    public static Bitmap? CreateSampleAspectRatioCorrectedBitmap(
        Bitmap source,
        PixelSize displaySourceSize,
        bool requiresCorrection)
    {
        if (!requiresCorrection
            || source.PixelSize.Width <= 0
            || source.PixelSize.Height <= 0
            || displaySourceSize.Width <= 0
            || displaySourceSize.Height <= 0)
        {
            return null;
        }

        var displayAspectRatio = displaySourceSize.Width / (double)displaySourceSize.Height;
        var correctedWidth = (int)Math.Round(source.PixelSize.Height * displayAspectRatio);
        if (correctedWidth <= 0 || correctedWidth == source.PixelSize.Width)
            return null;

        var corrected = new RenderTargetBitmap(
            new PixelSize(correctedWidth, source.PixelSize.Height),
            new Vector(96, 96));
        try
        {
            using var context = corrected.CreateDrawingContext(true);
            using (context.PushRenderOptions(new RenderOptions
            {
                BitmapInterpolationMode = BitmapInterpolationMode.HighQuality
            }))
            {
                context.DrawImage(
                    source,
                    new Rect(source.Size),
                    new Rect(0, 0, correctedWidth, source.PixelSize.Height));
            }
            return corrected;
        }
        catch
        {
            corrected.Dispose();
            throw;
        }
    }

    private static Bitmap CreateAudioBitmap(int width = 1920, int height = 1080)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        using var ctx = bitmap.CreateDrawingContext(true);
        // 黑色背景
        ctx.FillRectangle(Brushes.Black, new Rect(0, 0, width, height));
        // 文字部分
        var formattedText = new FormattedText(
            $"Video decoding failed!{Environment.NewLine}{Environment.NewLine}" +
            $"You can still continue editing based on the audio track.",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            60.0,
            Brushes.White
        );
        // 计算居中位置
        var textPosition = new Point(
            (width - formattedText.Width) / 2,
            (height - formattedText.Height) / 2
        );
        // 绘制文字
        ctx.DrawText(formattedText, textPosition);

        return bitmap;
    }

    public static void SaveAsJpeg(Bitmap bitmap, Stream stream, int quality = 90)
    {
        var size = bitmap.PixelSize;
        var imageInfo = new SKImageInfo(
            size.Width,
            size.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Opaque);
        using var skBitmap = new SKBitmap(imageInfo);
        if (skBitmap.GetPixels() == IntPtr.Zero)
            throw new OutOfMemoryException("Unable to allocate the JPEG pixel buffer.");

        using (var framebuffer = new SkiaBitmapFramebuffer(skBitmap))
            bitmap.CopyPixels(framebuffer, AlphaFormat.Opaque);

        using var skImage = SKImage.FromBitmap(skBitmap);
        using var data = skImage.Encode(SKEncodedImageFormat.Jpeg, quality);
        data.SaveTo(stream);
    }

    public static async Task CopyBitmapToClipboardAsync(Bitmap bitmap, bool isPng)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopApp
            && desktopApp.MainWindow?.Clipboard is { } clipboard)
        {
            var dt = new DataTransfer();
            var bitmapItem = new DataTransferItem();
            bitmapItem.SetBitmap(bitmap);
            dt.Add(bitmapItem);

            if (!isPng)
            {
                using var jpgStream = new MemoryStream();
                SaveAsJpeg(bitmap, jpgStream);
                var jpgItem = new DataTransferItem();
                jpgItem.Set(DataFormat.CreateBytesPlatformFormat("public.jpeg"), jpgStream.ToArray());
                dt.Add(jpgItem);
            }

            await clipboard.SetDataAsync(dt);
        }
    }

    private sealed class SkiaBitmapFramebuffer(SKBitmap bitmap) : ILockedFramebuffer
    {
        public IntPtr Address => bitmap.GetPixels();

        public PixelSize Size => new(bitmap.Width, bitmap.Height);

        public int RowBytes => bitmap.RowBytes;

        public Vector Dpi => new(96, 96);

        public PixelFormat Format => PixelFormat.Bgra8888;

        public void Dispose()
        {
        }
    }
}
