using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;

namespace TSCutter.GUI.FFmpeg;

public static unsafe partial class NativeMethods
{
    internal const int AssFeatureWrapUnicode = 3;
    internal const int CodecPropBitmapSubtitle = 1 << 16;
    internal const int CodecPropTextSubtitle = 1 << 17;

    [LibraryImport("tscutter.avcodec")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int avcodec_decode_subtitle2(AVCodecContext* context, AVSubtitle* subtitle, int* got, AVPacket* packet);

    [LibraryImport("tscutter.avcodec")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void avsubtitle_free(AVSubtitle* subtitle);

    [LibraryImport("tscutter.avcodec")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial AVCodec* avcodec_find_encoder(AVCodecID id);

    [LibraryImport("tscutter.avcodec")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int avcodec_encode_subtitle(AVCodecContext* context, byte* buffer, int size, AVSubtitle* subtitle);

    [LibraryImport("tscutter.avcodec")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int avcodec_parameters_from_context(AVCodecParameters* parameters, AVCodecContext* context);

    [LibraryImport("tscutter.ass")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint ass_library_init();
    [LibraryImport("tscutter.ass")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void ass_library_done(nint library);
    [LibraryImport("tscutter.ass")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint ass_renderer_init(nint library);
    [LibraryImport("tscutter.ass")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void ass_renderer_done(nint renderer);
    [LibraryImport("tscutter.ass")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint ass_new_track(nint library);
    [LibraryImport("tscutter.ass")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void ass_free_track(nint track);
    [LibraryImport("tscutter.ass")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void ass_set_frame_size(nint renderer, int width, int height);
    [LibraryImport("tscutter.ass")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void ass_set_storage_size(nint renderer, int width, int height);
    [LibraryImport("tscutter.ass")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void ass_set_pixel_aspect(nint renderer, double aspect);
    [LibraryImport("tscutter.ass", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void ass_set_fonts(nint renderer, string? font, string family, int provider, string? config, int update);
    [LibraryImport("tscutter.ass", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void ass_add_font(nint library, string name, byte* data, int size);
    [LibraryImport("tscutter.ass")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void ass_process_codec_private(nint track, byte* data, int size);
    [LibraryImport("tscutter.ass")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void ass_process_chunk(nint track, byte* data, int size, long start, long duration);
    [LibraryImport("tscutter.ass")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ass_track_set_feature(nint track, int feature, int enabled);
    [LibraryImport("tscutter.ass")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial AssImage* ass_render_frame(nint renderer, nint track, long time, int* changed);
}

// 布局对应 libass 0.17.4 的 ASS_Image；位图指针只在下一次渲染前有效。
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AssImage
{
    public int Width, Height, Stride;
    public byte* Bitmap;
    public uint Color;
    public int X, Y;
    public AssImage* Next;
    public int Type;
}
