using TSCutter.GUI.FFmpeg;
using System;
using System.IO;
using System.Linq;
using System.Text;
using FFmpeg.AutoGen.Abstractions;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Collections.Generic;
using TSCutter.GUI.Models;

namespace TSCutter.GUI.Utils;

public static class MediaInfoBuilder
{
    public static string Build(string filePath)
    {
        FormatContext input;
        try { input = FormatContext.OpenInputUrl(filePath); }
        catch (FFmpegException exception) when (exception.ErrorCode == FF.AVERROR_INVALIDDATA)
        {
            // 自动识别失败后才检查损坏前缀中的 TS，避免覆盖已识别的普通容器。
            var candidate = TsMediaInfoProbe.Read(filePath);
            if (candidate is { IsEncrypted: true, Streams.Count: > 0 })
                return BuildEncryptedTs(filePath, candidate);
            throw;
        }
        using var fc = input;
        TsMediaInfoProbe? ts = null;
        // 任意容器仍由 FFmpeg 识别。只有确认 TS 后才补充读取传输层信息。
        if (fc.InputFormat?.Name == "mpegts")
        {
            ts = TsMediaInfoProbe.Read(filePath);
            if (ts is { IsEncrypted: true, Streams.Count: > 0 })
                return BuildEncryptedTs(filePath, ts);
        }
        fc.LoadStreamInfo();

        var sb = new StringBuilder();
        var fi = new FileInfo(filePath);

        // ==================== General ====================
        AppendSection(sb, "General");
        var firstStream = fc.Streams.FirstOrDefault();
        if (ts?.TransportStreamId is int transportId)
            AppendField(sb, "ID", FormatId(transportId));
        AppendField(sb, "Complete name", filePath);
        AppendField(sb, "Format", ts is not null ? "MPEG-TS" : fc.InputFormat?.Name ?? "Unknown");
        AppendField(sb, "File size", ToSize(fi.Length));
        if (ts is not null) AppendValues(sb, "Network name", ts.NetworkNames);
        AppendField(sb, "Duration", FormatDuration(fc.Duration));
        if (fc.BitRate > 0)
        {
            AppendField(sb, "Overall bit rate mode", IsCbr(fc) ? "Constant" : "Variable");
            AppendField(sb, "Overall bit rate", FormatDecimal(fc.BitRate / 1_000_000.0) + " Mb/s");
        }
        if (ValidRate(firstStream.AvgFrameRate))
            AppendField(sb, "Frame rate", firstStream.AvgFrameRate.ToDouble().ToString("F3") + " FPS");
        sb.AppendLine();

        // ==================== Streams ====================
        foreach (var stream in fc.Streams)
        {
            var cp = stream.Codecpar!;
            switch (cp.CodecType)
            {
                case AVMediaType.AVMEDIA_TYPE_VIDEO: WriteVideo(sb, stream, cp, fc, fi, ts); break;
                case AVMediaType.AVMEDIA_TYPE_AUDIO: WriteAudio(sb, stream, cp, fc, fi, ts); break;
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
                AppendField(sb, "Menu ID", FormatId(prog.Id));
                AppendField(sb, "Duration", FormatDuration(prog.EndTime - prog.StartTime));

                if (ts?.Programs.TryGetValue(prog.Id, out var programInfo) == true)
                {
                    if (programInfo.Names.Count > 0) AppendValues(sb, "Service name", programInfo.Names);
                    else if (prog.Metadata.TryGetValue("service_name", out var name)) AppendField(sb, "Service name", name);
                    if (programInfo.Providers.Count > 0) AppendValues(sb, "Service provider", programInfo.Providers);
                    else if (prog.Metadata.TryGetValue("service_provider", out var provider)) AppendField(sb, "Service provider", provider);
                    AppendValues(sb, "Service type", programInfo.ServiceTypes.Select(ServiceTypeName));
                }
                else
                {
                    if (prog.Metadata.TryGetValue("service_name", out var sn)) AppendField(sb, "Service name", sn);
                    if (prog.Metadata.TryGetValue("service_provider", out var sp)) AppendField(sb, "Service provider", sp);
                }
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    #region ----- Helpers -----
    internal static string BuildEncryptedTs(string path, TsMediaInfoProbe ts)
    {
        var sb = new StringBuilder();
        AppendSection(sb, "General");
        if (ts.TransportStreamId is int id) AppendField(sb, "ID", FormatId(id));
        AppendField(sb, "Complete name", path);
        AppendField(sb, "Format", "MPEG-TS");
        AppendField(sb, "File size", ToSize(new FileInfo(path).Length));
        AppendField(sb, "Duration", "Unknown");
        AppendField(sb, "Encryption", "Encrypted");
        AppendValues(sb, "Network name", ts.NetworkNames);
        foreach (var (pid, stream) in ts.Streams.OrderBy(item =>
            item.Value.ResolvedTypes.Any(TsStreamTypes.IsVideo) ? 0 :
            item.Value.ResolvedTypes.Any(type => TsStreamTypes.IsAudio(type)) ? 1 : 2).ThenBy(item => item.Key))
        {
            var video = stream.ResolvedTypes.Any(TsStreamTypes.IsVideo);
            var audio = stream.ResolvedTypes.Any(type => TsStreamTypes.IsAudio(type));
            sb.AppendLine();
            AppendSection(sb, video ? "Video" : audio ? "Audio" : "Data");
            AppendField(sb, "ID", FormatId(pid));
            foreach (var program in stream.Programs.Order()) AppendField(sb, "Menu ID", FormatId(program));
            AppendValues(sb, "Format", stream.ResolvedTypes.Select(TsFormatName));
            if (stream.Types.Contains(TsStreamTypes.H264))
                AppendField(sb, "Format/Info", "Advanced Video Codec");
            AppendValues(sb, "Format profile", stream.Profiles);
            AppendField(sb, "Codec ID", string.Join(" / ", stream.Types.Order()));
            if (stream.ResolvedTypes.Any(type => type is TsStreamTypes.Mpeg1Audio or TsStreamTypes.Mpeg2Audio))
                AppendField(sb, "Compression mode", "Lossy");
            AppendValues(sb, "Language", stream.Languages.Select(LanguageName), stream.LanguagesChanged);
            if (ts.ScrambledPids.Contains(pid)) AppendField(sb, "Encryption", "Encrypted");
        }
        foreach (var (number, program) in ts.Programs.OrderBy(item => item.Key))
        {
            if (program.PmtPid < 0) continue;
            sb.AppendLine();
            AppendSection(sb, "Menu");
            AppendField(sb, "ID", FormatId(program.PmtPid));
            AppendField(sb, "Menu ID", FormatId(number));
            AppendValues(sb, "Service name", program.Names);
            AppendValues(sb, "Service provider", program.Providers);
            AppendValues(sb, "Service type", program.ServiceTypes.Select(ServiceTypeName));
        }
        return sb.ToString().TrimEnd();
    }

    private static string TsFormatName(byte type) => type switch
    {
        TsStreamTypes.H264 => "AVC", TsStreamTypes.Hevc => "HEVC", TsStreamTypes.Vvc => "VVC",
        TsStreamTypes.Mpeg1Video => "MPEG Video", TsStreamTypes.Mpeg2Video => "MPEG Video",
        TsStreamTypes.Mpeg4Video => "MPEG-4 Visual",
        TsStreamTypes.Mpeg1Audio or TsStreamTypes.Mpeg2Audio => "MPEG Audio",
        TsStreamTypes.Aac or TsStreamTypes.AacLatm or TsStreamTypes.Mpeg4Audio => "AAC",
        TsStreamTypes.Ac3 => "AC-3", TsStreamTypes.Eac3 or TsStreamTypes.Eac3Atsc => "E-AC-3",
        TsStreamTypes.Dts => "DTS", TsStreamTypes.Cavs => "AVS", TsStreamTypes.Avs2 => "AVS2",
        TsStreamTypes.Avs3 => "AVS3", TsStreamTypes.Av3a => "AV3A",
        _ => $"TS stream type 0x{type:X2}"
    };
    private static string ServiceTypeName(byte type) => type switch
    {
        1 => "digital television", 2 => "digital radio", 0x19 => "HD digital television",
        _ => $"0x{type:X2}"
    };
    private static string LanguageName(string code) =>
        CultureInfo.GetCultures(CultureTypes.NeutralCultures).FirstOrDefault(c =>
            c.ThreeLetterISOLanguageName.Equals(code, StringComparison.OrdinalIgnoreCase))?.EnglishName ?? code;

    private static void AppendSection(StringBuilder sb, string title) => sb.AppendLine(title);
    private static void AppendField(StringBuilder sb, string key, string value)
        => sb.AppendLine(key.PadRight(35) + ": " + value);
    private static string FormatId(int id) => $"{id} (0x{id:X})";
    // Matroska 等解复用器未提供轨道 ID 时，各流的 id 均为 0。
    private static string StreamId(MediaStream stream) => FormatId(stream.Id > 0 ? stream.Id : stream.Index + 1);
    private static string FormatDecimal(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static bool ValidRate(AVRational rate) => rate.num > 0 && rate.den > 0;
    private static void AppendValues(StringBuilder sb, string key, IEnumerable<string> values, bool changesDetected = true)
    {
        var items = values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().Order(StringComparer.Ordinal).ToArray();
        if (items.Length > 0)
            AppendField(sb, key, string.Join(" / ", items) + (items.Length > 1 && changesDetected ? " (changes detected)" : ""));
    }
    private static void WriteStreamIds(StringBuilder sb, MediaStream stream, FormatContext fc)
    {
        AppendField(sb, "ID", StreamId(stream));
        foreach (var program in fc.Programs.Where(p => p.ContainsStream(stream.Index)))
            AppendField(sb, "Menu ID", FormatId(program.Id));
    }
    private static string CodecId(CodecParameters cp, int pid, TsMediaInfoProbe? ts) =>
        ts?.Streams.TryGetValue(pid, out var stream) == true
            ? string.Join(" / ", stream.Types.Order())
            : cp.IsAv3a ? "av3a" : ((int)cp.CodecId).ToString();
    #endregion

    #region ----- Video -----
    private static unsafe void WriteVideo(StringBuilder sb, MediaStream stream, CodecParameters cp, FormatContext fc, FileInfo fi, TsMediaInfoProbe? ts)
    {
        AppendSection(sb, "Video");

        WriteStreamIds(sb, stream, fc);

        string codec = cp.CodecName;
        AppendField(sb, "Format", codec);
        AppendField(sb, "Format/Info", CodecLongName(cp.CodecId));

        AVCodecParameters* raw = cp;
        var profile = Marshal.PtrToStringUTF8((IntPtr)FF.avcodec_profile_name(cp.CodecId, raw->profile));
        if (!string.IsNullOrEmpty(profile))
            AppendField(sb, "Format profile", cp.CodecId == AVCodecID.AV_CODEC_ID_H264 && raw->level > 0
                ? $"{profile}@L{raw->level / 10}.{raw->level % 10}" : profile);
        else if (ts?.Streams.TryGetValue(stream.Id, out var definition) == true)
            AppendValues(sb, "Format profile", definition.Profiles);

        AppendField(sb, "Codec ID", CodecId(cp, stream.Id, ts));
        AppendField(sb, "Duration", FormatDuration(stream));

        if (cp.BitRate > 0)
        {
            AppendField(sb, "Bit rate mode", IsCbr(cp) ? "Constant" : "Variable");
            AppendField(sb, "Bit rate", FormatDecimal(cp.BitRate / 1_000_000.0) + " Mb/s");
        }

        if (cp.Width > 0) AppendField(sb, "Width", cp.Width + " pixels");
        if (cp.Height > 0) AppendField(sb, "Height", cp.Height + " pixels");

        if (cp.Width > 0 && cp.Height > 0 && ValidRate(cp.SampleAspectRatio))
        {
            int num = cp.Width * cp.SampleAspectRatio.num;
            int den = cp.Height * cp.SampleAspectRatio.den;
            AvReduce(out var resultNum, out var resultDen, num, den, long.MaxValue);
            AppendField(sb, "Display aspect ratio", resultNum + ":" + resultDen);
        }

        if (ValidRate(stream.AvgFrameRate))
            AppendField(sb, "Frame rate", stream.AvgFrameRate.ToDouble().ToString("F3") + " FPS");
        if (cp.Format >= 0)
        {
            var pixelFormat = FF.av_pix_fmt_desc_get((AVPixelFormat)cp.Format);
            if (pixelFormat != null)
            {
                const ulong rgbFlag = 1UL << 5;
                AppendField(sb, "Color space", (pixelFormat->flags & rgbFlag) != 0 ? "RGB" :
                    pixelFormat->nb_components == 1 ? "Gray" : "YUV");
                if (pixelFormat->nb_components >= 3 && (pixelFormat->flags & rgbFlag) == 0)
                    AppendField(sb, "Chroma subsampling", (pixelFormat->log2_chroma_w, pixelFormat->log2_chroma_h) switch
                    {
                        (1, 1) => "4:2:0", (1, 0) => "4:2:2", (0, 0) => "4:4:4", _ => "Unknown"
                    });
                var depth = GetBitDepth(cp);
                if (depth > 0) AppendField(sb, "Bit depth", depth + " bits");
            }
        }

        if (cp.ColorRange != AVColorRange.AVCOL_RANGE_UNSPECIFIED)
            AppendField(sb, "Color range", cp.ColorRange == AVColorRange.AVCOL_RANGE_JPEG ? "Full" : "Limited");
        if (cp.ColorPrimaries != AVColorPrimaries.AVCOL_PRI_UNSPECIFIED)
            AppendField(sb, "Color primaries", cp.ColorPrimaries.ToString().Replace("AVCOL_PRI_", "").Replace("bt2020", "BT.2020"));
        if (cp.ColorTrc != AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED)
            AppendField(sb, "Transfer characteristics", cp.ColorTrc.ToString().Replace("AVCOL_TRC_", "").ToUpper());
        if (cp.ColorSpace != AVColorSpace.AVCOL_SPC_UNSPECIFIED)
            AppendField(sb, "Matrix coefficients", cp.ColorSpace.ToString().Replace("AVCOL_SPC_", "").Replace("bt2020nc", "BT.2020 non-constant"));

        if (cp.BitRate > 0 && cp.Width > 0 && cp.Height > 0 && ValidRate(stream.AvgFrameRate))
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
    private static unsafe void WriteAudio(StringBuilder sb, MediaStream stream, CodecParameters cp, FormatContext fc, FileInfo fi, TsMediaInfoProbe? ts)
    {
        AppendSection(sb, "Audio");

        WriteStreamIds(sb, stream, fc);

        string codec = cp.CodecName;
        AppendField(sb, "Format", codec);
        AppendField(sb, "Format/Info", cp.IsAv3a ? "Audio Vivid" : CodecLongName(cp.CodecId));
        if (codec == "AC3") AppendField(sb, "Commercial name", "Dolby Digital");
        AppendField(sb, "Codec ID", CodecId(cp, stream.Id, ts));

        AppendField(sb, "Duration", FormatDuration(stream));

        if (cp.BitRate > 0)
        {
            AppendField(sb, "Bit rate mode", IsCbr(cp) ? "Constant" : "Variable");
            AppendField(sb, "Bit rate", FormatDecimal(cp.BitRate / 1000.0) + " kb/s");
        }

        if (cp.ChLayout.nb_channels > 0)
        {
            AppendField(sb, "Channel(s)", cp.ChLayout.nb_channels + " channels");
            AppendField(sb, "Channel layout", GetChannelLayoutDescription(cp.ChLayout));
        }

        if (cp.SampleRate > 0)
            AppendField(sb, "Sampling rate", FormatDecimal(cp.SampleRate / 1000.0) + " kHz");

        if (cp.HasReliableCodecParameters && ValidRate(stream.AvgFrameRate))
            AppendField(sb, "Frame rate", stream.AvgFrameRate.ToDouble().ToString("F3") + " FPS");

        AppendField(sb, "Compression mode", "Lossy");

        var firstVideo = fc.Streams.FirstOrDefault(x => x.Codecpar!.CodecType == AVMediaType.AVMEDIA_TYPE_VIDEO);
        if (firstVideo is {} && stream.StartTime != FF.AV_NOPTS_VALUE && firstVideo.StartTime != FF.AV_NOPTS_VALUE)
        {
            var delay = (long)Math.Round((stream.StartTime * stream.TimeBase.ToDouble() -
                firstVideo.StartTime * firstVideo.TimeBase.ToDouble()) * 1000);
            if (delay != 0)
                AppendField(sb, "Delay relative to video", delay + " ms");
        }

        long streamSize = StreamSize(fc, stream);
        if (streamSize > 0)
            AppendField(sb, "Stream size", ToSize(streamSize) + " (" + (100.0 * streamSize / fi.Length).ToString("F0") + "%)");

        if (ts?.Streams.TryGetValue(stream.Id, out var tsStream) == true && tsStream.Languages.Count > 0)
            AppendValues(sb, "Language", tsStream.Languages.Select(LanguageName), tsStream.LanguagesChanged);
        else if (stream.Metadata.TryGetValue("language", out var lang))
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
        AppendField(sb, "ID", StreamId(stream));
        AppendField(sb, "Format", cp.CodecName);
        if (stream.Metadata.TryGetValue("language", out var lang))
            AppendField(sb, "Language", lang);
    }
    #endregion

    #region ----- Generic -----
    private static void WriteGeneric(StringBuilder sb, MediaStream stream, CodecParameters cp)
    {
        AppendSection(sb, cp.CodecType.ToString().Replace("AVMEDIA_TYPE_", ""));
        AppendField(sb, "ID", StreamId(stream));
        AppendField(sb, "Codec", cp.CodecName);
    }
    #endregion

    #region ----- Utility -----
    private static string FormatDuration(long avTs)
    {
        if (avTs <= 0) return "Unknown";
        var ts = TimeSpan.FromSeconds(avTs / 1_000_000.0);
        return ts.TotalHours >= 1
            ? ((int)ts.TotalHours).ToString("D2") + " h " + ts.Minutes.ToString("D2") + " min " + ts.Seconds.ToString("D2") + " s"
            : ((int)ts.TotalMinutes) + " min " + ts.Seconds.ToString("D2") + " s";
    }

    private static unsafe string FormatDuration(AVStream* stream)
    {
        if (stream->duration <= 0) return "Unknown";
    
        // 按该流的时间基转换为秒。
        var seconds = stream->duration * FF.av_q2d(stream->time_base);
        var ts = TimeSpan.FromSeconds(seconds);

        return ts.TotalHours >= 1
            ? ((int)ts.TotalHours).ToString("D2") + " h " + ts.Minutes.ToString("D2") + " min " + ts.Seconds.ToString("D2") + " s"
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
            return (long)(stream.Codecpar.BitRate * (stream.Duration * stream.TimeBase.ToDouble()) / 8);
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
