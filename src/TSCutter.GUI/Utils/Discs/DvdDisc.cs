using System;
using System.Buffers.Binary;
using System.Threading;
using System.Collections.Generic;
using System.IO;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Discs;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Utils.Discs;

/// <summary>
/// 通过 libdvdread 读取 DVD-Video 导航结构：标题列表（tt_srpt）、章节角度信息，
/// 以及 CSS 加密探测。VTS 侧的 PGC/章节时长按 DVD-Video 在盘格式（字节序与偏移
/// 由规范固定，与 libdvdread 构建版本无关）经 libdvdread 文件 API 读取原始 IFO
/// 字节解析；标题播放由 FFmpeg 的 dvdvideo 解复用器完成。
/// </summary>
internal sealed unsafe class DvdDisc : IDisposable
{
    private const int SectorBytes = 2048;

    private nint handle;

    private DvdDisc(nint handle) => this.handle = handle;

    public static DvdDisc Open(string sourcePath)
    {
        if (!DiscCapabilities.HasDvdReadLibrary)
            throw new DiscScanException(DiscFailureKind.RuntimeMissing,
                "libdvdread is not available in the FFmpeg runtime directory.");
        if (!Directory.Exists(sourcePath) && !HasDiscVolumeSignature(sourcePath))
            throw new DiscScanException(DiscFailureKind.NotDiscOrCorrupt,
                "The file has no ISO9660/UDF volume signature.");
        var handle = FF.DVDOpen(sourcePath);
        if (handle == 0)
            throw new DiscScanException(DiscFailureKind.NotDiscOrCorrupt,
                "DVDOpen failed: no VIDEO_TS structure was found.");
        return new DvdDisc(handle);
    }

    // libdvdread 对损坏镜像可能返回异常结构，先用卷签名快速排掉非镜像文件。
    internal static bool HasDiscVolumeSignature(string sourcePath)
    {
        using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 0x8809)
            return false;
        Span<byte> sector = stackalloc byte[2048];
        for (var s = 16; s <= 18; s++)
        {
            stream.Seek(s * 2048L, SeekOrigin.Begin);
            if (stream.Read(sector) < 2048)
                return false;
            var descriptor = System.Text.Encoding.ASCII.GetString(sector[1..6]);
            if (descriptor == "CD001" || descriptor is "BEA01" or "NSR02" or "NSR03")
                return true;
        }
        return false;
    }

    public List<(DiscTitleInfo Info, DiscInput Input)> EnumerateTitles(string sourcePath, CancellationToken cancellationToken = default)
    {
        var vmg = FF.ifoOpen(handle, 0);
        if (vmg == 0)
            throw new DiscScanException(DiscFailureKind.StructureUnsupported,
                "VIDEO_TS.IFO could not be parsed.");
        try
        {
            var ifo = (DiscStructs.DvdIfoHandle*)vmg;
            if (ifo->TtSrpt == 0)
                throw new DiscScanException(DiscFailureKind.StructureUnsupported,
                    "The VMG has no title search pointer table.");
            var ttSrpt = (DiscStructs.DvdTtSrpt*)ifo->TtSrpt;
            // 损坏镜像的 IFO 计数可能是任意值，DVD 最多 99 个标题。
            if (ttSrpt->NrOfSrpts == 0 || ttSrpt->NrOfSrpts > 99 || ttSrpt->Titles == 0)
                throw new DiscScanException(DiscFailureKind.StructureUnsupported,
                    "The VMG title table is invalid.");
            var titles = new List<(DiscTitleInfo, DiscInput)>();
            var titleSets = new HashSet<int>();
            for (var i = 0; i < ttSrpt->NrOfSrpts; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var title = ((DiscStructs.DvdTitleInfo*)ttSrpt->Titles)[i];
                titleSets.Add(title.TitleSetNr);
                titles.Add(CreateTitle(title, i + 1, sourcePath));
            }
            foreach (var titleSet in titleSets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProbeTitleVobReadable(titleSet);
            }
            return titles;
        }
        finally
        {
            FF.ifoClose(vmg);
        }
    }

    private (DiscTitleInfo, DiscInput) CreateTitle(DiscStructs.DvdTitleInfo title, int number, string sourcePath)
    {
        if (title.VtsTtn < 1 || title.TitleSetNr < 1 ||
            title.VtsTtn > 99 || title.TitleSetNr > 99)
            throw new DiscScanException(DiscFailureKind.StructureUnsupported,
                $"Title {number} has invalid title set pointers.");
        var duration = ReadTitleDuration(title.TitleSetNr, title.VtsTtn);
        var input = new DiscInput(DiscKind.Dvd, sourcePath, number, 1, duration, []);
        var info = new DiscTitleInfo(
            number,
            duration,
            title.NrOfPtts,
            title.NrOfAngles,
            ClipCount: 0,
            VideoSpec: null,
            [],
            [],
            Recommended: false);
        return (info, input);
    }

    /// <summary>读取 VTS IFO 原始字节，校验文件大小并拒绝截断数据。</summary>
    private byte[]? ReadVtsIfo(int titleSetNumber)
    {
        var file = FF.DVDOpenFile(handle, titleSetNumber, FF.DVD_READ_INFO_FILE);
        if (file == 0)
            return null;
        try
        {
            // DVDReadBytes 越界读取可能整笔失败：按扇区推进，直到 EOF 或错误。
            var size = (long)FF.DVDFileSize(file) * SectorBytes;
            if (size is <= 0 or > 8 * 1024 * 1024)
                throw new DiscScanException(DiscFailureKind.StructureUnsupported, "The VTS IFO size is invalid.");
            var buffer = new byte[(int)size];
            long total = 0;
            fixed (byte* pointer = buffer)
            {
                while (total < buffer.Length)
                {
                    var read = FF.DVDReadBytes(file, pointer + total, SectorBytes);
                    if (read <= 0)
                        throw new DiscScanException(DiscFailureKind.NotDiscOrCorrupt, "The VTS IFO is truncated.");
                    total += read;
                }
            }
            if (total <= 0)
                return null;
            return buffer[..(int)total];
        }
        finally
        {
            FF.DVDCloseFile(file);
        }
    }

    /// <summary>
    /// 标题时长：标题 PTT 链引用到的各 PGC 内，默认角度的 Cell 播放时间之和。
    /// 在盘偏移由 DVD-Video 规范固定：VTSI_MAT 的 vts_ptt_srpt/vts_pgcit
    /// 扇区号位于 0xC8/0xCC；vts_ptt_srpt 内是每标题的字节偏移表，
    /// pgcit 内是每 PGC 的 8 字节记录（pgc_start_byte 相对 pgcit 起始），
    /// PGC 的 Cell 播放表偏移位于 0xE8，每条 Cell 记录 24 字节。
    /// </summary>
    private TimeSpan ReadTitleDuration(int titleSetNumber, int vtsTtn)
    {
        var ifo = ReadVtsIfo(titleSetNumber);
        if (ifo is null)
            throw new DiscScanException(DiscFailureKind.NotDiscOrCorrupt, "The VTS IFO could not be read.");
        return ParseTitleDuration(ifo, vtsTtn);
    }

    internal static TimeSpan ParseTitleDuration(byte[] ifo, int vtsTtn)
    {
        void RequireRange(long start, long length)
        {
            if (start < 0 || length < 0 || start > ifo.Length || length > ifo.Length - start)
                throw new DiscScanException(DiscFailureKind.StructureUnsupported, "The VTS IFO table is invalid.");
        }
        uint Be32(long offset)
        {
            RequireRange(offset, 4);
            return BinaryPrimitives.ReadUInt32BigEndian(ifo.AsSpan((int)offset, 4));
        }
        ushort Be16(long offset)
        {
            RequireRange(offset, 2);
            return BinaryPrimitives.ReadUInt16BigEndian(ifo.AsSpan((int)offset, 2));
        }

        var pttSrpt = (long)Be32(0xC8) * SectorBytes;
        var pgcit = (long)Be32(0xCC) * SectorBytes;
        var titleCount = Be16(pttSrpt);
        if (pttSrpt == 0 || pgcit == 0 || vtsTtn < 1 || vtsTtn > titleCount)
            throw new DiscScanException(DiscFailureKind.StructureUnsupported, "The DVD title pointers are invalid.");
        var tableEnd = Be32(pttSrpt + 4) + 1L;
        RequireRange(pttSrpt, tableEnd);
        var start = Be32(pttSrpt + 8 + 4L * (vtsTtn - 1));
        var end = vtsTtn < titleCount ? Be32(pttSrpt + 8 + 4L * vtsTtn) : tableEnd;
        // 在盘 TTU 没有 nr_of_ptts 字段；条目数由相邻 TTU 偏移之差得出。
        // 把第一个 pgcn 当作条目数会串读到其他标题，并把 24 秒误报成 34 秒。
        if (start < 8 + titleCount * 4 || end <= start || end > tableEnd || (end - start) % 4 != 0)
            throw new DiscScanException(DiscFailureKind.StructureUnsupported, "The DVD chapter table is invalid.");
        var pgcCount = Be16(pgcit);
        RequireRange(pgcit, 8 + 8L * pgcCount);
        var seenPgcs = new HashSet<ushort>();
        var total90k = 0L;
        for (var entry = (long)start; entry < end; entry += 4)
        {
            var pgcn = Be16(pttSrpt + entry);
            if (pgcn < 1 || pgcn > pgcCount)
                throw new DiscScanException(DiscFailureKind.StructureUnsupported, "The DVD chapter references an invalid PGC.");
            if (!seenPgcs.Add(pgcn))
                continue;
            var pgc = pgcit + Be32(pgcit + 8 + 8L * (pgcn - 1) + 4);
            RequireRange(pgc, 236);
            var cellCount = ifo[pgc + 3];
            var cellOffset = Be16(pgc + 0xE8);
            if (cellCount == 0 || cellOffset < 236)
                throw new DiscScanException(DiscFailureKind.StructureUnsupported, "The DVD cell table is invalid.");
            var cells = pgc + cellOffset;
            RequireRange(cells, cellCount * 24L);
            // 真实 DVD 的 PGC 汇总时长可能损坏（例如秒字段不是有效 BCD），
            // 而各 Cell 仍能正常播放。与 libdvdnav 一样按 Cell 累计，避免误报标题时长。
            for (var cellIndex = 0; cellIndex < cellCount; cellIndex++)
            {
                var cell = cells + cellIndex * 24L;
                var flags = ifo[cell];
                // 多角度块只累计默认的第一个角度，不能把替代角度重复计入时长。
                if ((flags & 0x30) == 0x10 && (flags & 0xC0) != 0x40)
                    continue;
                total90k += ConvertDvdTimeTo90k(ifo[cell + 4], ifo[cell + 5], ifo[cell + 6], ifo[cell + 7]);
            }
        }
        return TimeSpan.FromSeconds(total90k / 90000.0);
    }

    // 与 libdvdnav 的 dvdnav_convert_time 相同的换算：BCD 时分秒 + 帧率位。
    internal static long ConvertDvdTimeTo90k(byte hour, byte minute, byte second, byte frameU)
    {
        var result = Bcd(hour) * 3600L * 90000
            + Bcd(minute) * 60L * 90000
            + Bcd(second) * 90000;
        var frames = ((frameU & 0x30) >> 4) * 10 + (frameU & 0x0f);
        // libdvdnav：frame_u 高位表示 30fps 计时，否则按 25fps。
        result += (frameU & 0x80) != 0 ? frames * 3000 : frames * 3600;
        return result;
    }

    private static long Bcd(byte value) => (value >> 4) * 10 + (value & 0x0f);

    /// <summary>读取标题开头的 VOB 包，仅在 PES 明确标记加扰时报告 CSS 保护。</summary>
    public void ProbeTitleVobReadable(int titleSetNumber)
    {
        var file = FF.DVDOpenFile(handle, titleSetNumber, FF.DVD_READ_TITLE_VOBS);
        if (file == 0)
            throw new DiscScanException(DiscFailureKind.NotDiscOrCorrupt, "The DVD title VOB could not be opened.");
        try
        {
            var blockCount = Math.Min(64, (long)FF.DVDFileSize(file));
            if (blockCount <= 0)
                throw new DiscScanException(DiscFailureKind.NotDiscOrCorrupt, "The DVD title VOB is empty.");
            var buffer = new byte[(int)blockCount * SectorBytes];
            nint blocks;
            fixed (byte* pointer = buffer)
                blocks = FF.DVDReadBlocks(file, 0, (nuint)blockCount, pointer);
            if (blocks <= 0)
                throw new DiscScanException(DiscFailureKind.NotDiscOrCorrupt, "The DVD title VOB could not be read.");
            if (HasScrambledPes(buffer.AsSpan(0, (int)blocks * SectorBytes)))
                throw new DiscScanException(DiscFailureKind.ProtectedDisc, "The DVD CSS protection could not be decoded by the current runtime.");
        }
        finally
        {
            FF.DVDCloseFile(file);
        }
    }

    internal static bool HasScrambledPes(ReadOnlySpan<byte> bytes)
    {
        // DVD 的 pack 从扇区边界开始。按 pack/PES 长度前进，避免把载荷中的
        // 00 00 01 偶然字节序列误判成加密头。
        for (var sector = 0; sector + 14 <= bytes.Length; sector += SectorBytes)
        {
            if (bytes[sector] != 0 || bytes[sector + 1] != 0 || bytes[sector + 2] != 1 ||
                bytes[sector + 3] != 0xBA || (bytes[sector + 4] & 0xC0) != 0x40)
                continue;
            var offset = sector + 14 + (bytes[sector + 13] & 7);
            var end = Math.Min(sector + SectorBytes, bytes.Length);
            while (offset + 7 <= end && bytes[offset] == 0 && bytes[offset + 1] == 0 && bytes[offset + 2] == 1)
            {
                var id = bytes[offset + 3];
                if ((id == 0xBD || id is >= 0xC0 and <= 0xEF) &&
                    (bytes[offset + 6] & 0xC0) == 0x80 && (bytes[offset + 6] & 0x30) != 0)
                    return true;
                var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 4, 2));
                if (length == 0)
                    break;
                offset += 6 + length;
            }
        }
        return false;
    }

    public void Dispose()
    {
        if (handle != 0)
            FF.DVDClose(handle);
        handle = 0;
    }
}
