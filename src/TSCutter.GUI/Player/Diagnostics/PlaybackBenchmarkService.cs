using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Avalonia;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Extensions;
using TSCutter.GUI.Models.Discs;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Player.Video;
using TSCutter.GUI.Rendering;
using TSCutter.GUI.Utils.Media;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Player.Diagnostics;

/// <summary>独立输入上下文上的短片段自检，复用播放器解码、转换和呈现路径。</summary>
internal static class PlaybackBenchmarkService
{
    public static Task<PlaybackBenchmarkSource> DescribeAsync(string path, DiscInput? disc, CancellationToken token)
        => Task.Run(() =>
        {
            var deadline = new MediaReadDeadline(TimeSpan.FromSeconds(30), token);
            try
            {
                using var input = disc is null
                    ? InterruptibleInputFormatContext.Open(path, () => deadline.ShouldInterrupt)
                    : InterruptibleInputFormatContext.Open(disc, () => deadline.ShouldInterrupt);
                deadline.ThrowIfInterrupted();
                input.LoadStreamInfo();
                deadline.ThrowIfInterrupted();
                var tracks = input.Streams.Where(stream => stream.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_VIDEO && !stream.IsAttachedPicture)
                    .Select(stream => new PlaybackBenchmarkTrack(PreviewStreamUtil.GetSelection(input, stream, null),
                        stream.Codecpar!.CodecName, stream.Codecpar.Width, stream.Codecpar.Height,
                        stream.AvgFrameRate.num > 0 && stream.AvgFrameRate.den > 0
                            ? stream.AvgFrameRate.num / (double)stream.AvgFrameRate.den : 0,
                        stream.StartTime != FF.AV_NOPTS_VALUE && stream.TimeBase.den > 0
                            ? stream.StartTime * (double)stream.TimeBase.num / stream.TimeBase.den : 0,
                        VideoDynamicRangeDetector.Detect(stream.Codecpar!))).ToArray();
                if (tracks.Length == 0)
                    throw new PlaybackBenchmarkException(PlaybackBenchmarkFailure.TooFewFrames);
                return new PlaybackBenchmarkSource(path, tracks,
                    disc?.Duration.TotalSeconds ?? Math.Max(0, input.Duration / (double)FF.AV_TIME_BASE), disc);
            }
            catch
            {
                deadline.ThrowIfInterrupted();
                throw;
            }
        }, token);

    public static async Task<IReadOnlyList<PlaybackBenchmarkResult>> RunAsync(PlaybackBenchmarkRequest request,
        IProgress<PlaybackBenchmarkProgress>? progress, Action<DecodeResult>? present, CancellationToken token,
        PlaybackBenchmarkLimits? limits = null, Action<Action>? uiPost = null)
    {
        limits ??= new();
        var options = new PlaybackOptions
        {
            FilePath = request.Source.FilePath, Disc = request.Source.Disc, StreamSelection = request.Track.Selection,
            StartSeconds = request.StartSeconds, TimelineOriginSeconds = request.Track.OriginSeconds,
            EstimatedKeyFrameIntervalSeconds = 1, PreferHardwareDecoding = request.PreferHardwareDecoding,
            EnableSdrToneMapping = request.EnableSdrToneMapping, Volume = 0,
            MaxPresentWidth = request.OutputSize.Width, MaxPresentHeight = request.OutputSize.Height,
            EnableGpuPresentation = present is not null, RenderAtRequestedSize = true,
            InputReadEndSeconds = request.StartSeconds + limits.MediaSeconds, CollectReadTimings = true
        };
        var results = new List<PlaybackBenchmarkResult>();
        var captures = new List<Frame>();
        try
        {
            progress?.Report(new(PlaybackBenchmarkStage.Decode, 0));
            var decoded = await DecodeAsync(options, request, limits, captures, progress, token).ConfigureAwait(false);
            results.Add(decoded);
            progress?.Report(new(PlaybackBenchmarkStage.Decode, 1, decoded));
            // CPU 转换阶段使用固定软件样本；不要把每次硬件回读算进缩放/HDR 吞吐。
            for (var index = 0; index < captures.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                if (!captures[index].HasHardwareFramesContext)
                    continue;
                var software = captures[index].TransferToSoftwareFrame();
                captures[index].Dispose();
                captures[index] = software;
            }
            var converted = await Task.Run(() => Convert(options, limits, captures, decoded, progress, token), token).ConfigureAwait(false);
            results.Add(converted);
            progress?.Report(new(PlaybackBenchmarkStage.Conversion, 1, converted));
        }
        finally
        {
            // 8K 引用帧体积很大，进入播放阶段前先释放转换样本。
            foreach (var capture in captures)
                capture.Dispose();
        }
        progress?.Report(new(PlaybackBenchmarkStage.Playback, 0));
        var played = await PlayAsync(options with { EndSeconds = options.StartSeconds + limits.PlaybackSeconds },
            limits, request.Track.FrameRate, present, progress, token, uiPost).ConfigureAwait(false);
        results.Add(played);
        progress?.Report(new(PlaybackBenchmarkStage.Playback, 1, played));
        return results;
    }

    private static async Task<PlaybackBenchmarkResult> DecodeAsync(PlaybackOptions options, PlaybackBenchmarkRequest request,
        PlaybackBenchmarkLimits limits, List<Frame> captures, IProgress<PlaybackBenchmarkProgress>? progress, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var queue = new VideoFrameQueue(3);
        await using var engine = new VideoPlaybackEngine(options, queue);
        using var cancel = timeout.Token.Register(engine.Cancel);
        try
        {
            await engine.OpenAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new PlaybackBenchmarkException(PlaybackBenchmarkFailure.Timeout);
        }
        var task = Task.Run(engine.RunAsync);
        var warmup = Math.Clamp((int)request.Track.FrameRate, 4, 50);
        var remaining = request.Source.DurationSeconds - request.StartSeconds;
        if (remaining > 0 && request.Track.FrameRate > 0)
            warmup = Math.Min(warmup, Math.Max(0, (int)(remaining * request.Track.FrameRate / 10)));
        var count = 0;
        var measured = 0;
        var first = 0.0;
        var last = 0.0;
        var started = 0L;
        var readStart = 0L;
        var lastReport = 0.0;
        var hardware = false;
        try
        {
            await engine.PrerollCompletion.WaitAsync(timeout.Token).ConfigureAwait(false);
            while (true)
            {
                QueuedVideoFrame item;
                try { item = await queue.DequeueAsync(timeout.Token).ConfigureAwait(false); }
                catch (ChannelClosedException) { await task.ConfigureAwait(false); break; }
                using (item)
                {
                    count++;
                    if (captures.Count == 0 || captures.Count == 1 && count >= Math.Max(2, warmup / 2))
                        captures.Add(item.Frame.CloneReference());
                    if (count <= warmup)
                        continue;
                    if (measured == 0)
                    {
                        started = Stopwatch.GetTimestamp();
                        readStart = engine.ReadTicks;
                        first = item.TimelineSeconds;
                    }
                    measured++;
                    last = item.TimelineSeconds;
                    hardware = engine.IsHardwareDecoding;
                    var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
                    if (seconds - lastReport >= 0.25)
                    {
                        progress?.Report(new(PlaybackBenchmarkStage.Decode, Math.Min(1, seconds / limits.DecodeSeconds),
                            FramesPerSecond: (measured - 1) / Math.Max(seconds, 0.000001)));
                        lastReport = seconds;
                    }
                    if (measured >= limits.MaxFrames || seconds >= limits.DecodeSeconds)
                        break;
                }
            }
            if (measured < 4)
                throw new PlaybackBenchmarkException(PlaybackBenchmarkFailure.TooFewFrames);
            ValidateTimeline(measured, last - first, request.Track.FrameRate);
            var elapsed = Math.Max(Stopwatch.GetElapsedTime(started).TotalSeconds, 0.000001);
            return new(PlaybackBenchmarkStage.Decode, (measured - 1) / elapsed, request.Track.FrameRate,
                measured - 1, elapsed, hardware, Math.Clamp((engine.ReadTicks - readStart) / (double)Stopwatch.Frequency / elapsed, 0, 1));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new PlaybackBenchmarkException(PlaybackBenchmarkFailure.Timeout);
        }
        finally
        {
            engine.Cancel();
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    internal static void ValidateTimeline(int frames, double seconds, double frameRate)
    {
        if (seconds <= 0 || frameRate > 0 && (frames - 1) / seconds < frameRate * 0.5)
            throw new PlaybackBenchmarkException(PlaybackBenchmarkFailure.InvalidTimestamps);
    }

    private static PlaybackBenchmarkResult Convert(PlaybackOptions options, PlaybackBenchmarkLimits limits,
        List<Frame> captures, PlaybackBenchmarkResult decoded, IProgress<PlaybackBenchmarkProgress>? progress, CancellationToken token)
    {
        progress?.Report(new(PlaybackBenchmarkStage.Conversion, 0));
        using var presenter = new HostFramePresenter();
        PixelSize actual = default;
        for (var warmup = 0; warmup < 8; warmup++)
            ConvertOne(warmup);
        var time = Stopwatch.StartNew();
        var count = 0;
        var lastReport = 0.0;
        do
        {
            ConvertOne(count++);
            if (time.Elapsed.TotalSeconds - lastReport >= 0.25)
            {
                progress?.Report(new(PlaybackBenchmarkStage.Conversion, Math.Min(1, time.Elapsed.TotalSeconds / limits.ConvertSeconds),
                    FramesPerSecond: count / time.Elapsed.TotalSeconds));
                lastReport = time.Elapsed.TotalSeconds;
            }
        } while (time.Elapsed.TotalSeconds < limits.ConvertSeconds);
        return new(PlaybackBenchmarkStage.Conversion, count / time.Elapsed.TotalSeconds, decoded.SourceFrameRate,
            count, time.Elapsed.TotalSeconds, decoded.HardwareDecoding, OutputSize: actual);

        void ConvertOne(int index)
        {
            token.ThrowIfCancellationRequested();
            if (!presenter.TryConvert(captures[index % captures.Count], options.MaxPresentWidth, options.MaxPresentHeight,
                    out var lease, options.EnableSdrToneMapping))
                throw new InvalidDataException("No conversion buffer available.");
            using (lease)
                actual = lease!.Bitmap.PixelSize;
        }
    }

    private static async Task<PlaybackBenchmarkResult> PlayAsync(PlaybackOptions options, PlaybackBenchmarkLimits limits, double sourceFrameRate,
        Action<DecodeResult>? present, IProgress<PlaybackBenchmarkProgress>? progress, CancellationToken token, Action<Action>? uiPost)
    {
        var count = 0;
        Exception? failure = null;
        await using var session = await PlaybackSession.StartAsync(options, uiPost, token, owner =>
        {
            owner.FramePresented += frame =>
            {
                Interlocked.Increment(ref count);
                if (present is not null)
                    present(frame);
                else
                {
                    frame.GpuFrame?.Dispose();
                    frame.BitmapLease?.Dispose();
                    if (frame.BitmapLease is null)
                        frame.Bitmap?.Dispose();
                }
            };
            owner.Failed += exception => Volatile.Write(ref failure, exception);
        }, audioOutputFactory: static () => null).ConfigureAwait(false);
        var time = Stopwatch.StartNew();
        do
        {
            await Task.Delay(250, token).ConfigureAwait(false);
            progress?.Report(new(PlaybackBenchmarkStage.Playback, Math.Min(1, time.Elapsed.TotalSeconds / limits.PlaybackSeconds),
                FramesPerSecond: session.PresentedFrameCount / time.Elapsed.TotalSeconds));
        } while (session.State == PlaybackState.Playing && time.Elapsed.TotalSeconds < limits.PlaybackSeconds);
        if (Volatile.Read(ref failure) is { } error)
            throw error;
        count = (int)session.PresentedFrameCount;
        if (count < 2)
            throw new PlaybackBenchmarkException(PlaybackBenchmarkFailure.TooFewFrames);
        return new(PlaybackBenchmarkStage.Playback, count / time.Elapsed.TotalSeconds, session.OutputFrameRate > 0 ? session.OutputFrameRate : sourceFrameRate,
            count, time.Elapsed.TotalSeconds, session.IsHardwareDecodingAware, OutputSize: new(options.MaxPresentWidth, options.MaxPresentHeight),
            LateDrops: session.LateDroppedFrameCount, BusyDrops: session.BusyDroppedFrameCount, Recoveries: session.CatchUpCount, GpuPresentation: session.UsesGpuPresentation, HardwareSurfacePresentation: session.UsesHardwareSurfacePresentation);
    }
}
