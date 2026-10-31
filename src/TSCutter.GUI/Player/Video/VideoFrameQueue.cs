using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Avalonia;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Preview;

namespace TSCutter.GUI.Player.Video;

/// <summary>队列中的解码帧；持有独立的 AVFrame 引用，消费后必须 Dispose。</summary>
internal sealed unsafe class QueuedVideoFrame : IDisposable
{
    private Frame? frame;

    public static QueuedVideoFrame FromDecoded(Frame decoded, double timelineSeconds, long pts,
        VideoDynamicRange range, VideoPresentationMode mode, PixelSize pixelSize, bool requiresCorrection,
        long pts90k = long.MinValue, long position = -1)
    {
        var owned = decoded.CloneReference();
        try
        {
            return new QueuedVideoFrame
            {
                frame = owned,
                TimelineSeconds = timelineSeconds,
                Pts = pts,
                Range = range,
                Mode = mode,
                PixelSize = pixelSize,
                RequiresCorrection = requiresCorrection,
                Pts90k = pts90k,
                Position = position,
            };
        }
        catch
        {
            owned.Dispose();
            throw;
        }
    }

    public required double TimelineSeconds { get; init; }
    public required long Pts { get; init; }
    public long Pts90k { get; private init; }
    public long Position { get; private init; }
    /// <summary>入队时捕获的动态范围：硬件帧回读可能丢失 HDR 边数据。</summary>
    public VideoDynamicRange Range { get; private init; }
    public VideoPresentationMode Mode { get; private init; }
    public PixelSize PixelSize { get; private init; }
    public bool RequiresCorrection { get; private init; }

    public Frame Frame => frame ?? throw new InvalidOperationException("End-of-stream marker has no frame.");

    public void Dispose()
    {
        frame?.Dispose();
        frame = null;
    }
}

/// <summary>
/// 解码线程与呈现线程之间的有界帧队列。容量以帧数限制内存占用，
/// 队列满时阻塞解码端（背压），保证不在内存中积累未压缩帧。
/// </summary>
internal sealed class VideoFrameQueue : IDisposable
{
    private readonly Channel<QueuedVideoFrame> channel;

    public VideoFrameQueue(int capacity)
    {
        channel = Channel.CreateBounded<QueuedVideoFrame>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public ValueTask EnqueueAsync(QueuedVideoFrame item, System.Threading.CancellationToken cancellationToken)
        => channel.Writer.WriteAsync(item, cancellationToken);

    public bool HasReadyFrames => channel.Reader.TryPeek(out _);

    public void Complete() => channel.Writer.TryComplete();

    /// <summary>读取下一帧；流结束时抛出 ChannelClosedException。</summary>
    public ValueTask<QueuedVideoFrame> DequeueAsync(System.Threading.CancellationToken cancellationToken)
        => channel.Reader.ReadAsync(cancellationToken);

    public void Dispose()
    {
        channel.Writer.TryComplete();
        // 丢弃尚未消费的帧；读者退出后队列中剩余项由这里统一释放。
        while (channel.Reader.TryRead(out var item))
            item.Dispose();
    }
}
