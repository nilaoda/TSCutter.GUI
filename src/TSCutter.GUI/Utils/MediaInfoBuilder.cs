using TSCutter.GUI.FFmpeg;
using System;
using System.IO;
using System.Linq;
using System.Text;
using FFmpeg.AutoGen.Abstractions;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;
using System.Runtime.InteropServices;

namespace TSCutter.GUI.Utils;

public static class MediaInfoBuilder
{
    public static string Build(string filePath)
    {
        using var fc = FormatContext.OpenInputUrl(filePath);
        fc.LoadStreamInfo();

        var sb = new StringBuilder();
        var fi = new FileInfo(filePath);

        // ==================== General ====================
        AppendSection(sb, "General");
        var firstStream = fc.Streams.FirstOrDefault();
        AppendField(sb, "ID", firstStream.Index + 1 + " (0x" + (firstStream.Index + 1).ToString("X") + ")");
        AppendField(sb, "Complete name", filePath);
        AppendField(sb, "Format", fc.InputFormat?.Name ?? "Unknown");
        AppendField(sb, "File size", ToSize(fi.Length));
        AppendField(sb, "Duration", FormatDuration(fc.Duration));
        if (fc.BitRate > 0)
        {
            AppendField(sb, "Overall bit rate mode", IsCbr(fc) ? "Constant" : "Variable");
            AppendField(sb, "Overall bit rate", (fc.BitRate / 1_000_000.0).ToString("0.00") + " Mb/s");
        }
        if (firstStream.AvgFrameRate.num > 0)
            AppendField(sb, "Frame rate", firstStream.AvgFrameRate.ToDouble().ToString("F3") + " FPS");
        sb.AppendLine();

        // ==================== Streams ====================
        foreach (var stream in fc.Streams)
        {
            var cp = stream.Codecpar!;
            switch (cp.CodecType)
            {
                case AVMediaType.AVMEDIA_TYPE_VIDEO: WriteVideo(sb, stream, cp, fc, fi); break;
                case AVMediaType.AVMEDIA_TYPE_AUDIO: WriteAudio(sb, stream, cp, fc, fi); break;
                case AVMediaType.AVMEDIA_TYPE_SUBTITLE: WriteSubtitle(sb, stream, cp); break;
                default: WriteGeneric(sb, stream, cp); break;
            }
            sb.AppendLine();
        }

        // ==================== Menu (Program) ====================
        if (fc.Programs is { Count: > 0 })
        {
            AppendSection(sb, "Menu");
            foreach (var prog in fc.Programs)
            {
                AppendField(sb, "ID", prog.PmtPid + " (0x" + prog.PmtPid.ToString("X") + ")");
                AppendField(sb, "Menu ID", "1 (0x1)");
                AppendField(sb, "Duration", FormatDuration(prog.EndTime - prog.StartTime));

                if (prog.Metadata.TryGetValue("service_name", out var sn))
                    AppendField(sb, "Service name", sn);
                if (prog.Metadata.TryGetValue("service_provider", out var sp))
                    AppendField(sb, "Service provider", sp);
                AppendField(sb, "Service type", "digital television");
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    #region ----- Helpers -----
    private static void AppendSection(StringBuilder sb, string title) => sb.AppendLine(title);
    private static void AppendField(StringBuilder sb, string key, string value)
        => sb.AppendLine(key.PadRight(35) + ": " + value);
    #endregion

    #region ----- Video -----
    private static unsafe void WriteVideo(StringBuilder sb, MediaStream stream, CodecParameters cp, FormatContext fc, FileInfo fi)
    {
        AppendSection(sb, "Video");

        AppendField(sb, "ID", stream.Index + 256 + " (0x" + (stream.Index + 256).ToString("X") + ")");
        AppendField(sb, "Menu ID", "1 (0x1)");

        string codec = cp.CodecName;
        AppendField(sb, "Format", codec);
        AppendField(sb, "Format/Info", CodecLongName(cp.CodecId));

        var descriptor = FF.avcodec_descriptor_get(cp.CodecId);
        if (descriptor != null && !string.IsNullOrEmpty(Marshal.PtrToStringAnsi((IntPtr)descriptor->long_name)))
            AppendField(sb, "Format profile", Marshal.PtrToStringAnsi((IntPtr)descriptor->long_name)!);

        AppendField(sb, "Codec ID", ((int)cp.CodecId).ToString());
        AppendField(sb, "Duration", FormatDuration(stream));

        if (cp.BitRate > 0)
        {
            AppendField(sb, "Bit rate mode", IsCbr(cp) ? "Constant" : "Variable");
            AppendField(sb, "Bit rate", cp.BitRate / 1_000_000.0 + " Mb/s");
        }

        AppendField(sb, "Width", cp.Width + " pixels");
        AppendField(sb, "Height", cp.Height + " pixels");

        if (cp.SampleAspectRatio.num != 0 && cp.SampleAspectRatio.den != 0)
        {
            int num = cp.Width * cp.SampleAspectRatio.num;
            int den = cp.Height * cp.SampleAspectRatio.den;
            AvReduce(out var resultNum, out var resultDen, num, den, long.MaxValue);
            AppendField(sb, "Display aspect ratio", resultNum + ":" + resultDen);
        }

        AppendField(sb, "Frame rate", stream.AvgFrameRate.ToDouble().ToString("F3") + " FPS");

        AppendField(sb, "Color space", cp.ColorSpace.ToString().Replace("AVCOL_SPC_", ""));
        AppendField(sb, "Chroma subsampling", ChromaLocation(cp.ChromaLocation));
        AppendField(sb, "Bit depth", GetBitDepth(cp) + " bits");

        if (cp.ColorRange != AVColorRange.AVCOL_RANGE_UNSPECIFIED)
            AppendField(sb, "Color range", cp.ColorRange == AVColorRange.AVCOL_RANGE_JPEG ? "Full" : "Limited");
        if (cp.ColorPrimaries != AVColorPrimaries.AVCOL_PRI_UNSPECIFIED)
            AppendField(sb, "Color primaries", cp.ColorPrimaries.ToString().Replace("AVCOL_PRI_", "").Replace("bt2020", "BT.2020"));
        if (cp.ColorTrc != AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED)
            AppendField(sb, "Transfer characteristics", cp.ColorTrc.ToString().Replace("AVCOL_TRC_", "").ToUpper());
        if (cp.ColorSpace != AVColorSpace.AVCOL_SPC_UNSPECIFIED)
            AppendField(sb, "Matrix coefficients", cp.ColorSpace.ToString().Replace("AVCOL_SPC_", "").Replace("bt2020nc", "BT.2020 non-constant"));

        if (cp.BitRate > 0 && cp.Width > 0 && cp.Height > 0 && stream.AvgFrameRate.num > 0)
        {
            double fps = stream.AvgFrameRate.ToDouble();
            double bppf = cp.BitRate / (cp.Width * cp.Height * fps);
            AppendField(sb, "Bits/(Pixel*Frame)", bppf.ToString("F3"));
        }

        long streamSize = StreamSize(fc, stream);
        if (streamSize > 0)
            AppendField(sb, "Stream size", ToSize(streamSize) + " (" + (100.0 * streamSize / fi.Length).ToString("F0") + "%)");

        if (stream.Codecpar!.FieldOrder != AVFieldOrder.AV_FIELD_PROGRESSIVE && stream.Codecpar.FieldOrder != AVFieldOrder.AV_FIELD_UNKNOWN)
            AppendField(sb, "Scan type", stream.Codecpar.FieldOrder.ToString().Replace("AV_FIELD_", ""));

        foreach (var kv in stream.Metadata)
            if (kv.Key != "language" && kv.Key != "title")
                AppendField(sb, kv.Key, kv.Value);
    }
    #endregion

    #region ----- Audio -----
    private static unsafe void WriteAudio(StringBuilder sb, MediaStream stream, CodecParameters cp, FormatContext fc, FileInfo fi)
    {
        AppendSection(sb, "Audio");

        AppendField(sb, "ID", stream.Index + 256 + " (0x" + (stream.Index + 256).ToString("X") + ")");
        AppendField(sb, "Menu ID", "1 (0x1)");

        string codec = cp.CodecName;
        AppendField(sb, "Format", codec);
        AppendField(sb, "Format/Info", cp.IsAv3a ? "Audio Vivid" : CodecLongName(cp.CodecId));
        if (codec == "AC3") AppendField(sb, "Commercial name", "Dolby Digital");
        AppendField(sb, "Codec ID", cp.IsAv3a ? "av3a" : ((int)cp.CodecId).ToString());

        AppendField(sb, "Duration", FormatDuration(stream));

        if (cp.BitRate > 0)
        {
            AppendField(sb, "Bit rate mode", IsCbr(cp) ? "Constant" : "Variable");
            AppendField(sb, "Bit rate", cp.BitRate / 1000.0 + " kb/s");
        }

        if (cp.ChLayout.nb_channels > 0)
        {
            AppendField(sb, "Channel(s)", cp.ChLayout.nb_channels + " channels");
            AppendField(sb, "Channel layout", GetChannelLayoutDescription(cp.ChLayout));
        }

        if (cp.SampleRate > 0)
            AppendField(sb, "Sampling rate", cp.SampleRate / 1000.0 + " kHz");

        if (cp.HasReliableCodecParameters && stream.AvgFrameRate.num > 0)
            AppendField(sb, "Frame rate", stream.AvgFrameRate.ToDouble().ToString("F3") + " FPS (1536 SPF)");

        AppendField(sb, "Compression mode", "Lossy");

        var firstVideo = fc.Streams.FirstOrDefault(x => x.Codecpar!.CodecType == AVMediaType.AVMEDIA_TYPE_VIDEO);
        if (firstVideo is {} && stream.StartTime != FF.AV_NOPTS_VALUE && firstVideo.StartTime != FF.AV_NOPTS_VALUE)
        {
            long delay = (stream.StartTime - firstVideo.StartTime) * 1000 / FF.AV_TIME_BASE;
            if (delay != 0)
                AppendField(sb, "Delay relative to video", delay + " ms");
        }

        long streamSize = StreamSize(fc, stream);
        if (streamSize > 0)
            AppendField(sb, "Stream size", ToSize(streamSize) + " (" + (100.0 * streamSize / fi.Length).ToString("F0") + "%)");

        if (stream.Metadata.TryGetValue("language", out var lang))
            AppendField(sb, "Language", lang);

        if (codec == "AC3")
        {
            if (stream.Metadata.TryGetValue("service_type", out var st))
                AppendField(sb, "Service kind", st);
            foreach (var kv in stream.Metadata)
                if (kv.Key.StartsWith("dialnorm") || kv.Key.Contains("cmix") || kv.Key.Contains("surmix") || kv.Key.Contains("compr"))
                    AppendField(sb, kv.Key, kv.Value);
        }

        foreach (var kv in stream.Metadata)
            if (kv.Key != "language" && kv.Key != "title" && kv.Key != "service_type")
                AppendField(sb, kv.Key, kv.Value);
    }
    #endregion

    #region ----- Subtitle -----
    private static void WriteSubtitle(StringBuilder sb, MediaStream stream, CodecParameters cp)
    {
        AppendSection(sb, "Subtitle");
        AppendField(sb, "ID", stream.Index + 256 + " (0x" + (stream.Index + 256).ToString("X") + ")");
        AppendField(sb, "Format", cp.CodecName);
        if (stream.Metadata.TryGetValue("language", out var lang))
            AppendField(sb, "Language", lang);
    }
    #endregion

    #region ----- Generic -----
    private static void WriteGeneric(StringBuilder sb, MediaStream stream, CodecParameters cp)
    {
        AppendSection(sb, cp.CodecType.ToString().Replace("AVMEDIA_TYPE_", ""));
        AppendField(sb, "ID", stream.Index + 256 + " (0x" + (stream.Index + 256).ToString("X") + ")");
        AppendField(sb, "Codec", cp.CodecName);
    }
    #endregion

    #region ----- Utility -----
    private static string FormatDuration(long avTs)
    {
        if (avTs <= 0) return "Unknown";
        var ts = TimeSpan.FromSeconds(avTs / 1_000_000.0);
        return ts.Hours > 0
            ? ts.Hours.ToString("D2") + " h " + ts.Minutes.ToString("D2") + " min " + ts.Seconds.ToString("D2") + " s"
            : ((int)ts.TotalMinutes) + " min " + ts.Seconds.ToString("D2") + " s";
    }

    private static unsafe string FormatDuration(AVStream* stream)
    {
        if (stream->duration <= 0) return "Unknown";
    
        // 将微秒转换为秒
        var seconds = stream->duration * FF.av_q2d(stream->time_base);
        var ts = TimeSpan.FromSeconds(seconds);

        return ts.Hours > 0
            ? ts.Hours.ToString("D2") + " h " + ts.Minutes.ToString("D2") + " min " + ts.Seconds.ToString("D2") + " s"
            : ((int)ts.TotalMinutes) + " min " + ts.Seconds.ToString("D2") + " s";
    }

    private static int AvReduce(out int dstNum, out int dstDen, long num, long den, long max)
    {
        unsafe
        {
            fixed (int* pDstNum = &dstNum, pDstDen = &dstDen)
            {
                return FF.av_reduce(pDstNum, pDstDen, num, den, max);
            }
        }
    }

    private static unsafe string GetChannelLayoutDescription(AVChannelLayout chLayout)
    {
        const int bufSize = 256;
        byte[] buf = new byte[bufSize];
        fixed (byte* pBuf = buf)
        {
            FF.av_channel_layout_describe(&chLayout, pBuf, (ulong)bufSize);
        }
        int len = Array.IndexOf(buf, (byte)0);
        return Encoding.UTF8.GetString(buf, 0, len > 0 ? len : bufSize - 1);
    }

    private static unsafe int GetBitDepth(CodecParameters cp)
    {
        if (cp.CodecType == AVMediaType.AVMEDIA_TYPE_VIDEO)
        {
            var desc = FF.av_pix_fmt_desc_get((AVPixelFormat)cp.Format);
            if (desc != null)
                return desc->comp[0].depth;
        }
        else if (cp.CodecType == AVMediaType.AVMEDIA_TYPE_AUDIO)
        {
            return FF.av_get_bits_per_sample(cp.CodecId);
        }
        return cp.BitsPerRawSample;
    }
    
    private static string ToSize(long bytes)
    {
        const double GiB = 1024 * 1024 * 1024;
        const double MiB = 1024 * 1024;
        return bytes >= GiB
            ? (bytes / GiB).ToString("F2") + " GiB"
            : (bytes / MiB).ToString("F1") + " MiB";
    }

    private static string ChromaLocation(AVChromaLocation loc) => loc switch
    {
        AVChromaLocation.AVCHROMA_LOC_LEFT or AVChromaLocation.AVCHROMA_LOC_CENTER or AVChromaLocation.AVCHROMA_LOC_TOPLEFT => "4:2:0",
        _ => loc.ToString().Replace("AVCHROMA_LOC_", "")
    };

    private static string CodecLongName(AVCodecID id) => id switch
    {
        AVCodecID.AV_CODEC_ID_HEVC => "High Efficiency Video Coding",
        AVCodecID.AV_CODEC_ID_H264 => "Advanced Video Codec",
        AVCodecID.AV_CODEC_ID_AC3 => "Audio Coding 3",
        AVCodecID.AV_CODEC_ID_EAC3 => "Enhanced AC-3",
        AVCodecID.AV_CODEC_ID_AAC => "Advanced Audio Codec",
        _ => CodecNames.Get(id)
    };

    private static long StreamSize(FormatContext fc, MediaStream stream)
    {
        if (stream.Codecpar!.BitRate > 0 && stream.Duration > 0)
            return (stream.Codecpar.BitRate * stream.Duration) / (8 * 1_000_000);
        return 0;
    }

    private static bool IsCbr(FormatContext fc)
    {
        var rates = fc.Streams.Select(s => s.Codecpar!.BitRate).Where(r => r > 0).Distinct().ToList();
        return rates.Count == 1;
    }

    private static bool IsCbr(CodecParameters cp) => cp.BitRate > 0;
    #endregion
}
