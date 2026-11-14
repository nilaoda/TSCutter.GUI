// 码流解析器绑定：把 PES 负载切成完整的编码帧（多轨合并解码来源音轨时使用）。
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;

namespace TSCutter.GUI.FFmpeg;

public static unsafe partial class NativeMethods
{
    [LibraryImport("tscutter.avcodec", EntryPoint = "av_parser_init")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial AVCodecParserContext* av_parser_init(int codecId);

    [LibraryImport("tscutter.avcodec", EntryPoint = "av_parser_parse2")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int av_parser_parse2(AVCodecParserContext* parser, AVCodecContext* context, byte** output,
        int* outputSize, byte* buffer, int bufferSize, long pts, long dts, long position);

    [LibraryImport("tscutter.avcodec", EntryPoint = "av_parser_close")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void av_parser_close(AVCodecParserContext* parser);
}
