using System;
using System.Globalization;
using System.IO;
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
    private static int _cachedWidth;
    private static int _cachedHeight;

    public static unsafe Bitmap CreateBitmapFromFrame(Frame frame, int dpi = 96)
    {
        var width = frame.Width;
        var height = frame.Height;

        // swscale 直接输出 BGRA 格式，消除逐像素 BGR→BGRA 转换
        if (_cachedSws == null || _cachedWidth != width || _cachedHeight != height)
        {
            _cachedSws?.Dispose();
            _cachedSws = new VideoFrameConverter();
            _cachedWidth = width;
            _cachedHeight = height;
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
        using var pngStream = new MemoryStream();
        bitmap.Save(pngStream);
        pngStream.Position = 0;
        using var skBitmap = SKBitmap.Decode(pngStream);
        using var skImage = SKImage.FromBitmap(skBitmap);
        using var data = skImage.Encode(SKEncodedImageFormat.Jpeg, quality);
        data.SaveTo(stream);
    }

    public static void CopyBitmapToClipboard(Bitmap bitmap, bool isPng)
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

            _ = clipboard.SetDataAsync(dt);
        }
    }
}