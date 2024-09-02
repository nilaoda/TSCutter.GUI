using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Swscales;
using Sdcb.FFmpeg.Utils;

namespace TSCutter.GUI.Utils;

public static class ImageUtil
{
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
}