using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Preview;

namespace TSCutter.GUI.Utils.Media;

internal static class PreviewStreamUtil
{
    public static MediaStream ResolveVideo(FormatContext context, PreviewStreamSelection? selection)
    {
        if (selection is null)
        {
            foreach (var stream in context.Streams)
                if (stream.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_VIDEO && !stream.IsAttachedPicture)
                    return stream;
            return context.GetVideoStream();
        }

        var candidates = context.Streams.Where(stream =>
            stream.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_VIDEO);
        if (selection.ProgramId is { } programId)
        {
            var programs = context.Programs.Where(program => program.Id == programId).ToArray();
            if (programs.Length != 1)
                throw new InvalidDataException("The selected program is no longer available.");
            candidates = candidates.Where(stream => programs[0].ContainsStream(stream.Index));
        }

        var matches = candidates.Where(stream => stream.Id == selection.StreamId &&
            (context.InputFormat?.Name == "mpegts" || stream.Index == selection.StreamIndex)).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException("The selected video track is no longer available.");
        return matches[0];
    }

    public static PreviewStreamSelection GetSelection(FormatContext context, MediaStream video,
        PreviewStreamSelection? requested)
    {
        var programId = requested?.ProgramId ?? context.Programs
            .Where(program => program.ContainsStream(video.Index))
            .Select(program => (int?)program.Id).FirstOrDefault();
        return new PreviewStreamSelection(video.Index, video.Id, programId);
    }

    public static IReadOnlyList<PreviewSubtitleTrackOption> CreateSubtitleOptions(FormatContext context,
        PreviewStreamSelection? video)
    {
        var program = video?.ProgramId is { } id
            ? context.Programs.Where(p => p.Id == id).ToArray() : [];
        return context.Streams.Where(stream => stream.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_SUBTITLE &&
                (program.Length == 0 || program[0].ContainsStream(stream.Index)))
            .Select(stream =>
            {
                stream.Metadata.TryGetValue("language", out var language);
                if (!stream.Metadata.TryGetValue("title", out var title) || string.IsNullOrWhiteSpace(title))
                    stream.Metadata.TryGetValue("name", out title);
                if (string.IsNullOrWhiteSpace(title))
                    stream.Metadata.TryGetValue("handler_name", out title);
                var identifier = context.InputFormat?.Name == "mpegts" ? $"PID 0x{stream.Id:X4}" : $"#{stream.Index + 1}";
                var details = new[] { identifier, title, language, stream.Codecpar!.CodecName.ToUpperInvariant() };
                return new PreviewSubtitleTrackOption(new(stream.Index, stream.Id, video?.ProgramId),
                    string.Join(" · ", details.Where(text => !string.IsNullOrWhiteSpace(text))));
            }).ToArray();
    }

    public static IReadOnlyList<PreviewAudioTrackOption> CreateAudioOptions(FormatContext context,
        PreviewStreamSelection? video)
    {
        var program = video?.ProgramId is { } id ? context.Programs.Where(p => p.Id == id).ToArray() : [];
        return context.Streams.Where(stream => stream.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_AUDIO &&
                (program.Length == 0 || program[0].ContainsStream(stream.Index)))
            .Select(stream =>
            {
                stream.Metadata.TryGetValue("language", out var language);
                stream.Metadata.TryGetValue("title", out var title);
                var identifier = context.InputFormat?.Name == "mpegts" ? $"PID 0x{stream.Id:X4}" : $"#{stream.Index + 1}";
                var details = new[] { identifier, title, language, stream.Codecpar!.CodecName.ToUpperInvariant() };
                return new PreviewAudioTrackOption(new(stream.Index, stream.Id, video?.ProgramId),
                    string.Join(" · ", details.Where(text => !string.IsNullOrWhiteSpace(text))),
                    TSCutter.GUI.Player.Audio.AudioPlaybackPipeline.CanDecode(stream));
            }).ToArray();
    }

    public static IReadOnlyList<PreviewProgramOption> CreateOptions(FormatContext context)
    {
        var options = new List<PreviewProgramOption>();
        var assigned = new HashSet<int>();
        var isTs = context.InputFormat?.Name == "mpegts";
        foreach (var program in context.Programs)
        {
            var streams = context.Streams.Where(stream => program.ContainsStream(stream.Index)).ToArray();
            program.Metadata.TryGetValue("service_name", out var name);
            Add(program.Id, name, streams);
            foreach (var stream in streams)
                assigned.Add(stream.Index);
        }
        Add(null, string.Empty, context.Streams.Where(stream => !assigned.Contains(stream.Index)).ToArray());
        return options;

        void Add(int? programId, string name, IReadOnlyList<MediaStream> streams)
        {
            var videos = streams.Where(stream => stream.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_VIDEO &&
                                                 !stream.IsAttachedPicture)
                .Select(stream =>
                {
                    var codec = stream.Codecpar!;
                    var identifier = isTs ? $"PID 0x{stream.Id:X4}" : $"#{stream.Index + 1}";
                    var dimensions = codec.Width > 0 && codec.Height > 0 ? $" · {codec.Width}×{codec.Height}" : "";
                    return new PreviewVideoTrackOption(new(stream.Index, stream.Id, programId),
                        $"{identifier} · {codec.CodecName.ToUpperInvariant()}{dimensions}");
                }).ToArray();
            if (videos.Length == 0)
                return;
            var audio = streams.Where(stream => stream.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_AUDIO)
                .Select(stream =>
                {
                    stream.Metadata.TryGetValue("language", out var language);
                    return string.IsNullOrWhiteSpace(language)
                        ? stream.Codecpar!.CodecName.ToUpperInvariant()
                        : $"{language} ({stream.Codecpar!.CodecName.ToUpperInvariant()})";
                }).ToArray();
            options.Add(new PreviewProgramOption(programId, name, videos, audio));
        }
    }
}
