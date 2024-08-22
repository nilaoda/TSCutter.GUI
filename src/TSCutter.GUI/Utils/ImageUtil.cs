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
        using Frame dest = Frame.CreateVideo(width, height, AVPixelFormat.Bgra);
        sws.ConvertFrame(frame, dest);
        
        var stride = width * 4;
        return new Bitmap(
            PixelFormat.Bgra8888,
            AlphaFormat.Premul,
            dest.Data[0],
            new PixelSize(width, height),
            new Vector(dpi, dpi),
            stride
        );
    }
}