using System;
using System.Collections.Generic;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Player.Video;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Utils.Media;
using Xunit;

namespace TSCutter.GUI.Tests.Player;

public sealed class VideoDeinterlacerTests
{
    [NativeRuntimeFact]
    public unsafe void FieldsKeepPresentationTimesAndOriginalFrameAnchorsThroughDrain()
    {
        FFmpegNativeBootstrapper.Initialize();
        using var input = Frame.CreateVideo(32, 32, AVPixelFormat.AV_PIX_FMT_YUV420P);
        AVFrame* raw = input;
        for (var plane = 0; plane < 3; plane++)
            new Span<byte>(raw->data[(uint)plane], raw->linesize[(uint)plane] * (32 >> (plane == 0 ? 0 : 1))).Fill(128);
        raw->flags = NativeMethods.AV_FRAME_FLAG_INTERLACED | NativeMethods.AV_FRAME_FLAG_TOP_FIELD_FIRST;
        using var filter = new VideoDeinterlacer(input, new AVRational { num = 1, den = 90000 });
        using var output = new Frame();
        var frames = new List<(long Presentation, long Source)>();
        for (var index = 0; index < 3; index++)
        {
            raw->pts = index * 3600;
            raw->best_effort_timestamp = raw->pts;
            filter.Push(input);
            Read();
        }
        filter.Complete();
        Read();
        Assert.Equal(new[] { (0L, 0L), (1800L, 0L), (3600L, 3600L), (5400L, 3600L), (7200L, 7200L), (9000L, 7200L) }, frames);
        Assert.Equal(7200, input.Pts);

        void Read()
        {
            while (filter.TryReceive(output, out var presentation, out var source))
            {
                frames.Add((presentation, source));
                Assert.Equal(0, output.Flags & NativeMethods.AV_FRAME_FLAG_INTERLACED);
            }
        }
    }

    [NativeRuntimeFact]
    public unsafe void ProgressiveFramesInAMixedStreamAreNotDuplicated()
    {
        FFmpegNativeBootstrapper.Initialize();
        using var input = Frame.CreateVideo(32, 32, AVPixelFormat.AV_PIX_FMT_YUV420P);
        using var filter = new VideoDeinterlacer(input, new AVRational { num = 1, den = 90000 });
        using var output = new Frame();
        AVFrame* raw = input;
        for (var plane = 0; plane < 3; plane++)
            new Span<byte>(raw->data[(uint)plane], raw->linesize[(uint)plane] * (32 >> (plane == 0 ? 0 : 1))).Fill(128);
        var times = new List<long>();
        for (var index = 0; index < 3; index++)
        {
            raw->pts = index * 3600;
            raw->best_effort_timestamp = raw->pts;
            raw->flags = index == 1 ? 0 : NativeMethods.AV_FRAME_FLAG_INTERLACED | NativeMethods.AV_FRAME_FLAG_TOP_FIELD_FIRST;
            filter.Push(input);
            Read();
        }
        filter.Complete();
        Read();
        Assert.Equal(new[] { 0L, 1800L, 3600L, 7200L, 9000L }, times);

        void Read()
        {
            while (filter.TryReceive(output, out var presentation, out _))
                times.Add(presentation);
        }
    }
}
