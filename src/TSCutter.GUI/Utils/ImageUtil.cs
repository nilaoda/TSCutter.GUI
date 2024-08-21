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
    public static Bitmap CreateBitmapFromFrame(Frame frame, int width, int height, int dpi = 96)
    {
        using VideoFrameConverter sws = new();
        using Frame dest = Frame.CreateVideo(width, height, AVPixelFormat.Bgra);
        sws.ConvertFrame(frame, dest);
        byte[] data = dest.ToImageBuffer();
        
        // Byte Count per line (BGRA)
        var stride = width * 4;
        
        // Convert byte array to Bitmap
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        var pointer = handle.AddrOfPinnedObject();
        var bitmap = new Bitmap(
            PixelFormat.Bgra8888,
            AlphaFormat.Premul,
            pointer,
            new PixelSize(width, height),
            new Vector(dpi, dpi),
            stride
        );
        handle.Free();

        return bitmap;
    }
}