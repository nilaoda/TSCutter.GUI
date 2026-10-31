using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Avalonia;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Player.Video;
using Xunit;

namespace TSCutter.GUI.Tests.Player;

/// <summary>有界帧队列的背压、完成与释放语义。</summary>
public class VideoFrameQueueTests
{
    private static QueuedVideoFrame Marker(double seconds) => new()
    {
        TimelineSeconds = seconds,
        Pts = (long)seconds,
    };

    [Fact]
    public async Task EnqueueBlocksWhenFullAndResumesAfterDequeue()
    {
        using var queue = new VideoFrameQueue(2);
        await queue.EnqueueAsync(Marker(0), CancellationToken.None);
        await queue.EnqueueAsync(Marker(1), CancellationToken.None);

        using var blocking = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queue.EnqueueAsync(Marker(2), blocking.Token).AsTask());

        var first = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal(0, first.TimelineSeconds);
        first.Dispose();

        await queue.EnqueueAsync(Marker(2), CancellationToken.None);
        var second = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal(1, second.TimelineSeconds);
        second.Dispose();
    }

    [Fact]
    public async Task CompleteEndsDequeueWithChannelClosedException()
    {
        using var queue = new VideoFrameQueue(2);
        await queue.EnqueueAsync(Marker(0), CancellationToken.None);
        queue.Complete();
        var item = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal(0, item.TimelineSeconds);
        item.Dispose();
        await Assert.ThrowsAsync<ChannelClosedException>(
            () => queue.DequeueAsync(CancellationToken.None).AsTask());
    }

    [NativeRuntimeFact]
    public async Task RetainsFrameProvenanceAndReleasesPendingFrames()
    {
        using var source = Frame.CreateVideo(32, 32, AVPixelFormat.AV_PIX_FMT_YUV420P);
        source.PacketPositionSource = FramePacketPositionSource.Unknown;
        unsafe
        {
            AVFrame* raw = source;
            raw->opaque = (void*)FramePacketPosition.Encode(42);
        }
        using var queue = new VideoFrameQueue(1);
        using var queued = QueuedVideoFrame.FromDecoded(source, 0, 0, VideoDynamicRange.Standard,
            VideoPresentationMode.SoftwareBitmap, new PixelSize(32, 32), false);
        await queue.EnqueueAsync(queued, CancellationToken.None);
        source.Unref();
        Assert.Equal(32, queued.Frame.Width);
        Assert.Equal(-1, queued.Frame.PacketPosition);
        queue.Dispose();
        Assert.Throws<InvalidOperationException>(() => queued.Frame);
        await Assert.ThrowsAsync<ChannelClosedException>(
            () => queue.DequeueAsync(CancellationToken.None).AsTask());
    }
}
