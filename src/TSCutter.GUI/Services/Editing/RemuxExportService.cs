using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.Discs;
using TSCutter.GUI.Services.TransportStream.Analysis;
using TSCutter.GUI.Utils.Files;
using TSCutter.GUI.Utils.Media;

namespace TSCutter.GUI.Services.Editing;

// 通过复制压缩包重新封装，导出过程不进行解码或编码。
internal sealed unsafe class RemuxExportService
{
    private static readonly AVRational Microseconds = new() { num = 1, den = FF.AV_TIME_BASE };

    public Task ExportAsync(RemuxExportRequest request, IProgress<RemuxExportProgress>? progress = null,
        CancellationToken cancellationToken = default, ExportTsCheckSession? checkSession = null) =>
        Task.Run(() => Export(request, progress, cancellationToken, checkSession), cancellationToken);

    internal static bool IsSupportedInput(string? name) => name is not null &&
        (name.Split(',').Contains("mov") || name.Split(',').Contains("matroska"));

    internal static IReadOnlyList<RemuxExportTrack> DescribeTracks(FormatContext input) => input.Streams.Select(stream =>
    {
        AVStream* raw = stream;
        stream.Metadata.TryGetValue("language", out var language);
        var title = GetTrackTitle(stream.Metadata);
        return new RemuxExportTrack(stream.Index, stream.Codecpar?.CodecName ?? "", raw->codecpar->codec_type,
            language, title, raw->disposition);
    }).ToArray();

    private static void Export(RemuxExportRequest request, IProgress<RemuxExportProgress>? progress,
        CancellationToken token, ExportTsCheckSession? checkSession = null)
    {
        Validate(request);
        token.ThrowIfCancellationRequested();
        var prepared = new List<PreparedSegment>();
        AVFormatContext* output = null;
        M2tsAvioWriteContext? avioWriter = null;
        AVIOInterruptCB_callback interrupt = _ => token.IsCancellationRequested ? 1 : 0;
        var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(request.OutputPath))!,
            $".tscutter-{Guid.NewGuid():N}.tmp");
        // M2TS 输出按顺序写出，可通过自定义 AVIO 写回调把封装器生成的最终字节交给检测会话；
        // MP4/MKV/VOB 的输出管线需要回写，不接入 TS 检测。
        var useCustomAvio = checkSession is not null && request.Segments[0].Settings.Container == "m2ts";
        try
        {
            foreach (var segment in request.Segments)
            {
                token.ThrowIfCancellationRequested();
                prepared.Add(Prepare(segment, token));
            }
            var first = prepared[0];
            // 独立 VOB 使用 MPEG-2 PS 复用器；不写需要配套 IFO 的 DVD-Video 导航包。
            FFmpegException.Check(FF.avformat_alloc_output_context2(&output, null,
                first.Segment.Settings.Container switch
                {
                    "mp4" => "mp4", "mkv" => "matroska", "vob" => "vob", "m2ts" => "mpegts",
                    _ => throw new RemuxExportException(RemuxExportError.UnsupportedContainer)
                }, temporary));
            if (output == null)
                throw new OutOfMemoryException();
            output->interrupt_callback = new AVIOInterruptCB { callback = interrupt };
            // 将显示时间起点设为零并保留 B 帧的解码预滚；MP4 通过编辑列表处理。
            output->avoid_negative_ts = first.Segment.Settings.Container is "vob" or "m2ts" ? 1 : 0;
            // MOV 仅在 FF_COMPLIANCE_UNOFFICIAL 模式下写入 dvcC/dvvC。
            // 否则虽然会保留 RPU 数据，却会丢失杜比视界配置。
            output->strict_std_compliance = -1;
            CreateStreams(output, first);
            foreach (var item in prepared.Skip(1)) CheckCompatibility(first, item);
            if (useCustomAvio)
            {
                avioWriter = M2tsAvioWriteContext.Open(temporary, checkSession!);
                output->pb = avioWriter.Io;
                output->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;
            }
            else
            {
                using (new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                FFmpegException.Check(FF.avio_open2(&output->pb, temporary, 2, &output->interrupt_callback, null));
            }
            AVDictionary* options = null;
            try
            {
                if (first.Segment.Settings.Container == "mp4")
                {
                    // FFmpeg 的 mdta 模式会省略 iTunes covr 封面节点，
                    // 保留封面时使用标准元数据写入方式。
                    if (!first.Streams.Any(index => first.Input.Streams[index].IsAttachedPicture))
                        FFmpegException.Check(FF.av_dict_set(&options, "movflags", "+use_metadata_tags", 0));
                    FFmpegException.Check(FF.av_dict_set(&options, "use_editlist", "1", 0));
                }
                if (first.Segment.Settings.Container == "m2ts")
                {
                    FFmpegException.Check(FF.av_dict_set(&options, "mpegts_m2ts_mode", "1", 0));
                    FFmpegException.Check(FF.av_dict_set(&options, "mpegts_copyts", "1", 0));
                }
                FFmpegException.Check(FF.avformat_write_header(output, &options));
            }
            finally { FF.av_dict_free(&options); }

            progress?.Report(new RemuxExportProgress(0, 0, 0));
            token.ThrowIfCancellationRequested();
            WriteAttachedPictures(output, first);
            var duration = prepared.Sum(item => item.End - item.Start);
            var timer = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            var lastDts = Enumerable.Repeat(FF.AV_NOPTS_VALUE, first.Streams.Length).ToArray();
            var initialCompositionOffsets = Enumerable.Repeat(FF.AV_NOPTS_VALUE, first.Streams.Length).ToArray();
            double offset = 0;
            long bytes = 0;
            var subtitleOrder = new long[first.Streams.Length];
            for (var segmentIndex = 0; segmentIndex < prepared.Count; segmentIndex++)
            {
                var item = prepared[segmentIndex];
                token.ThrowIfCancellationRequested();
                EnsureUnchanged(item);
                var actualEnd = CopyPackets(output, item, offset, lastDts, initialCompositionOffsets, subtitleOrder, token, (time, size) =>
                {
                    bytes += size;
                    if (timer.Elapsed - lastReport < TimeSpan.FromMilliseconds(100))
                        return;
                    lastReport = timer.Elapsed;
                    var elapsed = Math.Max(timer.Elapsed.TotalSeconds, 0.001);
                    var done = Math.Clamp(offset + time, 0, duration);
                    progress?.Report(new RemuxExportProgress(Math.Min(99.9, done / duration * 100),
                        bytes / elapsed, done > 0 ? elapsed * (duration - done) / done : 0));
                });
                EnsureUnchanged(item);
                duration += actualEnd - item.End;
                prepared[segmentIndex] = item with { End = actualEnd };
                offset += actualEnd - item.Start;
            }
            token.ThrowIfCancellationRequested();
            // MOV 和 Matroska 支持在写入文件尾时写入章节。
            // 截取到文件末尾时，以保留轨道的实际数据包终点计算偏移，
            // 包括音频和字幕尾部，使章节位置与片段拼接位置一致。
            if (first.Segment.Settings.Container is "mp4" or "mkv")
                CopyChapters(output, prepared);
            FFmpegException.Check(FF.av_write_trailer(output));
            if (useCustomAvio)
            {
                // 自定义 AVIO 不能走 avio_closep 的 URL 关闭逻辑：先冲出封装器缓冲
                // （其中包含 trailer 之后最后 flush 的字节）并检测，再释放原生上下文。
                avioWriter!.Complete();
                output->pb = null;
            }
            else
            {
                FFmpegException.Check(FF.avio_closep(&output->pb));
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, request.OutputPath, overwrite: true);
            progress?.Report(new RemuxExportProgress(100, bytes / Math.Max(timer.Elapsed.TotalSeconds, 0.001), 0));
        }
        catch (FFmpegException) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
        finally
        {
            if (output != null)
            {
                if (output->pb != null)
                {
                    if (avioWriter != null)
                    {
                        // 释放自定义 AVIO 前先把指针置空，避免悬空指针被再次释放。
                        output->pb = null;
                        avioWriter.Dispose();
                    }
                    else
                    {
                        FF.avio_closep(&output->pb);
                    }
                }
                FF.avformat_free_context(output);
            }
            foreach (var item in prepared) item.Input.Dispose();
            if (File.Exists(temporary))
                File.Delete(temporary);
            GC.KeepAlive(interrupt);
        }
    }

    internal static void Validate(RemuxExportRequest request)
    {
        if (request.Segments is not { Count: > 0 and <= 256 } || string.IsNullOrWhiteSpace(request.OutputPath))
            throw new RemuxExportException(RemuxExportError.InvalidRequest);
        var container = request.Segments[0].Settings.Container;
        var extension = Path.GetExtension(request.OutputPath);
        if (!extension.Equals("." + container, StringComparison.OrdinalIgnoreCase))
            throw new RemuxExportException(RemuxExportError.UnsupportedContainer, extension);
        foreach (var item in request.Segments)
        {
            if (!item.Settings.IsValid || item.Settings.Container != container ||
                !double.IsFinite(item.StartSeconds) || item.StartSeconds < 0 ||
                item.EndSeconds is { } end && (!double.IsFinite(end) || end <= item.StartSeconds))
                throw new RemuxExportException(RemuxExportError.InvalidRequest);
            if (item.Settings.Disc is { } disc && !MediaFileUtil.PathsEqual(item.SourcePath, disc.SourcePath))
                throw new RemuxExportException(RemuxExportError.InvalidRequest);
            if (MediaFileUtil.ConflictsWithSource(request.OutputPath, item.SourcePath))
                throw new RemuxExportException(RemuxExportError.SameFile);
        }
    }

    private sealed record PreparedSegment(RemuxExportSegment Segment, FormatContext Input, int[] Streams,
        double Start, double End, long[] StartDts, long[] EndPts, long[] EndDurations, SourceStamp[] Source);

    private sealed record SourceStamp(string Path, long Length, DateTime Modified);

    private static PreparedSegment Prepare(RemuxExportSegment segment, CancellationToken token)
    {
        var source = CaptureSource(segment.SourcePath, segment.Settings.Disc);
        var input = OpenInput(segment, token);
        try
        {
            input.LoadStreamInfo();
            if (segment.Settings.Disc?.Kind == DiscKind.Dvd)
                ResolveDvdMpegAudioLayers(input, token);
            if (segment.Settings.Disc is null && !IsSupportedInput(input.InputFormat?.Name))
                throw new RemuxExportException(RemuxExportError.UnsupportedContainer, input.InputFormat?.Name ?? "");
            AVFormatContext* raw = input;
            var indices = segment.Settings.StreamIndices.ToArray();
            if (indices.Any(index => index >= raw->nb_streams) || segment.Settings.ReferenceVideoIndex >= raw->nb_streams)
                throw new RemuxExportException(RemuxExportError.InvalidRequest);
            var reference = raw->streams[segment.Settings.ReferenceVideoIndex];
            if (reference->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO || (reference->disposition & 1024) != 0)
                throw new RemuxExportException(RemuxExportError.InvalidRequest);
            var sourceEnd = segment.Settings.Disc is { } disc
                ? disc.Duration.TotalSeconds + segment.Settings.TimelineOriginSeconds
                : StreamEnd(raw, reference, segment.Settings.TimelineOriginSeconds);
            var requestedStart = segment.StartSeconds + segment.Settings.TimelineOriginSeconds;
            var requestedEnd = segment.EndSeconds is { } end ? end + segment.Settings.TimelineOriginSeconds : sourceEnd;
            if (segment.EndSeconds is null && segment.Settings.Disc is null)
            {
                foreach (var index in indices)
                {
                    var selected = raw->streams[index];
                    if ((selected->disposition & 1024) == 0 && selected->codecpar->codec_type is
                        AVMediaType.AVMEDIA_TYPE_VIDEO or AVMediaType.AVMEDIA_TYPE_AUDIO or AVMediaType.AVMEDIA_TYPE_SUBTITLE)
                        requestedEnd = Math.Max(requestedEnd, StreamEnd(raw, selected, segment.Settings.TimelineOriginSeconds));
                }
            }
            if (requestedStart >= sourceEnd || segment.EndSeconds is not null && requestedEnd > sourceEnd + 0.1)
                throw new RemuxExportException(RemuxExportError.InvalidBoundary);
            var startBoundary = FindBoundary(input, reference->index, requestedStart, false, token);
            var endBoundary = segment.EndSeconds is null || requestedEnd >= sourceEnd - 0.002
                ? (Time: requestedEnd, Dts: long.MaxValue)
                : FindBoundary(input, reference->index, requestedEnd, true, token);
            if (endBoundary.Time <= startBoundary.Time)
                throw new RemuxExportException(RemuxExportError.InvalidBoundary);
            var endTime = endBoundary.Time;
            var starts = new long[indices.Length];
            var ends = Enumerable.Repeat(long.MaxValue, indices.Length).ToArray();
            var endDurations = new long[indices.Length];
            for (var i = 0; i < indices.Length; i++)
            {
                var stream = raw->streams[indices[i]];
                if (stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && (stream->disposition & 1024) == 0)
                {
                    var start = stream->index == reference->index ? startBoundary :
                        FindBoundary(input, stream->index, startBoundary.Time, false, token);
                    var finish = endBoundary.Dts == long.MaxValue ? endBoundary :
                        stream->index == reference->index ? endBoundary :
                            FindBoundary(input, stream->index, endBoundary.Time, true, token);
                    if (Math.Abs(start.Time - startBoundary.Time) > 0.002 || Math.Abs(finish.Time - endBoundary.Time) > 0.002)
                        throw new RemuxExportException(RemuxExportError.InvalidBoundary, $"#{stream->index}");
                    InspectGopBoundary(input, stream->index, start.Time, token, segment.Settings.Disc is not null);
                    if (finish.Dts != long.MaxValue)
                    {
                        // 标记终点的关键帧也要写入；音频、字幕和章节延伸到该帧显示结束。
                        var frameEnd = InspectGopBoundary(input, stream->index, finish.Time, token, segment.Settings.Disc is not null);
                        if (!double.IsFinite(frameEnd) || frameEnd <= finish.Time)
                            throw new RemuxExportException(RemuxExportError.MissingTimestamp, $"#{stream->index}");
                        endTime = Math.Max(endTime, frameEnd);
                        endDurations[i] = Ticks(frameEnd - finish.Time, stream->time_base);
                    }
                    starts[i] = start.Dts;
                    ends[i] = finish.Dts == long.MaxValue ? long.MaxValue : Ticks(finish.Time, stream->time_base);
                }
                // MKV 的 PGS 会补齐起点显示状态；其他位图格式仍需独立处理其显示集合。
                if (stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_SUBTITLE &&
                    stream->codecpar->codec_id is not (AVCodecID.AV_CODEC_ID_MOV_TEXT or AVCodecID.AV_CODEC_ID_ASS or
                        AVCodecID.AV_CODEC_ID_SSA or AVCodecID.AV_CODEC_ID_SUBRIP or AVCodecID.AV_CODEC_ID_TEXT or AVCodecID.AV_CODEC_ID_WEBVTT) &&
                    !(segment.Settings.Container is "mkv" or "vob" or "m2ts" &&
                      stream->codecpar->codec_id is AVCodecID.AV_CODEC_ID_HDMV_PGS_SUBTITLE or AVCodecID.AV_CODEC_ID_DVD_SUBTITLE))
                    throw new RemuxExportException(RemuxExportError.UnsupportedTrack, $"#{stream->index}: {stream->codecpar->codec_id}");
            }
            var result = new PreparedSegment(segment, input, indices, startBoundary.Time, endTime,
                starts, ends, endDurations, source);
            input = null!;
            return result;
        }
        finally { input?.Dispose(); }
    }

    private static double StreamEnd(AVFormatContext* input, AVStream* stream, double origin) => stream->duration > 0
        ? Seconds(stream->duration, stream->time_base) +
          (stream->start_time == FF.AV_NOPTS_VALUE ? origin : Seconds(stream->start_time, stream->time_base))
        : input->duration / (double)FF.AV_TIME_BASE +
          (input->start_time == FF.AV_NOPTS_VALUE ? 0 : input->start_time / (double)FF.AV_TIME_BASE);

    private static (double Time, long Dts) FindBoundary(FormatContext input, int index, double time, bool next,
        CancellationToken token)
    {
        AVStream* stream = input.Streams[index];
        input.SeekFrame(Ticks(time, stream->time_base), index);
        (double Time, long Dts)? previous = null;
        double? pendingTime = null;
        long pendingDuration = 0;
        foreach (var packet in input.ReadPackets())
        {
            token.ThrowIfCancellationRequested();
            if (packet.StreamIndex != index) continue;
            AVPacket* raw = packet;
            if (pendingTime is { } pending && raw->dts != FF.AV_NOPTS_VALUE)
            {
                var recovered = (Time: pending, Dts: raw->dts - pendingDuration);
                if (Math.Abs(recovered.Time - time) <= 0.002) return recovered;
                if (recovered.Time > time) return next ? recovered : previous ??
                    throw new RemuxExportException(RemuxExportError.InvalidBoundary);
                previous = recovered;
                pendingTime = null;
            }
            if (pendingTime is not null)
            {
                if (raw->duration <= 0) throw new RemuxExportException(RemuxExportError.MissingTimestamp, $"#{index}");
                pendingDuration += raw->duration;
                continue;
            }
            if ((packet.Flags & 1) == 0) continue;
            if (raw->pts == FF.AV_NOPTS_VALUE)
                throw new RemuxExportException(RemuxExportError.MissingTimestamp, $"#{index}");
            if (raw->dts == FF.AV_NOPTS_VALUE)
            {
                if (raw->duration <= 0) throw new RemuxExportException(RemuxExportError.MissingTimestamp, $"#{index}");
                pendingTime = Seconds(raw->pts, stream->time_base);
                pendingDuration = raw->duration;
                continue;
            }
            var value = (Time: Seconds(raw->pts, stream->time_base), Dts: raw->dts);
            if (Math.Abs(value.Time - time) <= 0.002) return value;
            if (value.Time > time) return next ? value : previous ?? throw new RemuxExportException(RemuxExportError.InvalidBoundary);
            previous = value;
        }
        return !next && previous is { } last ? last : throw new RemuxExportException(RemuxExportError.InvalidBoundary);
    }

    private static double InspectGopBoundary(FormatContext input, int index, double boundary, CancellationToken token, bool disc = false)
    {
        AVStream* stream = input.Streams[index];
        input.SeekFrame(Ticks(boundary, stream->time_base), index);
        var found = false;
        var nextTime = double.PositiveInfinity;
        var packetEnd = double.NaN;
        foreach (var packet in input.ReadPackets())
        {
            token.ThrowIfCancellationRequested();
            if (packet.StreamIndex != index)
                continue;
            AVPacket* raw = packet;
            if (raw->pts == FF.AV_NOPTS_VALUE)
                throw new RemuxExportException(RemuxExportError.MissingTimestamp, $"#{index}");
            var pts = Seconds(raw->pts, stream->time_base);
            if (!found)
            {
                if ((raw->flags & 1) == 0 || Math.Abs(pts - boundary) > 0.002)
                    continue;
                found = true;
                if (raw->duration > 0)
                    packetEnd = pts + Seconds(raw->duration, stream->time_base);
                continue;
            }
            if (pts > boundary)
                nextTime = Math.Min(nextTime, pts);
            if ((raw->flags & 1) != 0 && pts > boundary + 0.002)
                break;
            // 开放 GOP 的前导画面可能排在访问点之后、显示时间却更早。
            // 只保留访问点会丢掉这些画面，起点使用该访问点也可能缺少参考帧。
            if (pts < boundary - 0.002 && !(disc && stream->codecpar->codec_id == AVCodecID.AV_CODEC_ID_MPEG2VIDEO))
                throw new RemuxExportException(RemuxExportError.UnsafeGop, $"#{index}");
        }
        if (!found)
            throw new RemuxExportException(RemuxExportError.InvalidBoundary, $"#{index}");
        // DVD 的短 GOP 可能在下一关键包之后才输出夹在两个 I 帧之间的 B 帧。
        // 这些画面不属于要保留的截止帧，不能把下一 I 帧的 PTS 当作它的显示终点，
        // 否则会多算两帧，并把后续合并片段的音视频和字幕整体推迟。
        if (disc && stream->codecpar->codec_id == AVCodecID.AV_CODEC_ID_MPEG2VIDEO && double.IsFinite(packetEnd))
            return packetEnd;
        // 先按播放顺序找下一帧，兼容 B 帧重排序和可变帧率，不能按平均帧率补一帧。
        // 文件末帧没有后继画面时，使用压缩包记录的时长。
        return double.IsFinite(nextTime) ? nextTime : packetEnd;
    }

    private static bool SupportsNativeDiscTrack(string container, AVCodecID codec) => container switch
    {
        "vob" => codec is AVCodecID.AV_CODEC_ID_MPEG1VIDEO or AVCodecID.AV_CODEC_ID_MPEG2VIDEO or
            AVCodecID.AV_CODEC_ID_MP1 or AVCodecID.AV_CODEC_ID_MP2 or AVCodecID.AV_CODEC_ID_AC3 or
            AVCodecID.AV_CODEC_ID_DTS or AVCodecID.AV_CODEC_ID_PCM_DVD or AVCodecID.AV_CODEC_ID_PCM_S16BE or
            AVCodecID.AV_CODEC_ID_DVD_SUBTITLE,
        "m2ts" => codec is AVCodecID.AV_CODEC_ID_H264 or AVCodecID.AV_CODEC_ID_HEVC or
            AVCodecID.AV_CODEC_ID_MPEG2VIDEO or AVCodecID.AV_CODEC_ID_VC1 or AVCodecID.AV_CODEC_ID_AC3 or
            AVCodecID.AV_CODEC_ID_EAC3 or AVCodecID.AV_CODEC_ID_DTS or AVCodecID.AV_CODEC_ID_TRUEHD or
            AVCodecID.AV_CODEC_ID_PCM_BLURAY or AVCodecID.AV_CODEC_ID_HDMV_PGS_SUBTITLE,
        _ => false
    };

    private static void ResolveDvdMpegAudioLayers(FormatContext input, CancellationToken token)
    {
        var remaining = input.Streams.Where(stream => stream.Codecpar?.CodecId is
            AVCodecID.AV_CODEC_ID_MP1 or AVCodecID.AV_CODEC_ID_MP2).Select(stream => stream.Index).ToHashSet();
        if (remaining.Count == 0)
            return;
        var count = 0;
        foreach (var packet in input.ReadPackets())
        {
            token.ThrowIfCancellationRequested();
            if (++count > 256)
                break;
            AVPacket* raw = packet;
            if (!remaining.Contains(raw->stream_index) || raw->size < 4 ||
                raw->data[0] != 0xFF || (raw->data[1] & 0xE0) != 0xE0)
                continue;
            // IFO 的 MPEG-1/2 音频标记是版本号，不能直接当作 Layer I/II。
            // 以实际帧头补齐编码类型，只修正封装参数，不改变压缩音频内容。
            var codec = ((raw->data[1] >> 1) & 3) switch
            {
                3 => AVCodecID.AV_CODEC_ID_MP1, 2 => AVCodecID.AV_CODEC_ID_MP2,
                1 => AVCodecID.AV_CODEC_ID_MP3, _ => AVCodecID.AV_CODEC_ID_NONE
            };
            if (codec == AVCodecID.AV_CODEC_ID_NONE)
                continue;
            AVStream* stream = input.Streams[raw->stream_index];
            stream->codecpar->codec_id = codec;
            remaining.Remove(raw->stream_index);
            if (remaining.Count == 0)
                break;
        }
    }

    private static void CreateStreams(AVFormatContext* output, PreparedSegment first)
    {
        AVFormatContext* input = first.Input;
        FFmpegException.Check(FF.av_dict_copy(&output->metadata, input->metadata, 0));
        foreach (var index in first.Streams)
        {
            var source = input->streams[index];
            var attachment = source->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_ATTACHMENT;
            if (attachment ? first.Segment.Settings.Container != "mkv" :
                FF.avformat_query_codec(output->oformat, source->codecpar->codec_id, 0) <= 0 &&
                !SupportsNativeDiscTrack(first.Segment.Settings.Container, source->codecpar->codec_id))
                throw new RemuxExportException(RemuxExportError.UnsupportedTrack,
                    $"#{index}: {first.Input.Streams[index].Codecpar?.CodecName}");
            var stream = FF.avformat_new_stream(output, null);
            if (stream == null) throw new OutOfMemoryException();
            // 同时复制编解码配置、HDR 静态附加数据、杜比视界配置和显示矩阵。
            FFmpegException.Check(FF.avcodec_parameters_copy(stream->codecpar, source->codecpar));
            stream->codecpar->codec_tag = first.Segment.Settings.Container == "mp4" &&
                source->codecpar->codec_id == AVCodecID.AV_CODEC_ID_HEVC ? source->codecpar->codec_tag : 0;
            stream->time_base = source->time_base;
            stream->avg_frame_rate = source->avg_frame_rate;
            stream->sample_aspect_ratio = source->sample_aspect_ratio;
            stream->disposition = source->disposition;
            FFmpegException.Check(FF.av_dict_copy(&stream->metadata, source->metadata, 0));
            // MOV 将 trak/udta/name 读取为 name，写入同一节点时却使用 title。
            // 统一两种封装在读取和写入时使用的轨道名称。
            var title = GetTrackTitle(new NativeMetadata(source->metadata));
            if (!string.IsNullOrEmpty(title))
                FFmpegException.Check(FF.av_dict_set(&stream->metadata, "title", title, 0));
            RemoveDurationTags(&stream->metadata);
            if ((source->disposition & 1024) != 0)
                FFmpegException.Check(FF.av_packet_ref(&stream->attached_pic, &source->attached_pic));
        }
        RemoveDurationTags(&output->metadata);
    }

    private static void WriteAttachedPictures(AVFormatContext* output, PreparedSegment first)
    {
        for (var i = 0; i < first.Streams.Length; i++)
        {
            AVStream* stream = first.Input.Streams[first.Streams[i]];
            if ((stream->disposition & 1024) == 0) continue;
            using var packet = new Packet();
            AVPacket* raw = packet;
            FFmpegException.Check(FF.av_packet_ref(raw, &stream->attached_pic));
            raw->stream_index = i;
            raw->pts = raw->dts = 0;
            raw->pos = -1;
            FFmpegException.Check(FF.av_interleaved_write_frame(output, raw));
        }
    }

    private static void RemoveDurationTags(AVDictionary** metadata)
    {
        foreach (var key in new[] { "DURATION", "NUMBER_OF_FRAMES", "NUMBER_OF_BYTES", "_STATISTICS_WRITING_APP",
                     "_STATISTICS_WRITING_DATE_UTC", "_STATISTICS_TAGS" })
            FFmpegException.Check(FF.av_dict_set(metadata, key, null!, 0));
    }

    private static void CheckCompatibility(PreparedSegment first, PreparedSegment other)
    {
        if (first.Streams.Length != other.Streams.Length)
            throw new RemuxExportException(RemuxExportError.IncompatibleStreams);
        for (var i = 0; i < first.Streams.Length; i++)
        {
            AVStream* a = first.Input.Streams[first.Streams[i]], b = other.Input.Streams[other.Streams[i]];
            var x = a->codecpar;
            var y = b->codecpar;
            if (x->codec_id != y->codec_id || x->codec_type != y->codec_type || x->format != y->format ||
                x->profile != y->profile || x->level != y->level || x->width != y->width || x->height != y->height ||
                x->sample_rate != y->sample_rate || x->block_align != y->block_align || x->frame_size != y->frame_size ||
                x->sample_aspect_ratio.num * (long)y->sample_aspect_ratio.den != y->sample_aspect_ratio.num * (long)x->sample_aspect_ratio.den ||
                x->color_range != y->color_range || x->color_primaries != y->color_primaries || x->color_trc != y->color_trc ||
                x->color_space != y->color_space || x->chroma_location != y->chroma_location || x->field_order != y->field_order ||
                FF.av_channel_layout_compare(&x->ch_layout, &y->ch_layout) != 0 || a->disposition != b->disposition ||
                !DataEqual(x->extradata, x->extradata_size, y->extradata, y->extradata_size) ||
                x->nb_coded_side_data != y->nb_coded_side_data || !MetadataEqual(a->metadata, b->metadata))
                throw new RemuxExportException(RemuxExportError.IncompatibleStreams, $"#{first.Streams[i]} / #{other.Streams[i]}");
            for (var side = 0; side < x->nb_coded_side_data; side++)
            {
                var left = x->coded_side_data[side];
                var right = y->coded_side_data[side];
                if (left.type != right.type || !DataEqual(left.data, checked((int)left.size), right.data, checked((int)right.size)))
                    throw new RemuxExportException(RemuxExportError.IncompatibleStreams, $"#{first.Streams[i]}: {left.type}");
            }
            if ((a->disposition & 1024) != 0 && !DataEqual(a->attached_pic.data, a->attached_pic.size, b->attached_pic.data, b->attached_pic.size))
                throw new RemuxExportException(RemuxExportError.IncompatibleStreams, $"#{first.Streams[i]}: cover");
        }
    }

    private static string GetTrackTitle(NativeMetadata metadata)
    {
        if (metadata.TryGetValue("title", out var title) && !string.IsNullOrEmpty(title)) return title;
        return metadata.TryGetValue("name", out var name) ? name : "";
    }

    private static bool MetadataEqual(AVDictionary* first, AVDictionary* second)
    {
        if (GetTrackTitle(new NativeMetadata(first)) != GetTrackTitle(new NativeMetadata(second))) return false;
        foreach (var key in new[] { "language", "filename", "mimetype" })
        {
            var a = FF.av_dict_get(first, key, null, 0);
            var b = FF.av_dict_get(second, key, null, 0);
            if ((a == null ? "" : Marshal.PtrToStringUTF8((IntPtr)a->value)) !=
                (b == null ? "" : Marshal.PtrToStringUTF8((IntPtr)b->value))) return false;
        }
        return true;
    }

    private static bool DataEqual(byte* a, int aLength, byte* b, int bLength) => aLength == bLength &&
        new ReadOnlySpan<byte>(a, aLength).SequenceEqual(new ReadOnlySpan<byte>(b, bLength));

    private static void CopyChapters(AVFormatContext* output, IReadOnlyList<PreparedSegment> segments)
    {
        var chapters = new List<(double Start, double End, IntPtr Metadata)>();
        double offset = 0;
        foreach (var item in segments)
        {
            AVFormatContext* input = item.Input;
            for (var i = 0; i < input->nb_chapters; i++)
            {
                var source = input->chapters[i];
                var start = Math.Max(item.Start, Seconds(source->start, source->time_base));
                var end = Math.Min(item.End, Seconds(source->end, source->time_base));
                if (end > start) chapters.Add((offset + start - item.Start, offset + end - item.Start, (IntPtr)source->metadata));
            }
            offset += item.End - item.Start;
        }
        if (chapters.Count == 0) return;
        output->chapters = (AVChapter**)FF.av_mallocz(checked((ulong)chapters.Count * (ulong)sizeof(AVChapter*)));
        if (output->chapters == null) throw new OutOfMemoryException();
        foreach (var value in chapters)
        {
            var chapter = (AVChapter*)FF.av_mallocz((ulong)sizeof(AVChapter));
            if (chapter == null) throw new OutOfMemoryException();
            output->chapters[output->nb_chapters++] = chapter;
            chapter->id = output->nb_chapters - 1;
            chapter->time_base = Microseconds;
            chapter->start = Ticks(value.Start, Microseconds);
            chapter->end = Ticks(value.End, Microseconds);
            FFmpegException.Check(FF.av_dict_copy(&chapter->metadata, (AVDictionary*)value.Metadata, 0));
        }
    }

    private static double CopyPackets(AVFormatContext* output, PreparedSegment item, double offset, long[] lastDts,
        long[] initialCompositionOffsets, long[] subtitleOrder, CancellationToken token, Action<double, int> report)
    {
        var pgs = ReadPgsPreroll(item, token);
        var timeline = item.Segment.Settings.Disc is { Kind: DiscKind.BluRay } disc ? disc.CreateTimeline() : null;
        var activeSegment = timeline?.FindByRelativeTime((long)Math.Round(item.Start * 90_000));
        var hasTextSubtitles = item.Streams.Any(index =>
            item.Input.Streams[index].Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_SUBTITLE &&
            item.Input.Streams[index].Codecpar?.CodecId != AVCodecID.AV_CODEC_ID_HDMV_PGS_SUBTITLE);
        // MOV 默认按各轨道索引分别向前定位，能保留跨起点的 mov_text 字幕。
        // Matroska 文字字幕快速模式按公共回看范围读取；关闭后从头读取以保留长字幕。
        // 起点仍在回看范围内时从头读取，避免定位到首个视频关键帧后漏掉开头字幕。
        var isMatroska = item.Input.InputFormat!.Name.Split(',').Contains("matroska");
        var fastSubtitleSeek = hasTextSubtitles && isMatroska && item.Segment.Settings.FastSubtitleSeek &&
            item.Start - item.Segment.Settings.TimelineOriginSeconds > SubtitleReadDefaults.LookbackSeconds;
        var scanSubtitles = hasTextSubtitles && isMatroska && !fastSubtitleSeek;
        using var scanInput = scanSubtitles
            ? OpenInput(item.Segment, token) : null;
        // 可定位的输入复用准备阶段的上下文，避免再次打开文件和探测轨道。
        var input = scanInput ?? item.Input;
        AVFormatContext* rawInput = input;
        if (scanInput is not null)
        {
            // 新上下文还需要初始化 FFmpeg 内部的 B 帧时间戳状态。
            // 仅复制 codecpar 和帧率会使分数帧率 HEVC 的 DTS 回退或重复。
            input.LoadStreamInfo();
        }
        var map = Enumerable.Repeat(-1, checked((int)rawInput->nb_streams)).ToArray();
        for (var i = 0; i < item.Streams.Length; i++) map[item.Streams[i]] = i;
        // MOV 可直接按索引跳过不再需要的轨道数据，避免为字幕读取整段音视频尾部。
        for (var i = 0; i < map.Length; i++)
            if (map[i] < 0) rawInput->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
        if (!scanSubtitles)
        {
            var reference = rawInput->streams[item.Segment.Settings.ReferenceVideoIndex];
            var seekStart = fastSubtitleSeek ? item.Start - SubtitleReadDefaults.LookbackSeconds : item.Start;
            input.SeekFrame(Ticks(seekStart, reference->time_base), reference->index);
        }
        var toEof = item.Segment.EndSeconds is null;
        var actualEnd = item.Start;
        var previousDts = lastDts.ToArray();
        var videoStarted = new bool[item.Streams.Length];
        var videoSegments = new DiscSegment?[item.Streams.Length];
        var endFrameWritten = new bool[item.Streams.Length];
        var packetsWritten = new int[item.Streams.Length];
        var nextSourceDts = item.StartDts.ToArray();
        var sourceStartDts = item.StartDts.ToArray();
        var finished = new bool[item.Streams.Length];
        var remainingStreams = item.Streams.Length;
        for (var i = 0; i < item.Streams.Length; i++)
        {
            var stream = rawInput->streams[item.Streams[i]];
            if ((stream->disposition & 1024) == 0 && stream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_ATTACHMENT) continue;
            finished[i] = true;
            remainingStreams--;
            stream->discard = AVDiscard.AVDISCARD_ALL;
        }
        foreach (var (streamIndex, preroll) in pgs)
            WritePgsPacket(output, map[streamIndex], preroll.Epoch, offset, lastDts);
        // 各轨道独立确认终点；在读取下一包之前判断是否结束，避免 continue 绕过退出判断。
        using var packets = input.ReadPackets().GetEnumerator();
        while (remainingStreams > 0 && packets.MoveNext())
        {
            token.ThrowIfCancellationRequested();
            var packet = packets.Current;
            AVPacket* raw = packet;
            if (timeline?.FindByByte(raw->pos) is { } segment)
            {
                if (!ReferenceEquals(activeSegment, segment) && segment.RelativeStart90k / 90_000.0 >= item.Start - 0.002)
                {
                    if (!timeline.Continues(activeSegment, segment))
                    {
                        var transition = segment.RelativeStart90k / 90_000.0;
                        if (transition < item.End)
                            foreach (var subtitleIndex in pgs.Keys.ToArray())
                            {
                                WritePgsPacket(output, map[subtitleIndex], pgs[subtitleIndex].State.CreateClear(),
                                    offset + Math.Max(0, transition - item.Start), lastDts);
                                pgs[subtitleIndex] = pgs[subtitleIndex] with { State = new PgsSubtitleState() };
                            }
                    }
                    activeSegment = segment;
                }
                var ticks = raw->pts == FF.AV_NOPTS_VALUE ? double.NaN :
                    Seconds(raw->pts, rawInput->streams[raw->stream_index]->time_base) * 90_000;
                if (ticks < segment.RelativeStart90k || ticks >= segment.RelativeStart90k + segment.OutPts90k - segment.InPts90k)
                    continue;
            }
            if (raw->stream_index >= map.Length || map[raw->stream_index] < 0) continue;
            var index = map[raw->stream_index];
            var stream = rawInput->streams[raw->stream_index];
            // 光盘按播放顺序交织包。视频/音频已结束后，给稀疏字幕保留有界前读，
            // 不为等待下一条字幕而继续扫描到整张光盘末尾。
            if (item.Segment.Settings.Disc is not null && !toEof &&
                stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO &&
                raw->pts != FF.AV_NOPTS_VALUE && Seconds(raw->pts, stream->time_base) > item.End + SubtitleReadDefaults.LookbackSeconds &&
                item.Streams.Select((source, slot) => (source, slot)).All(entry => finished[entry.slot] ||
                    input.Streams[entry.source].Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_SUBTITLE))
                break;
            if (finished[index])
                continue;
            var type = stream->codecpar->codec_type;
            if (type == AVMediaType.AVMEDIA_TYPE_VIDEO && timeline?.FindByByte(raw->pos) is { } videoSegment &&
                !ReferenceEquals(videoSegments[index], videoSegment))
            {
                // 非连续播放项必须从独立访问点接入。入点落在 GOP 内时，过滤掉的
                // 预滚参考帧无法直接拼入输出；强行复制会花屏或导致 DTS 回退。
                if (videoStarted[index] && !timeline.Continues(videoSegments[index], videoSegment) && (raw->flags & 1) == 0)
                    throw new RemuxExportException(RemuxExportError.UnsafeDiscBoundary,
                        Utils.CommonUtil.FormatSeconds(videoSegment.RelativeStart90k / 90_000.0));
                videoSegments[index] = videoSegment;
            }
            if (type == AVMediaType.AVMEDIA_TYPE_ATTACHMENT || (stream->disposition & 1024) != 0) continue;
            if (item.Segment.Settings.Disc is not null && type == AVMediaType.AVMEDIA_TYPE_AUDIO && raw->pts == FF.AV_NOPTS_VALUE)
            {
                // MPEG-PS/PES 可能只为同组的首个音频帧记录时间戳。
                if (raw->dts != FF.AV_NOPTS_VALUE)
                    raw->pts = raw->dts;
                else if (packetsWritten[index] > 0 && raw->duration > 0)
                    raw->pts = nextSourceDts[index];
                else
                    continue;
            }
            if (raw->pts == FF.AV_NOPTS_VALUE)
                throw new RemuxExportException(RemuxExportError.MissingTimestamp, $"#{stream->index}");
            if (raw->dts == FF.AV_NOPTS_VALUE)
            {
                if (type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    // Matroska 可能省略 DTS，仅根据后续时间戳和数据包时长，
                    // 补齐片段开头可确定的解码时间戳。
                    if (!videoStarted[index] && Math.Abs(Seconds(raw->pts, stream->time_base) - item.Start) > 0.002) continue;
                    raw->dts = nextSourceDts[index];
                    if (raw->duration <= 0) throw new RemuxExportException(RemuxExportError.MissingTimestamp, $"#{stream->index}");
                }
                else raw->dts = raw->pts;
            }
            nextSourceDts[index] = raw->dts + raw->duration;
            var pts = Seconds(raw->pts, stream->time_base);
            var packetDuration = Math.Max(0, Seconds(raw->duration, stream->time_base));
            var end = pts + packetDuration;
            var isEndFrame = type == AVMediaType.AVMEDIA_TYPE_VIDEO && raw->pts == item.EndPts[index];
            var discMpeg2 = item.Segment.Settings.Disc is not null && stream->codecpar->codec_id == AVCodecID.AV_CODEC_ID_MPEG2VIDEO;
            if (discMpeg2 && videoStarted[index] && pts < item.Start - 0.002)
                continue;
            if (discMpeg2 && endFrameWritten[index] && raw->pts > item.EndPts[index])
            {
                finished[index] = true;
                remainingStreams--;
                if (item.Segment.Settings.Disc is null)
                    stream->discard = AVDiscard.AVDISCARD_ALL;
                continue;
            }
            if (type == AVMediaType.AVMEDIA_TYPE_VIDEO)
            {
                if (!videoStarted[index])
                {
                    if ((raw->flags & 1) == 0 || Math.Abs(pts - item.Start) > 0.002)
                    {
                        if ((raw->flags & 1) != 0 && pts > item.Start + 0.002)
                            throw new RemuxExportException(RemuxExportError.InvalidBoundary, $"#{stream->index}");
                        continue;
                    }
                    // MKV 定位后可能缺少开头几个 DTS；字幕回看则能恢复它们。
                    // 两次读取的 DTS 可相差若干毫秒，起止帧必须按实际 PTS 识别。
                    sourceStartDts[index] = raw->dts;
                    videoStarted[index] = true;
                }
                if (raw->pts > item.EndPts[index])
                    throw new RemuxExportException(RemuxExportError.InvalidBoundary, $"#{stream->index}");
                // VFR 压缩包的解码时长可能不同于显示时长；终点帧须完整显示后再结束。
                if (isEndFrame)
                    raw->duration = item.EndDurations[index];
            }
            else if (type == AVMediaType.AVMEDIA_TYPE_SUBTITLE)
            {
                // 按轨道内的时间顺序读取；跨越终点的事件裁剪后仍继续接收重叠字幕，
                // 直到读到起始时间已到终点的下一条事件。
                if (!toEof && pts >= item.End)
                {
                    finished[index] = true;
                    remainingStreams--;
                    if (item.Segment.Settings.Disc is null)
                        stream->discard = AVDiscard.AVDISCARD_ALL;
                    continue;
                }
                if (stream->codecpar->codec_id == AVCodecID.AV_CODEC_ID_DVD_SUBTITLE)
                {
                    var clipped = VobSubtitlePacket.Clip(new ReadOnlySpan<byte>(raw->data, raw->size),
                        pts, item.Start, item.End);
                    if (clipped is null)
                        continue;
                    using var replacement = new Packet();
                    AVPacket* target = replacement;
                    FFmpegException.Check(FF.av_new_packet(target, clipped.Bytes.Length));
                    FFmpegException.Check(FF.av_packet_copy_props(target, raw));
                    clipped.Bytes.CopyTo(new Span<byte>(target->data, target->size));
                    FF.av_packet_unref(raw);
                    FF.av_packet_move_ref(raw, target);
                    raw->pts = Ticks(clipped.Start, stream->time_base);
                    raw->dts = raw->pts;
                    raw->duration = Ticks(clipped.Duration, stream->time_base);
                }
                else if (pgs.TryGetValue(raw->stream_index, out var state))
                {
                    // PGS 通常没有包时长，显示与清屏由显示集合决定。
                    // 起点状态已补齐，此处原样复制后续集合，不能当作零时长文字字幕丢弃。
                    if (pts < item.Start)
                        continue;
                    if (PgsSubtitlePacket.Decompress(new ReadOnlySpan<byte>(raw->data, raw->size)) is { } decoded)
                    {
                        using var replacement = new Packet();
                        AVPacket* target = replacement;
                        FFmpegException.Check(FF.av_new_packet(target, decoded.Length));
                        FFmpegException.Check(FF.av_packet_copy_props(target, raw));
                        decoded.CopyTo(new Span<byte>(target->data, target->size));
                        FF.av_packet_unref(raw);
                        FF.av_packet_move_ref(raw, target);
                    }
                    state.State.Read(new ReadOnlySpan<byte>(raw->data, raw->size));
                    raw->dts = raw->pts;
                    if (!toEof && end > item.End)
                        raw->duration = Ticks(item.End - pts, stream->time_base);
                }
                else
                {
                    if (end <= item.Start)
                        continue;
                    var clippedStart = Math.Max(pts, item.Start);
                    var clippedEnd = toEof ? end : Math.Min(end, item.End);
                    if (stream->codecpar->codec_id is AVCodecID.AV_CODEC_ID_ASS or AVCodecID.AV_CODEC_ID_SSA &&
                        (clippedStart > pts || clippedEnd < end) && HasTimedAssEffects(raw))
                        throw new RemuxExportException(RemuxExportError.SubtitleTiming, $"#{stream->index}");
                    raw->pts = Ticks(clippedStart, stream->time_base);
                    raw->dts = raw->pts;
                    raw->duration = Ticks(clippedEnd - clippedStart, stream->time_base);
                    if (stream->codecpar->codec_id is AVCodecID.AV_CODEC_ID_ASS or AVCodecID.AV_CODEC_ID_SSA)
                        RewriteAssReadOrder(raw, subtitleOrder[index]++);
                }
            }
            else
            {
                if (pts < item.Start) continue;
                if (!toEof && pts >= item.End)
                {
                    finished[index] = true;
                    remainingStreams--;
                    if (item.Segment.Settings.Disc is null)
                        stream->discard = AVDiscard.AVDISCARD_ALL;
                    continue;
                }
            }
            var presentationDuration = Math.Max(0, Seconds(raw->duration, stream->time_base));
            if (toEof && presentationDuration == 0 && type == AVMediaType.AVMEDIA_TYPE_VIDEO)
            {
                if (stream->avg_frame_rate.num <= 0 || stream->avg_frame_rate.den <= 0)
                    throw new RemuxExportException(RemuxExportError.MissingTimestamp, $"#{stream->index}");
                presentationDuration = stream->avg_frame_rate.den / (double)stream->avg_frame_rate.num;
            }
            actualEnd = Math.Max(actualEnd, Seconds(raw->pts, stream->time_base) + presentationDuration);
            var timeBase = output->streams[index]->time_base;
            raw->pts = Ticks(Seconds(raw->pts, stream->time_base) - item.Start + offset, timeBase);
            raw->dts = Ticks(Seconds(raw->dts, stream->time_base) - item.Start + offset, timeBase);
            raw->duration = Ticks(Seconds(raw->duration, stream->time_base), timeBase);
            if (type == AVMediaType.AVMEDIA_TYPE_VIDEO && previousDts[index] != FF.AV_NOPTS_VALUE)
            {
                var firstDts = Ticks(Seconds(sourceStartDts[index], stream->time_base) - item.Start + offset, timeBase);
                var splice = Ticks(offset, timeBase);
                if (firstDts <= previousDts[index] && raw->dts < splice)
                {
                    // 不同帧率或重排序延迟会让下一段预滚与上一段 DTS 重叠。
                    // 将这段预滚压入拼接前的可用间隔，保留 PTS 和压缩包内容。
                    var firstAvailable = previousDts[index] + 1;
                    if (firstAvailable >= splice)
                        throw new RemuxExportException(RemuxExportError.NonMonotonicTimestamp, $"#{stream->index}");
                    raw->dts = Math.Max(lastDts[index] + 1, firstAvailable + (long)Math.Floor(
                        (raw->dts - firstDts) / (double)(splice - firstDts) * (splice - firstAvailable)));
                    if (raw->dts >= splice || raw->dts > raw->pts)
                        throw new RemuxExportException(RemuxExportError.NonMonotonicTimestamp, $"#{stream->index}");
                }
            }
            if (type == AVMediaType.AVMEDIA_TYPE_VIDEO)
            {
                if (initialCompositionOffsets[index] == FF.AV_NOPTS_VALUE)
                    initialCompositionOffsets[index] = raw->pts - raw->dts;
                // MP4 的媒体时长按 DTS 计算。末端重排序延迟增大时，需同步推进终点 DTS，
                // 否则文件报告的时长可能短于截止帧的播放时间；PTS 和帧显示时长保持不变。
                if (isEndFrame && item.Segment.Settings.Container == "mp4")
                    raw->dts = Math.Max(raw->dts, raw->pts - initialCompositionOffsets[index]);
            }
            if (lastDts[index] != FF.AV_NOPTS_VALUE && (raw->dts < lastDts[index] ||
                raw->dts == lastDts[index] && type is AVMediaType.AVMEDIA_TYPE_VIDEO or AVMediaType.AVMEDIA_TYPE_AUDIO))
                throw new RemuxExportException(RemuxExportError.NonMonotonicTimestamp, $"#{stream->index}");
            lastDts[index] = raw->dts;
            raw->stream_index = index;
            raw->pos = -1;
            var size = raw->size;
            FFmpegException.Check(FF.av_interleaved_write_frame(output, raw));
            packetsWritten[index]++;
            if (isEndFrame && discMpeg2)
                endFrameWritten[index] = true;
            if (isEndFrame && !discMpeg2)
            {
                finished[index] = true;
                remainingStreams--;
                if (item.Segment.Settings.Disc is null)
                    stream->discard = AVDiscard.AVDISCARD_ALL;
            }
            report(Math.Clamp(pts - item.Start, 0, item.End - item.Start), size);
        }
        for (var i = 0; i < item.Streams.Length; i++)
        {
            var stream = rawInput->streams[item.Streams[i]];
            if (stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO &&
                (stream->disposition & 1024) == 0 && packetsWritten[i] == 0)
                throw new RemuxExportException(RemuxExportError.NoPackets, $"#{stream->index}");
        }
        if (!toEof)
            foreach (var (streamIndex, state) in pgs)
                WritePgsPacket(output, map[streamIndex], state.State.CreateClear(),
                    offset + item.End - item.Start, lastDts);
        return toEof ? actualEnd : item.End;
    }

    private sealed record PgsPreroll(PgsSubtitleState State, byte[] Epoch);

    private static Dictionary<int, PgsPreroll> ReadPgsPreroll(PreparedSegment item, CancellationToken token)
    {
        var indices = item.Streams.Where(index =>
            item.Input.Streams[index].Codecpar?.CodecId == AVCodecID.AV_CODEC_ID_HDMV_PGS_SUBTITLE).ToArray();
        if (indices.Length == 0)
            return [];
        // 优先回看附近的显示集合；缺少对象或长时间没有字幕时扩大范围，
        // 直到取得完整状态，不能把“附近没有字幕包”误判为已清屏。
        var lookback = SubtitleReadDefaults.LookbackSeconds;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var start = Math.Max(item.Segment.Settings.TimelineOriginSeconds, item.Start - lookback);
            var fromBeginning = start <= item.Segment.Settings.TimelineOriginSeconds;
            using var input = OpenInput(item.Segment, token);
            if (item.Segment.Settings.Disc is not null)
                input.LoadStreamInfo();
            AVFormatContext* raw = input;
            if (raw->nb_streams != item.Input.Streams.Count)
                throw new RemuxExportException(RemuxExportError.SourceChanged);
            var states = indices.ToDictionary(index => index, _ => new PgsSubtitleState());
            var timeline = item.Segment.Settings.Disc is { Kind: DiscKind.BluRay } disc ? disc.CreateTimeline() : null;
            var activeSegment = timeline?.FindByRelativeTime((long)Math.Round(start * 90_000));
            var knownBeginning = fromBeginning;
            for (var i = 0; i < raw->nb_streams; i++)
                if (!states.ContainsKey(i) && (timeline is null || i != item.Segment.Settings.ReferenceVideoIndex))
                    raw->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
            if (!fromBeginning)
            {
                var video = raw->streams[item.Segment.Settings.ReferenceVideoIndex];
                input.SeekFrame(Ticks(start, video->time_base), video->index);
            }
            var remaining = indices.ToHashSet();
            foreach (var packet in input.ReadPackets())
            {
                token.ThrowIfCancellationRequested();
                AVPacket* data = packet;
                if (timeline?.FindByByte(data->pos) is { } segment && !ReferenceEquals(activeSegment, segment) &&
                    segment.RelativeStart90k / 90_000.0 < item.Start)
                {
                    if (!timeline.Continues(activeSegment, segment))
                    {
                        foreach (var index in indices)
                            states[index] = new PgsSubtitleState();
                        knownBeginning = true;
                    }
                    activeSegment = segment;
                }
                if (timeline is not null && data->stream_index == item.Segment.Settings.ReferenceVideoIndex &&
                    data->pts != FF.AV_NOPTS_VALUE && Seconds(data->pts, raw->streams[data->stream_index]->time_base) > item.Start + 2)
                    break;
                if (!remaining.Contains(data->stream_index))
                    continue;
                if (data->pts == FF.AV_NOPTS_VALUE)
                    throw new RemuxExportException(RemuxExportError.MissingTimestamp, $"#{data->stream_index}");
                if (Seconds(data->pts, raw->streams[data->stream_index]->time_base) >= item.Start)
                {
                    remaining.Remove(data->stream_index);
                    if (remaining.Count == 0)
                        break;
                    continue;
                }
                if (timeline?.FindByByte(data->pos) is { } packetSegment)
                {
                    var ticks = Seconds(data->pts, raw->streams[data->stream_index]->time_base) * 90_000;
                    if (ticks < packetSegment.RelativeStart90k || ticks >= packetSegment.RelativeStart90k + packetSegment.OutPts90k - packetSegment.InPts90k)
                        continue;
                }
                states[data->stream_index].Read(new ReadOnlySpan<byte>(data->data, data->size));
            }
            var result = new Dictionary<int, PgsPreroll>();
            foreach (var (index, state) in states)
                if (state.CreateEpoch(knownBeginning) is { } epoch)
                    result[index] = new PgsPreroll(state, epoch);
            if (result.Count == indices.Length)
                return result;
            if (fromBeginning)
                throw new RemuxExportException(RemuxExportError.UnsupportedTrack, "PGS preroll");
            lookback *= 4;
        }
    }

    private static void WritePgsPacket(AVFormatContext* output, int index, byte[] bytes, double time, long[] lastDts)
    {
        if (bytes.Length == 0)
            return;
        using var packet = new Packet();
        AVPacket* raw = packet;
        FFmpegException.Check(FF.av_new_packet(raw, bytes.Length));
        bytes.CopyTo(new Span<byte>(raw->data, raw->size));
        raw->stream_index = index;
        raw->pts = raw->dts = Ticks(time, output->streams[index]->time_base);
        raw->pos = -1;
        raw->flags = 1;
        if (lastDts[index] != FF.AV_NOPTS_VALUE && raw->dts < lastDts[index])
            throw new RemuxExportException(RemuxExportError.NonMonotonicTimestamp, $"#{index}");
        lastDts[index] = raw->dts;
        FFmpegException.Check(FF.av_interleaved_write_frame(output, raw));
    }

    private static bool HasTimedAssEffects(AVPacket* packet)
    {
        var fields = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(packet->data, packet->size)).Split(',', 9);
        if (fields.Length != 9) throw new RemuxExportException(RemuxExportError.UnsupportedTrack, "ASS event");
        // 滚动、横幅效果及时序覆盖标签均相对于原始字幕事件的起止时间。
        // 只改时间戳会改变这些效果的时序。
        return !string.IsNullOrWhiteSpace(fields[7]) || Regex.IsMatch(fields[8],
            @"\\(?:t|move|fad|fade|k[oft]?)(?=[(\d+\-\s])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static void RewriteAssReadOrder(AVPacket* packet, long order)
    {
        var data = new ReadOnlySpan<byte>(packet->data, packet->size);
        var comma = data.IndexOf((byte)',');
        if (comma <= 0) throw new RemuxExportException(RemuxExportError.UnsupportedTrack, "ASS ReadOrder");
        var prefix = Encoding.ASCII.GetBytes(order.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var replacement = new Packet();
        AVPacket* raw = replacement;
        FFmpegException.Check(FF.av_new_packet(raw, prefix.Length + data.Length - comma));
        FFmpegException.Check(FF.av_packet_copy_props(raw, packet));
        prefix.CopyTo(new Span<byte>(raw->data, prefix.Length));
        data[comma..].CopyTo(new Span<byte>(raw->data + prefix.Length, data.Length - comma));
        FF.av_packet_unref(packet);
        FF.av_packet_move_ref(packet, raw);
    }

    private static FormatContext OpenInput(RemuxExportSegment segment, CancellationToken token) =>
        segment.Settings.Disc is { } disc
            ? InterruptibleInputFormatContext.Open(disc, () => token.IsCancellationRequested,
                disc.Kind == DiscKind.Dvd ? Math.Max(0, segment.StartSeconds - SubtitleReadDefaults.LookbackSeconds) : null)
            : InterruptibleInputFormatContext.Open(segment.SourcePath, () => token.IsCancellationRequested);

    private static SourceStamp[] CaptureSource(string path, DiscInput? disc)
    {
        var files = disc is not null && Directory.Exists(path)
            ? Directory.EnumerateFiles(Path.Combine(path, disc.Kind == DiscKind.Dvd ? "VIDEO_TS" : "BDMV"), "*", new EnumerationOptions
            {
                RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint
            }).Where(file => Path.GetExtension(file).ToLowerInvariant() is
                ".ifo" or ".bup" or ".vob" or ".bdmv" or ".mpls" or ".clpi" or ".m2ts" or ".ssif")
            : [path];
        return files.OrderBy(file => file, StringComparer.Ordinal).Select(file =>
        {
            var info = new FileInfo(file);
            return new SourceStamp(info.FullName, info.Length, info.LastWriteTimeUtc);
        }).ToArray();
    }

    private static void EnsureUnchanged(PreparedSegment item)
    {
        if (!item.Source.SequenceEqual(CaptureSource(item.Segment.SourcePath, item.Segment.Settings.Disc)))
            throw new RemuxExportException(RemuxExportError.SourceChanged, item.Segment.SourcePath);
    }

    private static double Seconds(long value, AVRational timeBase) => value * (double)timeBase.num / timeBase.den;
    private static long Ticks(double seconds, AVRational timeBase) => checked((long)Math.Round(seconds * timeBase.den / timeBase.num));
}
