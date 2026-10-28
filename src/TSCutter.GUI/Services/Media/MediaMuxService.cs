using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Media;
using TSCutter.GUI.Utils.Files;
using TSCutter.GUI.Utils.Media;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Services.Media;

/// <summary>多输入混流；音视频复制，必要时转换文字字幕，以有界的待写包按解码时间交错输出。</summary>
internal sealed unsafe class MediaMuxService
{
    public static IReadOnlyList<string> Containers { get; } = ["mkv", "mp4", "ts", "m2ts"];
    private static readonly AVRational Microseconds = new() { num = 1, den = FF.AV_TIME_BASE };

    public Task<MediaMuxSource> ProbeAsync(string path, CancellationToken token = default) =>
        Task.Run(() => Probe(path, token), token);

    public Task ExportAsync(MediaMuxRequest request, IProgress<MediaMuxProgress>? progress = null,
        CancellationToken token = default) => Task.Run(() => Export(request, progress, token), token);

    private static MediaMuxSource Probe(string path, CancellationToken token)
    {
        path = Path.GetFullPath(path);
        var stamp = new FileInfo(path);
        var size = stamp.Length;
        var modified = stamp.LastWriteTimeUtc;
        var deadline = new MediaReadDeadline(TimeSpan.FromSeconds(30), token);
        try
        {
            using var input = InterruptibleInputFormatContext.Open(path, () => deadline.ShouldInterrupt);
            input.LoadStreamInfo();
            deadline.ThrowIfInterrupted();
            var streams = new List<MediaMuxStream>();
            foreach (var stream in input.Streams)
            {
                var codec = stream.Codecpar;
                if (codec is null || codec.CodecType is not (AVMediaType.AVMEDIA_TYPE_VIDEO or
                    AVMediaType.AVMEDIA_TYPE_AUDIO or AVMediaType.AVMEDIA_TYPE_SUBTITLE or AVMediaType.AVMEDIA_TYPE_ATTACHMENT))
                    continue;
                AVStream* raw = stream;
                stream.Metadata.TryGetValue("language", out var language);
                stream.Metadata.TryGetValue("title", out var title);
                if (title.Length == 0)
                    stream.Metadata.TryGetValue("name", out title);
                var parameters = codec.CodecType switch
                {
                    AVMediaType.AVMEDIA_TYPE_VIDEO => $"{raw->codecpar->width} × {raw->codecpar->height}",
                    AVMediaType.AVMEDIA_TYPE_AUDIO => $"{raw->codecpar->sample_rate} Hz · {raw->codecpar->ch_layout.nb_channels} ch",
                    _ => ""
                };
                var conversions = new Dictionary<string, AVCodecID>();
                var containers = new List<string>();
                foreach (var container in Containers)
                {
                    if (Supports(container, raw))
                        containers.Add(container);
                    else if (SubtitleTarget(container, raw) is { } target)
                    {
                        conversions.Add(container, target);
                        if (CanConvert(raw->codecpar->codec_id, target))
                            containers.Add(container);
                    }
                }
                streams.Add(new(stream.Index, codec.CodecType, codec.CodecId, codec.CodecName,
                    parameters, language, title, raw->disposition, containers) { SubtitleConversions = conversions });
            }
            stamp.Refresh();
            if (!stamp.Exists || stamp.Length != size || stamp.LastWriteTimeUtc != modified)
                throw new MediaMuxException(MediaMuxError.SourceChanged, path);
            var format = input.InputFormat?.Name ?? "";
            var label = format.Split(',').Contains("mov") ? "MP4 / MOV" : format.Split(',').Contains("matroska") ? "Matroska" :
                format == "mpegts" ? "MPEG-TS" : format.ToUpperInvariant();
            return new(path, size, modified, label, Math.Max(0, input.Duration / 1_000_000d), streams);
        }
        catch (FFmpegException) when (deadline.ShouldInterrupt)
        {
            deadline.ThrowIfInterrupted();
            throw;
        }
    }

    private static string FormatName(string container) => container switch
    {
        "mkv" => "matroska", "ts" or "m2ts" => "mpegts", "mp4" => "mp4",
        _ => throw new MediaMuxException(MediaMuxError.InvalidRequest)
    };

    private static bool Supports(string container, AVStream* stream)
    {
        if (stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_ATTACHMENT)
            return container == "mkv";
        if ((stream->disposition & 1024) != 0 && container is "ts" or "m2ts")
            return false;
        // 蓝光模式只能自动分配 PGS／TextST 字幕 PID，DVB 等字幕不能沿用普通 TS 的判断。
        if (container == "m2ts" && stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_SUBTITLE &&
            stream->codecpar->codec_id is not (AVCodecID.AV_CODEC_ID_HDMV_PGS_SUBTITLE or AVCodecID.AV_CODEC_ID_HDMV_TEXT_SUBTITLE))
            return false;
        var format = FF.av_guess_format(FormatName(container), null!, null!);
        if (format == null)
            throw FFmpegException.FromErrorCode(FF.AVERROR_INVALIDDATA);
        // 使用正常兼容级别，避免 Matroska 的非标准 VFW/ACM 回退误报支持未知编码。
        var support = FF.avformat_query_codec(format, stream->codecpar->codec_id, 0);
        // MPEG-TS 没有完整的 query_codec 回调，负值表示未知，不能误判为拒绝 H.264 等标准轨道。
        if (support >= 0 || container is not ("ts" or "m2ts"))
            return support > 0;
        return stream->codecpar->codec_id is AVCodecID.AV_CODEC_ID_H264 or AVCodecID.AV_CODEC_ID_HEVC or
                AVCodecID.AV_CODEC_ID_MPEG1VIDEO or AVCodecID.AV_CODEC_ID_MPEG2VIDEO or AVCodecID.AV_CODEC_ID_MPEG4 or
                AVCodecID.AV_CODEC_ID_VC1 or AVCodecID.AV_CODEC_ID_CAVS or AVCodecID.AV_CODEC_ID_AVS2 or AVCodecID.AV_CODEC_ID_AVS3 or
                AVCodecID.AV_CODEC_ID_AAC or AVCodecID.AV_CODEC_ID_AAC_LATM or AVCodecID.AV_CODEC_ID_MP1 or
                AVCodecID.AV_CODEC_ID_MP2 or AVCodecID.AV_CODEC_ID_MP3 or AVCodecID.AV_CODEC_ID_AC3 or
                AVCodecID.AV_CODEC_ID_EAC3 or AVCodecID.AV_CODEC_ID_DTS or AVCodecID.AV_CODEC_ID_TRUEHD or
                AVCodecID.AV_CODEC_ID_PCM_BLURAY or AVCodecID.AV_CODEC_ID_OPUS or
                AVCodecID.AV_CODEC_ID_HDMV_PGS_SUBTITLE or AVCodecID.AV_CODEC_ID_HDMV_TEXT_SUBTITLE or
                AVCodecID.AV_CODEC_ID_DVB_SUBTITLE or AVCodecID.AV_CODEC_ID_DVB_TELETEXT ||
            container == "ts" && new CodecParameters(stream->codecpar) is { IsAv3a: true, HasReliableCodecParameters: true };
    }

    private static AVCodecID? SubtitleTarget(string container, AVStream* stream)
    {
        if (stream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_SUBTITLE)
            return null;
        var descriptor = FF.avcodec_descriptor_get(stream->codecpar->codec_id);
        // 只进行文字字幕之间的转换；图形字幕需要渲染或 OCR，不能直接转成文字。
        if (descriptor == null || (descriptor->props & FF.CodecPropTextSubtitle) == 0)
            return null;
        return container switch
        {
            "mp4" => AVCodecID.AV_CODEC_ID_MOV_TEXT,
            "mkv" => AVCodecID.AV_CODEC_ID_SUBRIP,
            _ => null
        };
    }

    private static bool CanConvert(AVCodecID source, AVCodecID target) =>
        FF.avcodec_find_decoder(source) != null && FF.avcodec_find_encoder(target) != null;

    public static void Validate(MediaMuxRequest request)
    {
        if (!Containers.Contains(request.Container) || request.Tracks.Count is 0 or > 256 ||
            string.IsNullOrWhiteSpace(request.OutputPath) ||
            !Path.GetExtension(request.OutputPath).Equals("." + request.Container, StringComparison.OrdinalIgnoreCase))
            throw new MediaMuxException(MediaMuxError.InvalidRequest);
        var selectedStreams = new Dictionary<string, HashSet<int>>(MediaFileUtil.PathComparer);
        foreach (var track in request.Tracks)
        {
            var sourcePath = MediaFileUtil.CanonicalPath(track.Source.Path);
            if (!selectedStreams.TryGetValue(sourcePath, out var indices))
                selectedStreams.Add(sourcePath, indices = []);
            if (!indices.Add(track.Stream.Index))
                throw new MediaMuxException(MediaMuxError.InvalidRequest);
            if (MediaFileUtil.ConflictsWithSource(request.OutputPath, track.Source.Path))
                throw new MediaMuxException(MediaMuxError.SameFile, track.Source.Path);
            if (track.Stream.Index < 0 || track.OffsetMilliseconds is < -86_400_000 or > 86_400_000 ||
                track.Language.Length > 0 && (track.Language.Length != 3 || track.Language.Any(c => c is not (>= 'a' and <= 'z'))))
                throw new MediaMuxException(MediaMuxError.InvalidRequest);
            if (!track.Stream.Containers.Contains(request.Container))
                throw new MediaMuxException(MediaMuxError.UnsupportedTrack, $"{Path.GetFileName(track.Source.Path)} #{track.Stream.Index}: {track.Stream.Codec}");
            if (!track.Stream.IsTimed && track.OffsetMilliseconds != 0)
                throw new MediaMuxException(MediaMuxError.InvalidRequest);
        }
        if (request.Tracks.All(track => !track.Stream.IsTimed))
            throw new MediaMuxException(MediaMuxError.NoPackets);
        if (selectedStreams.Count > 32)
            throw new MediaMuxException(MediaMuxError.InvalidRequest);
    }

    private static void EnsureUnchanged(MediaMuxSource source)
    {
        var file = new FileInfo(source.Path);
        if (!file.Exists || file.Length != source.Size || file.LastWriteTimeUtc != source.Modified)
            throw new MediaMuxException(MediaMuxError.SourceChanged, source.Path);
    }

    private static void Export(MediaMuxRequest request, IProgress<MediaMuxProgress>? progress, CancellationToken token)
    {
        Validate(request);
        token.ThrowIfCancellationRequested();
        using var logs = new FFmpegLogCapture();
        var operation = MediaMuxOperation.OpenInput;
        var context = "";
        var cursors = new Dictionary<string, InputCursor>(MediaFileUtil.PathComparer);
        AVFormatContext* output = null;
        AVIOInterruptCB_callback interrupt = _ => token.IsCancellationRequested ? 1 : 0;
        var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(request.OutputPath))!, $".tscutter-mux-{Guid.NewGuid():N}.tmp");
        try
        {
            foreach (var group in request.Tracks.GroupBy(track => MediaFileUtil.CanonicalPath(track.Source.Path), MediaFileUtil.PathComparer))
            {
                var source = group.First().Source;
                EnsureUnchanged(source);
                context = source.Name;
                logs.Clear();
                var cursor = new InputCursor(source, token);
                cursors.Add(group.Key, cursor);
                cursor.Open();
            }
            logs.Clear();
            operation = MediaMuxOperation.CreateOutput;
            context = "";
            FFmpegException.Check(FF.avformat_alloc_output_context2(&output, null, FormatName(request.Container), temporary));
            if (output == null)
                throw new OutOfMemoryException();
            output->interrupt_callback = new AVIOInterruptCB { callback = interrupt };
            output->strict_std_compliance = -1;
            output->max_interleave_delta = FF.AV_TIME_BASE;
            // 与 FFmpeg -itsoffset 一样只移动所选轨道，负时间戳由封装器按默认规则处理。
            output->avoid_negative_ts = -1;
            var outputIndex = 0;
            foreach (var track in request.Tracks)
            {
                logs.Clear();
                operation = MediaMuxOperation.PrepareTracks;
                context = $"{track.Source.Name} #{track.Stream.Index} · {track.Stream.Codec}";
                var cursor = cursors[MediaFileUtil.CanonicalPath(track.Source.Path)];
                AVFormatContext* input = cursor.Input;
                if (track.Stream.Index >= input->nb_streams)
                    throw new MediaMuxException(MediaMuxError.SourceChanged, track.Source.Path);
                var source = input->streams[track.Stream.Index];
                var canCopy = Supports(request.Container, source);
                var conversion = canCopy ? null : SubtitleTarget(request.Container, source);
                if (source->codecpar->codec_id != track.Stream.CodecId ||
                    !canCopy && (conversion is null || !CanConvert(source->codecpar->codec_id, conversion.Value)))
                    throw new MediaMuxException(MediaMuxError.UnsupportedTrack, track.Stream.Codec);
                var stream = FF.avformat_new_stream(output, null);
                if (stream == null)
                    throw new OutOfMemoryException();
                var shift = track.OffsetMilliseconds / 1000m - (decimal)cursor.Origin;
                var state = new OutputTrack(track, outputIndex++, (double)shift);
                cursor.Tracks.Add(track.Stream.Index, state);
                if (conversion is { } target)
                    state.Subtitle = new MediaMuxSubtitleTranscoder(source, stream, target);
                else
                    FFmpegException.Check(FF.avcodec_parameters_copy(stream->codecpar, source->codecpar));
                stream->codecpar->codec_tag = RemuxCodecTag.Select(request.Container, source->codecpar);
                if (request.Container == "mp4" && source->codecpar->codec_id == AVCodecID.AV_CODEC_ID_HEVC && !track.Stream.IsCover)
                    state.Hevc = HevcMp4PacketConverter.Create(stream->codecpar);
                if (state.Subtitle is null)
                    stream->time_base = source->time_base;
                stream->avg_frame_rate = source->avg_frame_rate;
                stream->sample_aspect_ratio = source->sample_aspect_ratio;
                stream->disposition = (source->disposition & ~(1 | 64)) | (track.Default ? 1 : 0) | (track.Forced ? 64 : 0);
                FFmpegException.Check(FF.av_dict_copy(&stream->metadata, source->metadata, 0));
                FFmpegException.Check(FF.av_dict_set(&stream->metadata, "language", track.Language.Length == 0 ? null! : track.Language, 0));
                FFmpegException.Check(FF.av_dict_set(&stream->metadata, "title", track.Title.Length == 0 ? null! : track.Title, 0));
                // 旧容器的统计和轨道名称不能覆盖新的时长及名称。
                foreach (var key in new[] { "language_ietf", "language-ietf", "name", "DURATION", "DURATION-eng", "NUMBER_OF_BYTES", "NUMBER_OF_FRAMES", "BPS", "BPS-eng" })
                    FFmpegException.Check(FF.av_dict_set(&stream->metadata, key, null!, 0));
                if (track.Stream.IsCover)
                    FFmpegException.Check(FF.av_packet_ref(&stream->attached_pic, &source->attached_pic));
            }
            var shortestEnd = double.PositiveInfinity;
            if (request.Shortest)
            {
                operation = MediaMuxOperation.OpenInput;
                var inputs = cursors.Values.Select(cursor =>
                    (Cursor: cursor, Tracks: cursor.Tracks.Values.Select(state => state.Track).ToArray())).ToArray();
                // 先汇总全部输入的已知终点，缺时长的输入即使排在前面，也能利用此上限提前结束预读。
                foreach (var input in inputs)
                    shortestEnd = Math.Min(shortestEnd, MediaMuxShortest.FindKnownEnd(input.Cursor.Input,
                        input.Tracks, input.Cursor.Origin, request.ShortestIncludesSubtitles));
                foreach (var input in inputs)
                {
                    context = input.Cursor.Source.Name;
                    shortestEnd = Math.Min(shortestEnd, MediaMuxShortest.FindEnd(input.Cursor.Input,
                        input.Tracks, input.Cursor.Origin, request.ShortestIncludesSubtitles, shortestEnd, token));
                }
                if (shortestEnd <= 0 && request.Container == "mp4")
                    throw new MediaMuxException(MediaMuxError.NoPackets);
            }
            logs.Clear();
            operation = MediaMuxOperation.OpenInput;
            foreach (var cursor in cursors.Values)
            {
                context = cursor.Source.Name;
                logs.Clear();
                cursor.Initialize();
                cursor.Advance();
            }
            logs.Clear();
            operation = MediaMuxOperation.CreateOutput;
            context = "";
            using (new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            FFmpegException.Check(FF.avio_open2(&output->pb, temporary, 2, &output->interrupt_callback, null));
            AVDictionary* options = null;
            try
            {
                if (request.Container == "mp4")
                {
                    FFmpegException.Check(FF.av_dict_set(&options, "use_editlist", "1", 0));
                    if (request.FastStart)
                        FFmpegException.Check(FF.av_dict_set(&options, "movflags", "+faststart", 0));
                }
                if (request.Container == "m2ts")
                    FFmpegException.Check(FF.av_dict_set(&options, "mpegts_m2ts_mode", "1", 0));
                if (request.Container is "ts" or "m2ts")
                    FFmpegException.Check(FF.av_dict_set(&options, "mpegts_copyts", "1", 0));
                logs.Clear();
                operation = MediaMuxOperation.WriteHeader;
                FFmpegException.Check(FF.avformat_write_header(output, &options));
            }
            finally { FF.av_dict_free(&options); }
            progress?.Report(new(0, 0, 0));
            token.ThrowIfCancellationRequested();
            foreach (var state in cursors.Values.SelectMany(cursor => cursor.Tracks.Values))
            {
                var timeBase = output->streams[state.OutputIndex]->time_base;
                state.DeltaTicks = Rescale((long)Math.Round(state.Shift * FF.AV_TIME_BASE), Microseconds, timeBase);
                if (double.IsFinite(shortestEnd))
                    state.EndTicks = Rescale(checked((long)Math.Round(shortestEnd * FF.AV_TIME_BASE)), Microseconds, timeBase);
            }
            logs.Clear();
            operation = MediaMuxOperation.WritePackets;
            foreach (var cursor in cursors.Values)
            foreach (var state in cursor.Tracks.Values.Where(item => item.Track.Stream.IsCover))
            {
                context = state.Label;
                using var packet = new Packet();
                AVPacket* raw = packet;
                AVFormatContext* input = cursor.Input;
                FFmpegException.Check(FF.av_packet_ref(raw, &input->streams[state.Track.Stream.Index]->attached_pic));
                raw->stream_index = state.OutputIndex;
                raw->pts = raw->dts = 0;
                FFmpegException.Check(FF.av_interleaved_write_frame(output, raw));
            }
            var timer = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            long bytes = 0;
            long packets = 0;
            var duration = cursors.Values.SelectMany(cursor => cursor.Tracks.Values.Where(state => state.Track.Stream.IsTimed).Select(state =>
                cursor.Source.Duration + state.Shift + cursor.Origin)).DefaultIfEmpty(0).Max();
            duration = Math.Min(duration, shortestEnd);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                InputCursor? cursor = null;
                foreach (var candidate in cursors.Values)
                {
                    if (candidate.HasPacket && (cursor is null || candidate.PacketTime < cursor.PacketTime))
                        cursor = candidate;
                }
                if (cursor is null)
                    break;
                AVPacket* packet = cursor.Packet;
                var state = cursor.Tracks[packet->stream_index];
                var stream = output->streams[state.OutputIndex];
                // 按 DTS 截止，保留截止点前 B 帧所需的前向参考包，不按重排后的 PTS 丢参考帧。
                if (state.EndTicks != long.MaxValue &&
                    checked(Rescale(packet->dts, cursor.TimeBase, stream->time_base) + state.DeltaTicks) >= state.EndTicks)
                {
                    cursor.FinishTrack(state);
                    cursor.Advance();
                    continue;
                }
                var time = packet->pts == FF.AV_NOPTS_VALUE ? packet->dts : packet->pts;
                var seconds = Seconds(time, cursor.TimeBase) + state.Shift;
                var packetSize = packet->size;
                context = state.Label;
                if (state.Subtitle is { } subtitle)
                {
                    logs.Clear();
                    operation = MediaMuxOperation.ConvertSubtitle;
                    packets += subtitle.Write(packet, output, state.OutputIndex, state.DeltaTicks, state.EndTicks);
                }
                else
                {
                    logs.Clear();
                    operation = MediaMuxOperation.WritePackets;
                    packet->pts = Rescale(packet->pts, cursor.TimeBase, stream->time_base);
                    packet->dts = Rescale(packet->dts, cursor.TimeBase, stream->time_base);
                    packet->duration = Rescale(packet->duration, cursor.TimeBase, stream->time_base);
                    var delta = state.DeltaTicks;
                    if (packet->pts != FF.AV_NOPTS_VALUE)
                        packet->pts = checked(packet->pts + delta);
                    if (packet->dts != FF.AV_NOPTS_VALUE)
                        packet->dts = checked(packet->dts + delta);
                    if (state.Track.Stream.Type != AVMediaType.AVMEDIA_TYPE_SUBTITLE ||
                        TrimSubtitleToOutputRange(packet, request.Container == "mp4", state.EndTicks))
                    {
                        if (packet->dts != FF.AV_NOPTS_VALUE && state.LastDts != FF.AV_NOPTS_VALUE && packet->dts < state.LastDts)
                            throw new MediaMuxException(MediaMuxError.NonMonotonicTimestamp, state.Label);
                        state.LastDts = packet->dts;
                        packet->stream_index = state.OutputIndex;
                        packet->pos = -1;
                        FFmpegException.Check(FF.av_interleaved_write_frame(output, state.Hevc is { } hevc ? hevc.Convert(packet) : packet));
                        packets++;
                    }
                }
                bytes += packetSize;
                if (timer.Elapsed - lastReport > TimeSpan.FromMilliseconds(100))
                {
                    lastReport = timer.Elapsed;
                    progress?.Report(new(duration > 0 ? Math.Clamp(seconds / duration * 100, 0, 99.9) : 0,
                        bytes, bytes / Math.Max(timer.Elapsed.TotalSeconds, 0.001)));
                }
                logs.Clear();
                operation = MediaMuxOperation.OpenInput;
                context = cursor.Source.Name;
                cursor.Advance();
            }
            if (packets == 0)
                throw new MediaMuxException(MediaMuxError.NoPackets);
            token.ThrowIfCancellationRequested();
            logs.Clear();
            operation = MediaMuxOperation.FinalizeOutput;
            context = "";
            FFmpegException.Check(FF.av_write_trailer(output));
            FFmpegException.Check(FF.avio_closep(&output->pb));
            foreach (var cursor in cursors.Values)
                EnsureUnchanged(cursor.Source);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, request.OutputPath, overwrite: true);
            progress?.Report(new(100, bytes, bytes / Math.Max(timer.Elapsed.TotalSeconds, 0.001)));
        }
        catch (FFmpegException) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
        catch (FFmpegException exception)
        {
            throw new MediaMuxFailureException(operation, request.Container, context, logs.Messages, exception);
        }
        finally
        {
            if (output != null)
            {
                if (output->pb != null)
                    FF.avio_closep(&output->pb);
                FF.avformat_free_context(output);
            }
            foreach (var cursor in cursors.Values)
                cursor.Dispose();
            if (File.Exists(temporary))
                File.Delete(temporary);
            GC.KeepAlive(interrupt);
        }
    }

    private sealed class OutputTrack(MediaMuxTrack track, int outputIndex, double shift)
    {
        public MediaMuxTrack Track { get; } = track;
        public int OutputIndex { get; } = outputIndex;
        public double Shift { get; } = shift;
        public MediaMuxSubtitleTranscoder? Subtitle { get; set; }
        public HevcMp4PacketConverter? Hevc { get; set; }
        public long DeltaTicks { get; set; }
        public long EndTicks { get; set; } = long.MaxValue;
        public bool Finished { get; set; }
        public long LastDts { get; set; } = FF.AV_NOPTS_VALUE;
        public long NextDts { get; set; } = FF.AV_NOPTS_VALUE;
        public string Label { get; } = $"{track.Source.Name} #{track.Stream.Index} · {track.Stream.Codec}";
    }

    private sealed class InputCursor(MediaMuxSource source, CancellationToken token) : IDisposable
    {
        public MediaMuxSource Source { get; } = source;
        public FormatContext Input { get; private set; } = null!;
        public Dictionary<int, OutputTrack> Tracks { get; } = [];
        public Packet Packet { get; } = new();
        public bool HasPacket { get; private set; }
        public double PacketTime { get; private set; }
        public AVRational TimeBase { get; private set; }
        public double Origin { get; private set; }
        private bool hasTimedTracks;
        public bool SubtitlesOnly => Source.Streams.All(stream => stream.Type is AVMediaType.AVMEDIA_TYPE_SUBTITLE or AVMediaType.AVMEDIA_TYPE_ATTACHMENT);

        public void Open()
        {
            Input = InterruptibleInputFormatContext.Open(Source.Path, () => token.IsCancellationRequested);
            Input.LoadStreamInfo();
            AVFormatContext* raw = Input;
            // 外部字幕的首条提示可能在片中，不能把第一条字幕归零。
            Origin = SubtitlesOnly ? 0 : raw->start_time != FF.AV_NOPTS_VALUE ? raw->start_time / 1_000_000d :
                Input.Streams.Where(stream => stream.StartTime != FF.AV_NOPTS_VALUE)
                    .Select(stream => Seconds(stream.StartTime, stream.TimeBase)).DefaultIfEmpty(0).Min();
        }

        public void Initialize()
        {
            hasTimedTracks = Tracks.Values.Any(state => state.Track.Stream.IsTimed);
            AVFormatContext* input = Input;
            for (var index = 0; index < input->nb_streams; index++)
            {
                if (!Tracks.TryGetValue(index, out var state) || !state.Track.Stream.IsTimed)
                    input->streams[index]->discard = AVDiscard.AVDISCARD_ALL;
            }
        }

        public void FinishTrack(OutputTrack state)
        {
            state.Finished = true;
            AVFormatContext* input = Input;
            input->streams[state.Track.Stream.Index]->discard = AVDiscard.AVDISCARD_ALL;
            hasTimedTracks = Tracks.Values.Any(item => item.Track.Stream.IsTimed && !item.Finished);
        }

        public void Advance()
        {
            Packet.Unref();
            HasPacket = false;
            if (!hasTimedTracks)
                return;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var code = Input.ReadPacket(Packet);
                if (code == FF.AVERROR_EOF)
                    return;
                FFmpegException.Check(code);
                AVPacket* raw = Packet;
                if (!Tracks.TryGetValue(raw->stream_index, out var state) || !state.Track.Stream.IsTimed || state.Finished)
                {
                    Packet.Unref();
                    continue;
                }
                AVFormatContext* input = Input;
                var stream = input->streams[raw->stream_index];
                TimeBase = stream->time_base;
                if (raw->pts == FF.AV_NOPTS_VALUE)
                    throw new MediaMuxException(MediaMuxError.MissingTimestamp, state.Label);
                if (raw->dts == FF.AV_NOPTS_VALUE)
                {
                    if (stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        var duration = raw->duration > 0 ? raw->duration :
                            stream->avg_frame_rate.num > 0 ? Rescale(1,
                                new AVRational { num = stream->avg_frame_rate.den, den = stream->avg_frame_rate.num }, TimeBase) : 0;
                        if (duration <= 0)
                            throw new MediaMuxException(MediaMuxError.MissingTimestamp, state.Label);
                        // Matroska 可能省略首批 DTS；按探测得到的重排序深度补齐解码预滚，保留 PTS。
                        raw->dts = state.NextDts != FF.AV_NOPTS_VALUE ? state.NextDts :
                            checked(raw->pts - stream->codecpar->video_delay * duration);
                        raw->duration = duration;
                    }
                    else
                        raw->dts = raw->pts;
                }
                state.NextDts = checked(raw->dts + raw->duration);
                PacketTime = Seconds(raw->dts, TimeBase) + state.Shift;
                HasPacket = true;
                return;
            }
        }

        public void Dispose()
        {
            foreach (var state in Tracks.Values)
            {
                state.Subtitle?.Dispose();
                state.Hevc?.Dispose();
            }
            Packet.Dispose();
            Input?.Dispose();
        }
    }

    // mov_text 的负起点会干扰 MP4 字幕样本时长；零点前的提示跳过，跨零点的提示保留剩余显示时间。
    internal static bool TrimSubtitleToOutputRange(AVPacket* packet, bool trimAtZero, long end)
    {
        if (packet->pts == FF.AV_NOPTS_VALUE)
            return true;
        if (end != long.MaxValue)
        {
            if (packet->pts >= end)
                return false;
            if (packet->duration > 0)
                packet->duration = Math.Min(packet->duration, checked(end - packet->pts));
        }
        if (!trimAtZero || packet->pts >= 0)
            return true;
        if (packet->duration <= 0 || packet->pts <= -packet->duration)
            return false;
        packet->duration += packet->pts;
        packet->pts = packet->dts = 0;
        return true;
    }

    // 用整数保持时间基精度，避免长片和分数帧率经过 double 换算后产生重复 DTS。
    internal static long Rescale(long value, AVRational source, AVRational destination)
    {
        if (value == FF.AV_NOPTS_VALUE)
            return value;
        if (source.num <= 0 || source.den <= 0 || destination.num <= 0 || destination.den <= 0)
            throw new MediaMuxException(MediaMuxError.MissingTimestamp);
        var numerator = (Int128)value * source.num * destination.den;
        var denominator = (long)source.den * destination.num;
        return checked((long)(numerator >= 0 ? (numerator + denominator / 2) / denominator :
            -((-numerator + denominator / 2) / denominator)));
    }

    private static double Seconds(long value, AVRational timeBase) => value * (double)timeBase.num / timeBase.den;
}
