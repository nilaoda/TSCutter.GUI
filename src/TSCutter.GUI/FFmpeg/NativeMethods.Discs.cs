using System.Runtime.CompilerServices;
using FFmpeg.AutoGen.Abstractions;
using System.Runtime.InteropServices;

namespace TSCutter.GUI.FFmpeg;

// 光盘枚举所需的 libbluray / libdvdread 最小接口。与 FFmpeg 库相同，通过
// FFmpegNativeBootstrapper 从运行库目录解析（"tscutter." 前缀），随 FFmpeg 一同分发。
public static unsafe partial class NativeMethods
{
    public const int DVD_READ_INFO_FILE = 0;
    public const int DVD_READ_INFO_BACKUP_FILE = 1;
    public const int DVD_READ_MENU_VOBS = 2;
    public const int DVD_READ_TITLE_VOBS = 3;

    public const byte BlurayTitlesAll = 0;

    [LibraryImport("tscutter.avformat")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial AVIOContext* avio_alloc_context(byte* buffer, int size, int write,
        nint opaque, nint read, nint writePacket, nint seek);

    [LibraryImport("tscutter.avformat")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void avio_context_free(AVIOContext** context);

    [LibraryImport("tscutter.avutil")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void av_free(void* pointer);

    [LibraryImport("tscutter.bluray")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void bd_get_version(out int major, out int minor, out int micro);

    [LibraryImport("tscutter.bluray")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int bd_select_playlist(nint bd, uint playlist);

    [LibraryImport("tscutter.bluray")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int bd_read(nint bd, byte* buffer, int size);

    [LibraryImport("tscutter.bluray")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial long bd_seek(nint bd, ulong position);

    [LibraryImport("tscutter.bluray")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial long bd_seek_time(nint bd, ulong ticks);

    [LibraryImport("tscutter.bluray")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial ulong bd_tell(nint bd);

    [LibraryImport("tscutter.bluray")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial ulong bd_get_title_size(nint bd);

    [LibraryImport("tscutter.bluray")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int bd_select_angle(nint bd, uint angle);

    [LibraryImport("tscutter.bluray", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint bd_open(string devicePath, nint keyFilePath);

    [LibraryImport("tscutter.bluray")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void bd_close(nint bd);

    [LibraryImport("tscutter.bluray")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint bd_get_disc_info(nint bd);

    [LibraryImport("tscutter.bluray")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint bd_get_titles(nint bd, byte flags, uint minimumTitleLength);

    [LibraryImport("tscutter.bluray")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint bd_get_title_info(nint bd, uint titleIndex, uint angle);

    [LibraryImport("tscutter.bluray")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void bd_free_title_info(nint titleInfo);

    [LibraryImport("tscutter.dvdread", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint DVDOpen(string path);

    [LibraryImport("tscutter.dvdread")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void DVDClose(nint dvd);

    [LibraryImport("tscutter.dvdread")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint ifoOpen(nint dvd, int titleSetNumber);

    [LibraryImport("tscutter.dvdread")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void ifoClose(nint ifo);

    [LibraryImport("tscutter.dvdread")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint DVDOpenFile(nint dvd, int titleNumber, int domain);

    [LibraryImport("tscutter.dvdread")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint DVDReadBytes(nint file, void* buffer, nuint size);

    [LibraryImport("tscutter.dvdread")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint DVDReadBlocks(nint file, int offset, nuint blocks, byte* buffer);

    [LibraryImport("tscutter.dvdread")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint DVDFileSize(nint file);

    [LibraryImport("tscutter.dvdread")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void DVDCloseFile(nint file);
}

// libbluray bluray.h 结构（LGPL-2.1）与 libdvdread ifo_types.h 结构（GPL-2+）。
// 只绑定用到的成员；字段顺序即原生布局，专用于布局的私有字段不参与逻辑。
#pragma warning disable CS0649
public static unsafe class DiscStructs
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct BlurayDiscInfo
    {
        public byte BlurayDetected;
        private nint discName;
        private nint udfVolumeId;
        public fixed byte DiscId[20];
        public byte NoMenuSupport;
        public byte FirstPlaySupported;
        public byte TopMenuSupported;
        public uint NumTitles;
        private nint titles;
        private nint firstPlay;
        private nint topMenu;
        public uint NumHdmvTitles;
        public uint NumBdjTitles;
        public uint NumUnsupportedTitles;
        public byte BdjDetected;
        private byte bdjSupported;
        public byte LibJvmDetected;
        public byte BdjHandled;
        public fixed byte BdjOrgId[9];
        public fixed byte BdjDiscId[33];
        public byte VideoFormat;
        public byte FrameRate;
        public byte ContentExist3D;
        public byte InitialOutputModePreference;
        public fixed byte ProviderData[32];
        public byte AacsDetected;
        public byte LibAacsDetected;
        public byte AacsHandled;
        public int AacsErrorCode;
        public int AacsMkbv;
        public byte BdPlusDetected;
        public byte LibBdPlusDetected;
        public byte BdPlusHandled;
        public byte BdPlusGen;
        public uint BdPlusDate;

        public readonly string? DiscName => discName == 0 ? null : Marshal.PtrToStringUTF8(discName);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BlurayStreamInfo
    {
        public byte CodingType;
        public byte Format;
        public byte Rate;
        public byte CharCode;
        public fixed byte Lang[4];
        public ushort Pid;
        public byte Aspect;
        public byte SubpathId;
        // libbluray >= 1.4：动态范围/色域扩展字段，占位以保持数组步长。
        public fixed byte RangeExtension[4];

        public readonly string Language
        {
            get
            {
                fixed (byte* p = Lang)
                {
                    if (p[0] == 0)
                        return "";
                    return $"{(char)p[0]}{(char)p[1]}{(char)p[2]}";
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BlurayClipInfo
    {
        public uint PktCount;
        public byte StillMode;
        public ushort StillTime;
        public byte VideoStreamCount;
        public byte AudioStreamCount;
        public byte PgStreamCount;
        public byte IgStreamCount;
        public byte SecAudioStreamCount;
        public byte SecVideoStreamCount;
        public byte DvStreamCount;
        private nint videoStreams;
        private nint audioStreams;
        private nint pgStreams;
        private nint igStreams;
        private nint secAudioStreams;
        private nint secVideoStreams;
        private nint dvStreams;
        public ulong StartTime;
        public ulong InTime;
        public ulong OutTime;
        public fixed byte ClipId[6];

        public readonly BlurayStreamInfo* VideoStreams => (BlurayStreamInfo*)videoStreams;
        public readonly BlurayStreamInfo* AudioStreams => (BlurayStreamInfo*)audioStreams;
        public readonly BlurayStreamInfo* PgStreams => (BlurayStreamInfo*)pgStreams;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BlurayTitleChapter
    {
        public uint Idx;
        public ulong Start;
        public ulong Duration;
        public ulong Offset;
        public uint ClipRef;
        // libbluray 1.5 新增章节名称指针，必须计入结构大小以正确遍历章节数组。
        public nint ChapterName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BlurayTitleInfo
    {
        public uint Idx;
        public uint Playlist;
        public ulong Duration;
        public uint ClipCount;
        public byte AngleCount;
        public uint ChapterCount;
        public uint MarkCount;
        private nint clips;
        private nint chapters;
        private nint marks;
        public byte MvcBaseViewRFlag;

        public readonly BlurayClipInfo* Clips => (BlurayClipInfo*)clips;
        public readonly BlurayTitleChapter* Chapters => (BlurayTitleChapter*)chapters;
    }

    // ifo_handle_t：15 个指针按 ifo_types.h 顺序（6.x/7.x 一致），
    // 仅使用 VMG 的 tt_srpt；VTS 结构因 libdvdread 版本间布局有差异，
    // 一律改由 DvdDisc 读取 IFO 原始字节并按 DVD-Video 在盘格式解析。
    [StructLayout(LayoutKind.Sequential)]
    internal struct DvdIfoHandle
    {
        private nint vmgiMat;
        private nint ttSrpt;
        private nint firstPlayPgc;
        private nint ptlMait;
        private nint vtsAtrt;
        private nint txtdtMgi;
        private nint pgciUt;
        private nint menuCAdt;
        private nint menuVobuAdmap;
        private nint vtsiMat;
        private nint vtsPttSrpt;
        private nint vtsPgcit;
        private nint vtsTmapt;
        private nint vtsCAdt;
        private nint vtsVobuAdmap;

        public readonly nint TtSrpt => ttSrpt;
        public readonly nint VtsiMat => vtsiMat;
        public readonly nint VtsPttSrpt => vtsPttSrpt;
        public readonly nint VtsPgcit => vtsPgcit;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct DvdTtSrpt
    {
        public ushort NrOfSrpts;
        private ushort zero1;
        private uint lastByte;
        public nint Titles;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct DvdTitleInfo
    {
        private byte playbackType;
        public byte NrOfAngles;
        public ushort NrOfPtts;
        public ushort ParentalId;
        public byte TitleSetNr;
        public byte VtsTtn;
        public uint TitleSetSector;
    }
}
#pragma warning restore CS0649
