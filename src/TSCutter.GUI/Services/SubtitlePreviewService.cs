using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models;
using TSCutter.GUI.Utils;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Services;

internal sealed unsafe class SubtitlePreviewService : IDisposable
{
    private const long MaximumCacheBytes = 128L * 1024 * 1024;
    private const int MaximumEvents = 200_000;
    private const int MaximumBitmapPixels = 16 * 1024 * 1024;
    private readonly object sync = new();
    private readonly List<BitmapCue> bitmapCues = [];
    private bool useDefaultTextStyle;
    private readonly PixelSize codedSize;
    private readonly double originSeconds;
    private nint library, renderer, track;
    private long cacheBytes;
    private int eventCount;
    private bool disposed;
    private readonly List<(long Start, byte[] Text)> pendingText = [];

    private sealed record BitmapPart(int X, int Y, int Width, int Height, byte[] Data);
    private sealed record BitmapCue(long Start, long End, PixelSize Canvas, IReadOnlyList<BitmapPart> Parts);

    private SubtitlePreviewService(PixelSize codedSize, double originSeconds)
    {
        this.codedSize = codedSize;
        this.originSeconds = originSeconds;
    }

    public static SubtitlePreviewService Load(string path, PreviewStreamSelection selection,
        PixelSize codedSize, double originSeconds, double durationSeconds, long tsSyncOffset,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (codedSize.Width <= 0 || codedSize.Height <= 0 || !double.IsFinite(originSeconds) ||
            !double.IsFinite(durationSeconds) || durationSeconds < 0)
            throw new InvalidDataException(LocalizationManager.Instance.String_SubtitlePreview_Invalid);
        var service = new SubtitlePreviewService(codedSize, originSeconds);
        try
        {
            service.Read(path, selection, durationSeconds, tsSyncOffset, cancellationToken);
            return service;
        }
        catch
        {
            service.Dispose();
            throw;
        }
    }

    private void Read(string path, PreviewStreamSelection selection, double durationSeconds,
        long tsSyncOffset, CancellationToken cancellationToken)
    {
        var deadline = new MediaReadDeadline(TimeSpan.FromSeconds(30), cancellationToken);
        using var input = OpenInput();
        FormatContext OpenInput()
        {
            try { return InterruptibleInputFormatContext.Open(path, () => deadline.ShouldInterrupt, tsSyncOffset); }
            catch { deadline.ThrowIfInterrupted(); throw; }
        }
        try
        {
            input.LoadStreamInfo();
            deadline.ThrowIfInterrupted();
            var candidates = input.Streams.Where(stream => stream.Id == selection.StreamId &&
                stream.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_SUBTITLE &&
                (input.InputFormat?.Name == "mpegts" || stream.Index == selection.StreamIndex) &&
                (selection.ProgramId is not { } id || input.Programs.Any(program =>
                    program.Id == id && program.ContainsStream(stream.Index)))).ToArray();
            if (candidates.Length != 1)
                throw new InvalidDataException(LocalizationManager.Instance.String_SubtitlePreview_TrackMissing);
            var stream = candidates[0];
            var codec = FF.avcodec_find_decoder(stream.Codecpar!.CodecId);
            if (codec == null)
                throw new NotSupportedException(string.Format(LocalizationManager.Instance.String_SubtitlePreview_Unsupported,
                    stream.Codecpar.CodecName));
            using var decoder = new CodecContext(Codec.FindDecoderById(stream.Codecpar.CodecId));
            decoder.FillParameters(stream.Codecpar);
            decoder.PacketTimeBase = stream.TimeBase;
            FFmpegException.Check(FF.avcodec_open2(decoder, null, null));
            AVCodecContext* rawDecoder = decoder;
            AVCodecParameters* parameters = stream.Codecpar;
            // 位图字幕无需 libass；文字字幕沿用 ASS 原始样式或解码器生成的标准样式。
            var descriptor = FF.avcodec_descriptor_get(stream.Codecpar.CodecId);
            var isBitmap = descriptor != null && (descriptor->props & FF.CodecPropBitmapSubtitle) != 0;
            if (!isBitmap)
            {
                library = FF.ass_library_init();
                if (library == 0) throw new OutOfMemoryException();
                LoadFonts(input, cancellationToken);
                renderer = FF.ass_renderer_init(library);
                track = FF.ass_new_track(library);
                if (renderer == 0 || track == 0) throw new OutOfMemoryException();
                var originalAss = stream.Codecpar.CodecId is AVCodecID.AV_CODEC_ID_ASS or AVCodecID.AV_CODEC_ID_SSA;
                var header = originalAss && parameters->extradata_size > 0 ? parameters->extradata : rawDecoder->subtitle_header;
                var headerSize = originalAss && parameters->extradata_size > 0 ? parameters->extradata_size : rawDecoder->subtitle_header_size;
                if (header == null || headerSize <= 0)
                    throw new InvalidDataException(LocalizationManager.Instance.String_SubtitlePreview_Invalid);
                var defaultFont = GetSystemSubtitleFontFamily();
                if (originalAss)
                    FF.ass_process_codec_private(track, header, headerSize);
                else
                {
                    // 普通文字字幕统一使用系统字族和清爽样式；ASS 原始样式不参与覆盖。
                    useDefaultTextStyle = true;
                    // libass 以升降部总高度解释字号，按字体度量换算为系统文字的 42px，避免实际显示偏小。
                    var fontSize = 42.0;
                    if (FontManager.Current.TryGetGlyphTypeface(new Typeface(defaultFont), out var font))
                    {
                        var metrics = font.Metrics;
                        if (metrics.DesignEmHeight > 0 && metrics.Descent > metrics.Ascent)
                            fontSize *= (metrics.Descent - metrics.Ascent) / (double)metrics.DesignEmHeight;
                    }
                    var adjustedHeader = ApplyDefaultTextStyle(new ReadOnlySpan<byte>(header, headerSize), defaultFont, fontSize);
                    fixed (byte* data = adjustedHeader)
                        FF.ass_process_codec_private(track, data, adjustedHeader.Length);
                    FF.ass_track_set_feature(track, FF.AssFeatureWrapUnicode, 1);
                }
                FF.ass_set_fonts(renderer, null, defaultFont, 1, null, 1);
            }
            // 独立扫描字幕，避免预览跳到长字幕中间时漏掉其开始事件。
            // 未选中的流由解复用器丢弃，不解码音视频。
            foreach (var other in input.Streams)
                if (other.Index != stream.Index) ((AVStream*)other)->discard = AVDiscard.AVDISCARD_ALL;
            using var packet = new Packet();
            while (true)
            {
                deadline = new MediaReadDeadline(TimeSpan.FromSeconds(30), cancellationToken);
                var result = FF.av_read_frame(input, packet);
                deadline.ThrowIfInterrupted();
                if (result == FF.AVERROR_EOF) break;
                FFmpegException.Check(result);
                try
                {
                    if (packet.StreamIndex == stream.Index)
                        Decode(decoder, packet, stream.TimeBase, isBitmap, cancellationToken);
                }
                finally { packet.Unref(); }
            }
            AVPacket empty = default;
            empty.pts = empty.dts = FF.AV_NOPTS_VALUE;
            Decode(decoder, &empty, stream.TimeBase, isBitmap, cancellationToken);
            FlushPending((long)Math.Ceiling((originSeconds + durationSeconds) * 1000));
            bitmapCues.Sort((a, b) => a.Start.CompareTo(b.Start));
            for (var i = 0; i + 1 < bitmapCues.Count; i++)
                bitmapCues[i] = bitmapCues[i] with { End = Math.Min(bitmapCues[i].End, bitmapCues[i + 1].Start) };
        }
        catch
        {
            deadline.ThrowIfInterrupted();
            throw;
        }
    }

    internal static string GetSystemSubtitleFontFamily()
    {
        var fonts = FontManager.Current.SystemFonts;
        // 使用平台字体提供器，避开应用自带的界面字体；中文默认字族也包含拉丁字符，减少混排时的度量差异。
        if (fonts.TryMatchCharacter('中', FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, null, CultureInfo.CurrentUICulture, out var typeface))
            return typeface.FontFamily.Name;
        return "sans-serif";
    }

    internal static byte[] ApplyDefaultTextStyle(ReadOnlySpan<byte> header, string family, double fontSize = 42)
    {
        var lines = Encoding.UTF8.GetString(header).Split('\n');
        double Resolution(string key, double fallback) => lines.Select(line => line.Trim())
            .Where(line => line.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
            .Select(line => double.TryParse(line[(key.Length + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                && double.IsFinite(value) && value > 0 ? value : fallback).FirstOrDefault(fallback);
        var width = Resolution("PlayResX", 384);
        var height = Resolution("PlayResY", 288);
        var style = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Fontname"] = family,
            ["Fontsize"] = (fontSize * height / 1080).ToString("0.####", CultureInfo.InvariantCulture),
            ["PrimaryColour"] = "&H00FFFFFF", ["Bold"] = "0", ["Italic"] = "0",
            ["BorderStyle"] = "1", ["Outline"] = "0", ["Shadow"] = "0", ["Alignment"] = "2",
            ["MarginL"] = (width * 0.05).ToString("0", CultureInfo.InvariantCulture),
            ["MarginR"] = (width * 0.05).ToString("0", CultureInfo.InvariantCulture),
            ["MarginV"] = (height * 0.055).ToString("0", CultureInfo.InvariantCulture)
        };
        var inStyles = false;
        string[] fields = [];
        var nameIndex = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('['))
            {
                inStyles = line.Equals("[V4+ Styles]", StringComparison.OrdinalIgnoreCase) ||
                           line.Equals("[V4 Styles]", StringComparison.OrdinalIgnoreCase);
                fields = [];
                nameIndex = -1;
            }
            if (!inStyles) continue;
            if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                fields = line[7..].Split(',');
                nameIndex = Array.FindIndex(fields, field => field.Trim().Equals("Name", StringComparison.OrdinalIgnoreCase));
            }
            else if (line.StartsWith("Style:", StringComparison.OrdinalIgnoreCase) && nameIndex >= 0)
            {
                var values = line[6..].Split(',');
                if (values.Length <= nameIndex ||
                    !values[nameIndex].Trim().Equals("Default", StringComparison.OrdinalIgnoreCase)) continue;
                for (var j = 0; j < Math.Min(fields.Length, values.Length); j++)
                    if (style.TryGetValue(fields[j].Trim(), out var value)) values[j] = value;
                lines[i] = "Style:" + string.Join(',', values) + (lines[i].EndsWith('\r') ? "\r" : "");
            }
        }
        return Encoding.UTF8.GetBytes(string.Join('\n', lines));
    }

    private void LoadFonts(FormatContext input, CancellationToken cancellationToken)
    {
        long fontBytes = 0;
        foreach (var stream in input.Streams)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stream.Codecpar?.CodecType != AVMediaType.AVMEDIA_TYPE_ATTACHMENT) continue;
            stream.Metadata.TryGetValue("filename", out var name);
            stream.Metadata.TryGetValue("mimetype", out var mime);
            if (!(mime.StartsWith("font/", StringComparison.OrdinalIgnoreCase) ||
                  mime.Contains("font", StringComparison.OrdinalIgnoreCase) ||
                  new[] { ".ttf", ".otf", ".ttc" }.Contains(Path.GetExtension(name).ToLowerInvariant()))) continue;
            AVCodecParameters* parameters = stream.Codecpar;
            if (parameters->extradata == null || parameters->extradata_size <= 0) continue;
            fontBytes += parameters->extradata_size;
            if (fontBytes > 64 * 1024 * 1024) throw CacheLimit();
            FF.ass_add_font(library, string.IsNullOrWhiteSpace(name) ? $"attachment-{stream.Index}" : name,
                parameters->extradata, parameters->extradata_size);
        }
    }

    private void Decode(CodecContext decoder, AVPacket* packet, AVRational timeBase,
        bool bitmap, CancellationToken cancellationToken)
    {
        var remaining = *packet;
        for (var attempt = 0; attempt < 64; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AVSubtitle subtitle = default;
            subtitle.pts = FF.AV_NOPTS_VALUE;
            var got = 0;
            int consumed;
            try
            {
                consumed = FF.avcodec_decode_subtitle2(decoder, &subtitle, &got, &remaining);
                FFmpegException.Check(consumed);
                if (got != 0)
                {
                    var pts = subtitle.pts != FF.AV_NOPTS_VALUE ? subtitle.pts / 1000.0 :
                        packet->pts != FF.AV_NOPTS_VALUE ? packet->pts * timeBase.ToDouble() * 1000 : double.NaN;
                    if (double.IsFinite(pts))
                    {
                        var start = (long)Math.Round(pts + subtitle.start_display_time);
                        var duration = subtitle.end_display_time > subtitle.start_display_time && subtitle.end_display_time != uint.MaxValue
                            ? subtitle.end_display_time - subtitle.start_display_time :
                            packet->duration > 0 ? packet->duration * timeBase.ToDouble() * 1000 - subtitle.start_display_time : 0;
                        if (++eventCount > MaximumEvents) throw CacheLimit();
                        if (bitmap)
                            AddBitmap(subtitle, start, duration > 0 ? start + (long)Math.Round(duration) : long.MaxValue,
                                decoder, cancellationToken);
                        else AddText(subtitle, start, Math.Max(0, (long)Math.Round(duration)));
                    }
                }
            }
            finally { FF.avsubtitle_free(&subtitle); }
            if (consumed <= 0 || consumed >= remaining.size) break;
            remaining.data += consumed;
            remaining.size -= consumed;
        }
    }

    private void AddText(AVSubtitle subtitle, long start, long duration)
    {
        FlushPending(start);
        for (var i = 0; i < subtitle.num_rects; i++)
        {
            var rectangle = subtitle.rects[i];
            if (rectangle->type != AVSubtitleType.SUBTITLE_ASS || rectangle->ass == null) continue;
            var bytes = Encoding.UTF8.GetBytes(Marshal.PtrToStringUTF8((nint)rectangle->ass) ?? "");
            cacheBytes += bytes.Length;
            if (cacheBytes > MaximumCacheBytes) throw CacheLimit();
            if (duration == 0) pendingText.Add((start, bytes));
            else ProcessText(bytes, start, duration);
        }
    }

    private void FlushPending(long end)
    {
        foreach (var item in pendingText)
            if (end > item.Start) ProcessText(item.Text, item.Start, end - item.Start);
        pendingText.Clear();
    }

    private void ProcessText(byte[] bytes, long start, long duration)
    {
        fixed (byte* data = bytes) FF.ass_process_chunk(track, data, bytes.Length, start, duration);
    }

    private void AddBitmap(AVSubtitle subtitle, long start, long end, CodecContext decoder, CancellationToken token)
    {
        AVCodecContext* raw = decoder;
        var canvas = raw->width > 0 && raw->height > 0 ? new PixelSize(raw->width, raw->height) : codedSize;
        var parts = new List<BitmapPart>();
        for (var i = 0; i < subtitle.num_rects; i++)
        {
            token.ThrowIfCancellationRequested();
            var rectangle = subtitle.rects[i];
            if (rectangle->type != AVSubtitleType.SUBTITLE_BITMAP || rectangle->w <= 0 || rectangle->h <= 0) continue;
            if ((long)rectangle->w * rectangle->h > MaximumBitmapPixels || rectangle->linesize[0] < rectangle->w ||
                rectangle->data[0] == null || rectangle->data[1] == null || rectangle->nb_colors is <= 0 or > 256)
                throw new InvalidDataException(LocalizationManager.Instance.String_SubtitlePreview_Invalid);
            var bytes = new byte[checked(rectangle->w * rectangle->h * 4)];
            var palette = (uint*)rectangle->data[1];
            for (var y = 0; y < rectangle->h; y++)
            {
                token.ThrowIfCancellationRequested();
                for (var x = 0; x < rectangle->w; x++)
                {
                    var index = rectangle->data[0][y * rectangle->linesize[0] + x];
                    var color = index < rectangle->nb_colors ? palette[index] : 0;
                    var alpha = (byte)(color >> 24);
                    var offset = (y * rectangle->w + x) * 4;
                    bytes[offset] = (byte)((color & 255) * alpha / 255);
                    bytes[offset + 1] = (byte)(((color >> 8) & 255) * alpha / 255);
                    bytes[offset + 2] = (byte)(((color >> 16) & 255) * alpha / 255);
                    bytes[offset + 3] = alpha;
                }
            }
            // 缓存压缩后的字幕小图，避免长视频的位图字幕占用大量常驻内存。
            using var output = new MemoryStream();
            using (var compressor = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true)) compressor.Write(bytes);
            var compressed = output.ToArray();
            cacheBytes += compressed.Length;
            if (cacheBytes > MaximumCacheBytes) throw CacheLimit();
            parts.Add(new(rectangle->x, rectangle->y, rectangle->w, rectangle->h, compressed));
        }
        bitmapCues.Add(new(start, end, canvas, parts));
    }

    public SubtitleOverlay? Render(double displayedSeconds, PixelSize displaySize, CancellationToken token = default)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            token.ThrowIfCancellationRequested();
            if (!double.IsFinite(displayedSeconds) || displaySize.Width <= 0 || displaySize.Height <= 0) return null;
            var time = (long)Math.Round((displayedSeconds + originSeconds) * 1000);
            var parts = new List<SubtitleOverlayPart>();
            try
            {
                if (renderer != 0)
                {
                    var size = ImageUtil.CalculateDecodeSize(displaySize, new PixelSize(1920, 1080));
                    FF.ass_set_frame_size(renderer, size.Width, size.Height);
                    FF.ass_set_storage_size(renderer, codedSize.Width, codedSize.Height);
                    FF.ass_set_pixel_aspect(renderer, size.Width / (double)size.Height /
                        (codedSize.Width / (double)codedSize.Height));
                    var changed = 0;
                    for (var image = FF.ass_render_frame(renderer, track, time, &changed); image != null; image = image->Next)
                    {
                        token.ThrowIfCancellationRequested();
                        if (image->Width <= 0 || image->Height <= 0 || (image->Color & 255) == 255) continue;
                        if ((long)image->Width * image->Height > MaximumBitmapPixels) throw CacheLimit();
                        var opacity = 255 - (image->Color & 255);
                        var content = new PixelRect(0, 0, image->Width, image->Height);
                        if (useDefaultTextStyle)
                        {
                            // libass 位图可能带有不对称的透明留白；先按有效透明度裁剪，再计算文字与底衬的居中位置。
                            var mask = new ReadOnlySpan<byte>(image->Bitmap, checked((image->Height - 1) * image->Stride + image->Width));
                            if (GetVisibleTextBounds(mask, image->Width, image->Height, image->Stride, opacity) is not { } visible)
                                continue;
                            content = visible;
                        }
                        var bytes = new byte[checked(content.Width * content.Height * 4)];
                        for (var y = 0; y < content.Height; y++)
                            for (var x = 0; x < content.Width; x++)
                            {
                                var alpha = image->Bitmap[(content.Y + y) * image->Stride + content.X + x] * opacity / 255;
                                var offset = (y * content.Width + x) * 4;
                                bytes[offset] = (byte)(((image->Color >> 8) & 255) * alpha / 255);
                                bytes[offset + 1] = (byte)(((image->Color >> 16) & 255) * alpha / 255);
                                bytes[offset + 2] = (byte)((image->Color >> 24) * alpha / 255);
                                bytes[offset + 3] = (byte)alpha;
                            }
                        parts.Add(new(CreateBitmap(bytes, content.Size), image->X + content.X, image->Y + content.Y));
                    }
                    return parts.Count == 0 ? null : useDefaultTextStyle
                        ? CreateDefaultTextOverlay(size, parts) : new SubtitleOverlay(size, parts);
                }
                // 位图事件按显示顺序结束；清屏事件没有矩形，但仍会终止前一幅字幕。
                var cue = FindBitmapCue(time);
                if (cue is null) return null;
                foreach (var part in cue.Parts)
                {
                    token.ThrowIfCancellationRequested();
                    var bytes = new byte[checked(part.Width * part.Height * 4)];
                    using var input = new MemoryStream(part.Data, writable: false);
                    using var decompressor = new DeflateStream(input, CompressionMode.Decompress);
                    decompressor.ReadExactly(bytes);
                    parts.Add(new(CreateBitmap(bytes, new(part.Width, part.Height)), part.X, part.Y));
                }
                return parts.Count == 0 ? null : new SubtitleOverlay(cue.Canvas, parts);
            }
            catch
            {
                foreach (var part in parts) part.Bitmap.Dispose();
                throw;
            }
        }
    }

    internal static PixelRect? GetVisibleTextBounds(ReadOnlySpan<byte> mask, int width, int height, int stride, uint opacity)
    {
        var left = width;
        var top = height;
        var right = 0;
        var bottom = 0;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                if (mask[y * stride + x] * opacity / 255 == 0) continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x + 1);
                bottom = Math.Max(bottom, y + 1);
            }
        return right > left && bottom > top ? new PixelRect(left, top, right - left, bottom - top) : null;
    }

    internal static SubtitleOverlay CreateDefaultTextOverlay(PixelSize canvas, IReadOnlyList<SubtitleOverlayPart> parts)
    {
        var text = new Rect(parts[0].X, parts[0].Y, parts[0].Bitmap.PixelSize.Width, parts[0].Bitmap.PixelSize.Height);
        foreach (var part in parts.Skip(1))
            text = text.Union(new Rect(part.X, part.Y, part.Bitmap.PixelSize.Width, part.Bitmap.PixelSize.Height));
        var fontSize = 42 * canvas.Height / 1080.0;
        var horizontalPadding = fontSize * 0.65;
        var verticalPadding = fontSize * 0.2;
        var width = text.Width + horizontalPadding * 2;
        var height = text.Height + verticalPadding * 2;
        var left = (canvas.Width - width) / 2;
        var top = canvas.Height * (1 - 0.055) - height;
        // 整个文字块与底衬一起定位，避免字体升降部差异改变底部留白，多行字幕共用一块底衬。
        var dx = (int)Math.Round(left + horizontalPadding - text.X);
        var dy = (int)Math.Round(top + verticalPadding - text.Y);
        var shifted = parts.Select(part => part with { X = part.X + dx, Y = part.Y + dy }).ToArray();
        var backdrop = new SubtitleBackdrop(new Rect(text.X + dx - horizontalPadding,
            text.Y + dy - verticalPadding, width, height), fontSize * 0.2);
        return new SubtitleOverlay(canvas, shifted, backdrop);
    }

    private BitmapCue? FindBitmapCue(long time)
    {
        var lower = 0;
        var upper = bitmapCues.Count;
        while (lower < upper)
        {
            var middle = lower + (upper - lower) / 2;
            if (bitmapCues[middle].Start <= time) lower = middle + 1;
            else upper = middle;
        }
        return lower > 0 && time < bitmapCues[lower - 1].End ? bitmapCues[lower - 1] : null;
    }

    private static Bitmap CreateBitmap(byte[] bytes, PixelSize size)
    {
        fixed (byte* data = bytes)
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, (nint)data, size, new Vector(96, 96), size.Width * 4);
    }

    private static NotSupportedException CacheLimit() => new(LocalizationManager.Instance.String_SubtitlePreview_Limit);

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            if (track != 0) FF.ass_free_track(track);
            if (renderer != 0) FF.ass_renderer_done(renderer);
            if (library != 0) FF.ass_library_done(library);
            track = renderer = library = 0;
            bitmapCues.Clear();
            pendingText.Clear();
        }
    }
}
