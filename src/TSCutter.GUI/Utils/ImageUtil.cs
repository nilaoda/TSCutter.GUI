using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Swscales;
using Sdcb.FFmpeg.Utils;

namespace TSCutter.GUI.Utils;

public static class ImageUtil
{
    // 每次都创建一张纯黑图片作为音频的图
    public static Bitmap BlankImage => CreateAudioBitmap();
    
    public static Bitmap CreateBitmapFromFrame(Frame frame, int dpi = 96)
    {
        var width = frame.Width;
        var height = frame.Height;
        using VideoFrameConverter sws = new();
        using Frame dest = Frame.CreateVideo(width, height, AVPixelFormat.Bgr24);
        sws.ConvertFrame(frame, dest);
        
        var writableBitmap = new WriteableBitmap(
            new PixelSize(width, height), 
            new Vector(dpi, dpi), 
            PixelFormat.Bgra8888, 
            AlphaFormat.Opaque);

        using var buffer = writableBitmap.Lock();
        var destData = dest.Data[0]; // dest frame data。
        var stride = dest.Linesize[0]; // bytes per line。

        for (var y = 0; y < height; y++)
        {
            var sourcePtr = destData + y * stride;
            var destPtr = buffer.Address + y * buffer.RowBytes;
        
            for (var x = 0; x < width; x++)
            {
                var blue = Marshal.ReadByte(sourcePtr + x * 3);
                var green = Marshal.ReadByte(sourcePtr + x * 3 + 1);
                var red = Marshal.ReadByte(sourcePtr + x * 3 + 2);

                // write BGRA data
                Marshal.WriteByte(destPtr + x * 4, blue);
                Marshal.WriteByte(destPtr + x * 4 + 1, green);
                Marshal.WriteByte(destPtr + x * 4 + 2, red);
                Marshal.WriteByte(destPtr + x * 4 + 3, 255); // set alpha to 255
            }
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
}