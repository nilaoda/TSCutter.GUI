using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.TransportStream.Processing;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Tests.FFmpeg;
using Xunit;

namespace TSCutter.GUI.Tests.Editing;

/// <summary>
/// 删除模式的保留段：止于删除起点之前（终点画面本身不输出），从删除终点（恢复播放的画面）开始。
/// 三条导出路径（TS 按字节合并、MP4 重新封装、智能剪辑）删除第 60–119 帧后，输出画面依次为 0–59 与 120 至结尾。
/// 素材每 30 帧一个关键帧，第 60、120 帧都是关键帧。
/// </summary>
public sealed class RemoveModeExportTests
{
    private static string Fixture(string name) => Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), name);

    private static string Temporary(string extension) =>
        Path.Combine(Path.GetTempPath(), $"tscutter-remove-{Guid.NewGuid():N}{extension}");

    private static IEnumerable<int> Expected(int count) => Enumerable.Range(0, 60).Concat(Enumerable.Range(120, count - 120));

    [NativeMediaFact("frameaccurate")]
    public async Task SmartCutKeepsTheFramesAroundTheRemovedRange()
    {
        var source = Fixture("h264.ts");
        var frames = FrameAccurateExportTests.DecodeFrames(source);
        var output = Temporary(".ts");
        try
        {
            var settings = FrameAccurateExportTests.Settings(source);
            await new FrameAccurateExportService().ExportAsync(new FrameAccurateExportRequest(output,
            [
                new FrameAccurateExportSegment(source, frames[0], frames[60], settings, EndExclusive: true),
                new FrameAccurateExportSegment(source, frames[120], null, settings)
            ]));
            Assert.Equal(Expected(frames.Count), FrameAccurateExportTests.DecodeNumbers(output));
        }
        finally { File.Delete(output); }
    }

    [NativeMediaFact("frameaccurate")]
    public async Task RemuxStopsBeforeTheRemovalStartKeyFrame()
    {
        var source = Fixture("h264.mp4");
        var frames = FrameAccurateExportTests.DecodeFrames(source);
        var output = Temporary(".mp4");
        try
        {
            double origin;
            AVRational timeBase;
            using (var input = FormatContext.OpenInputUrl(source))
            {
                input.LoadStreamInfo();
                var video = input.GetVideoStream();
                timeBase = video.TimeBase;
                origin = frames[0] * timeBase.num / (double)timeBase.den;
            }
            double Time(int index) => frames[index] * timeBase.num / (double)timeBase.den - origin;
            var settings = new RemuxExportSettings
            {
                Container = "mp4", ReferenceVideoIndex = 0, TimelineOriginSeconds = origin, StreamIndices = [0, 1]
            };
            await new RemuxExportService().ExportAsync(new RemuxExportRequest(output,
            [
                new RemuxExportSegment(source, 0, Time(60), settings, EndExclusive: true),
                new RemuxExportSegment(source, Time(120), null, settings)
            ]));
            Assert.Equal(Expected(frames.Count), FrameAccurateExportTests.DecodeNumbers(output));
        }
        finally { File.Delete(output); }
    }

    [NativeMediaFact("frameaccurate")]
    public async Task TsByteMergeEndsAtTheRemovalStartPacket()
    {
        var source = Fixture("h264.ts");
        var frames = FrameAccurateExportTests.DecodeFrames(source);
        var positions = new Dictionary<long, long>();
        using (var input = FormatContext.OpenInputUrl(source))
        {
            input.LoadStreamInfo();
            var video = input.GetVideoStream();
            foreach (var packet in input.ReadPackets())
                unsafe
                {
                    AVPacket* raw = packet;
                    if (raw->stream_index == video.Index && (raw->flags & 1) != 0)
                        positions[raw->pts] = raw->pos;
                }
        }
        var output = Temporary(".ts");
        try
        {
            await new TsClipMergeService().MergeAsync(new TsClipMergeRequest
            {
                SourcePath = source, OutputPath = output,
                Ranges = [new(0, positions[frames[60]], 0, 2), new(positions[frames[120]], -1, 4, 12)]
            });
            Assert.Equal(Expected(frames.Count), FrameAccurateExportTests.DecodeNumbers(output));
        }
        finally { File.Delete(output); }
    }
}
