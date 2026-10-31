using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Extensions;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Player.Video;
using FFmpeg.AutoGen.Abstractions;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Player.Audio;

/// <summary>队列中的音频包；持有独立的 AVPacket 引用，消费后必须 Dispose。</summary>
internal sealed unsafe class QueuedAudioPacket : IDisposable
{
    private Packet? packet;

    public static QueuedAudioPacket CopyFrom(Packet source)
    {
        var owned = new Packet();
        try
        {
            FFmpegException.Check(FF.av_packet_ref(owned, source));
            return new QueuedAudioPacket { packet = owned };
        }
        catch
        {
            owned.Dispose();
            throw;
        }
    }

    public Packet Packet => packet ?? throw new InvalidOperationException("Packet was released.");

    public void Dispose()
    {
        packet?.Unref();
        packet?.Dispose();
        packet = null;
    }
}

/// <summary>独立音频解码线程，按音轨自身时基裁剪预滚并输出有界 PCM。</summary>
internal sealed class AudioPlaybackPipeline : IAsyncDisposable
{
    private const int RingCapacityFrames = AudioOutputFormat.SampleRate / 4;
    private Channel<QueuedAudioPacket> packetQueue;
    private readonly MediaStream audioStream;
    private double startSeconds;
    private readonly double originSeconds;
    private long? endSample;
    private CancellationTokenSource cancellation = new();
    private readonly ManualResetEventSlim running = new(true);
    private ulong consumedOrigin;
    private int paused;
    private readonly IAudioOutput device;
    private CodecContext? decoder;
    private AudioResampler resampler = new();
    private Frame? reusableFrame;
    private Task? runTask;
    private long writtenFrames;
    private long nextDecodedSample;
    private bool hasDecodedTimestamp;
    private int started;
    private TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposed;

    private AudioPlaybackPipeline(IAudioOutput device, MediaStream audioStream,
        double startSeconds, double? endSeconds, double originSeconds)
    {
        this.device = device;
        this.audioStream = audioStream;
        this.startSeconds = startSeconds;
        this.originSeconds = originSeconds;
        endSample = endSeconds is { } end
            ? Math.Max(0, (long)Math.Ceiling((end - startSeconds) * AudioOutputFormat.SampleRate)) : null;
        packetQueue = Channel.CreateBounded<QueuedAudioPacket>(new BoundedChannelOptions(128)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
        });
    }

    public long BufferedFrames => Math.Max(0, WrittenFrames - (long)ConsumedFrames);
    public ulong ConsumedFrames
    {
        get
        {
            var consumed = device.ConsumedFrames;
            return consumed >= consumedOrigin ? consumed - consumedOrigin : 0;
        }
    }
    public long WrittenFrames => Interlocked.Read(ref writtenFrames);
    public int SampleRate => AudioOutputFormat.SampleRate;
    public bool IsPlaying => device.IsPlaying;
    public string? Failure { get; private set; }
    public Task Completion => drained.Task;

    public static async Task<AudioPlaybackPipeline?> TryCreateAsync(MediaStream audioStream,
        double startSeconds, double? endSeconds, double originSeconds,
        Func<IAudioOutput?>? deviceFactory = null)
    {
        var device = deviceFactory is null
            ? AudioOutputDevice.TryCreate(AudioOutputFormat.SampleRate, AudioOutputFormat.Channels, RingCapacityFrames)
            : deviceFactory();
        if (device is null)
            return null;
        var pipeline = new AudioPlaybackPipeline(device, audioStream, startSeconds, endSeconds, originSeconds);
        try
        {
            await Task.Run(pipeline.OpenDecoder).ConfigureAwait(false);
            return pipeline;
        }
        catch
        {
            await pipeline.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void OpenDecoder()
    {
        decoder = TryOpenDecoder(audioStream)
            ?? throw new VideoDecoderUnavailableException(audioStream.Codecpar?.CodecName ?? "unknown");
    }

    // 只检查运行库能否打开该音轨的解码器，不读取音频包或打开音频设备。
    internal static bool CanDecode(MediaStream stream)
    {
        using var candidate = TryOpenDecoder(stream);
        return candidate is not null;
    }

    private static CodecContext? TryOpenDecoder(MediaStream stream)
    {
        if (stream.Codecpar is not { } parameters)
            return null;
        foreach (var codec in Codec.FindDecoders(parameters.CodecId))
        {
            CodecContext? candidate = null;
            try
            {
                candidate = new CodecContext(codec);
                candidate.FillParameters(parameters);
                candidate.PacketTimeBase = stream.TimeBase;
                candidate.Open();
                return candidate;
            }
            catch
            {
                candidate?.Dispose();
            }
        }
        return null;
    }

    public async ValueTask EnqueuePacketAsync(Packet source, CancellationToken token)
    {
        var copy = QueuedAudioPacket.CopyFrom(source);
        try { await packetQueue.Writer.WriteAsync(copy, token).ConfigureAwait(false); }
        catch { copy.Dispose(); throw; }
    }

    public void CompleteInput() => packetQueue.Writer.TryComplete();
    public void Cancel()
    {
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }
    public void SetVolume(double value) => device.SetVolume(Math.Clamp(value, 0, 1));

    // 不等待音频包才能启动视频：有的音轨在目标画面数秒后才开始。
    public async Task PrerollAsync(CancellationToken token)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (!Completion.IsCompleted && BufferedFrames < SampleRate / 20 && deadline.ElapsedMilliseconds < 150)
            await Task.Delay(5, token).ConfigureAwait(false);
    }

    public bool Start()
    {
        if (!device.Start())
            return false;
        Volatile.Write(ref started, 1);
        Volatile.Write(ref paused, 0);
        running.Set();
        return true;
    }

    public bool Pause()
    {
        Volatile.Write(ref paused, 1);
        running.Reset();
        return device.Stop();
    }

    internal async Task StopForSeekAsync()
    {
        cancellation.Cancel();
        packetQueue.Writer.TryComplete();
        if (runTask is { } task)
            await task.ConfigureAwait(false);
        device.Stop();
    }

    internal bool ResetForSeek(double start, double? end, CancellationToken token)
    {
        if (device.IsPlaying || !device.Flush())
            return false;
        consumedOrigin = device.ConsumedFrames;
        cancellation.Dispose();
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        decoder!.FlushBuffers();
        resampler.Dispose();
        resampler = new();
        reusableFrame?.Unref();
        startSeconds = start;
        endSample = end is { } limit
            ? Math.Max(0, (long)Math.Ceiling((limit - start) * SampleRate)) : null;
        nextDecodedSample = writtenFrames = 0;
        hasDecodedTimestamp = false;
        Failure = null;
        started = paused = 0;
        runTask = null;
        drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        packetQueue = Channel.CreateBounded<QueuedAudioPacket>(new BoundedChannelOptions(128)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
        });
        running.Set();
        return true;
    }

    // 音频解码和设备背压等待使用专用线程，避免 HDR 转换等线程池任务拖延声音供给。
    public Task RunAsync() => runTask ??= Task.Factory.StartNew(RunCore,
        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void RunCore()
    {
        var token = cancellation.Token;
        var destination = reusableFrame ??= new Frame();
        try
        {
            var boundaryReached = false;
            var badPackets = 0;
            while (packetQueue.Reader.WaitToReadAsync(token).AsTask().GetAwaiter().GetResult())
            {
                while (packetQueue.Reader.TryRead(out var queued))
                {
                    using (queued)
                    {
                        running.Wait(token);
                        try
                        {
                            boundaryReached = DecodeAndWrite(queued.Packet, destination, token);
                            badPackets = 0;
                        }
                        catch (FFmpegException exception) when (exception.ErrorCode == FF.AVERROR_INVALIDDATA && ++badPackets < 16)
                        {
                            // TS seek 可落在音频 PES 中间；丢弃无法解码的残包，后续完整包仍可恢复。
                        }
                    }
                    if (boundaryReached)
                        break;
                }
                if (boundaryReached)
                    break;
            }
            if (!boundaryReached)
            {
                DecodeAndWrite(null, destination, token);
                while (!token.IsCancellationRequested)
                {
                    var samples = resampler.Convert(null);
                    if (samples.IsEmpty)
                        break;
                    WriteSamples(samples, nextDecodedSample, token);
                    nextDecodedSample += samples.Length / AudioOutputFormat.Channels;
                }
            }
            packetQueue.Writer.TryComplete();
            // 短文件也必须把已经缓冲的音频播完，不能把“输入读完”等同于“输出播完”。
            while (WrittenFrames > (long)ConsumedFrames)
            {
                token.ThrowIfCancellationRequested();
                running.Wait(token);
                CheckDevice();
                Thread.Sleep(5);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Failure = exception.ToString();
            Console.WriteLine($"Audio playback failed: {exception}");
        }
        finally
        {
            // 消费者退出后必须关闭写端，否则解复用线程会永远等待一个无人消费的满队列。
            packetQueue.Writer.TryComplete();
            while (packetQueue.Reader.TryRead(out var queued))
                queued.Dispose();
            drained.TrySetResult();
        }
    }

    private unsafe bool DecodeAndWrite(Packet? packet, Frame destination, CancellationToken token)
    {
        foreach (var frame in decoder!.DecodePacket(packet, destination, preserveRemainingOutput: true))
        {
            token.ThrowIfCancellationRequested();
            var pts = frame.BestEffortTimestamp;
            if (pts == FF.AV_NOPTS_VALUE)
                pts = frame.Pts;
            var delay = resampler.DelaySeconds;
            if (pts != FF.AV_NOPTS_VALUE)
            {
                var seconds = pts * (double)audioStream.TimeBase.num / audioStream.TimeBase.den
                    - originSeconds - delay;
                nextDecodedSample = (long)Math.Round((seconds - startSeconds) * SampleRate);
                hasDecodedTimestamp = true;
            }
            else if (!hasDecodedTimestamp)
            {
                continue;
            }
            var samples = resampler.Convert(frame);
            var firstSample = nextDecodedSample;
            nextDecodedSample += samples.Length / AudioOutputFormat.Channels;
            if (WriteSamples(samples, firstSample, token))
                return true;
        }
        return false;
    }

    private bool WriteSamples(ReadOnlySpan<float> samples, long firstSample, CancellationToken token)
    {
        var frames = samples.Length / AudioOutputFormat.Channels;
        if (frames == 0)
            return false;
        if (endSample is { } end && firstSample >= end)
            return true;
        // 预滚只供解码依赖，不能播放目标位置以前的声音。
        var skipped = (int)Math.Min(frames, Math.Max(0, -firstSample));
        samples = samples[(skipped * AudioOutputFormat.Channels)..];
        firstSample += skipped;
        frames -= skipped;
        if (frames == 0)
            return false;
        if (endSample is { } limit)
            frames = (int)Math.Min(frames, Math.Max(0, limit - firstSample));
        samples = samples[..(frames * AudioOutputFormat.Channels)];

        // 音轨较晚开始或含时间戳间隙时补静音；欠载已播放的时间不能再排队重播。
        var cursor = Math.Max(WrittenFrames, (long)ConsumedFrames);
        Span<float> silence = stackalloc float[960];
        silence.Clear();
        while (cursor < firstSample)
        {
            var count = (int)Math.Min(silence.Length / AudioOutputFormat.Channels, firstSample - cursor);
            WriteBlock(silence[..(count * AudioOutputFormat.Channels)], token);
            cursor = Math.Max(WrittenFrames, (long)ConsumedFrames);
        }
        skipped = (int)Math.Min(frames, Math.Max(0, cursor - firstSample));
        WriteBlock(samples[(skipped * AudioOutputFormat.Channels)..], token);
        return endSample is { } cutoff && nextDecodedSample >= cutoff;
    }

    private void CheckDevice()
    {
        if (Volatile.Read(ref started) != 0 && !device.IsPlaying && Volatile.Read(ref paused) == 0)
            throw new InvalidOperationException("Audio output device stopped unexpectedly.");
    }

    private void WriteBlock(ReadOnlySpan<float> samples, CancellationToken token)
    {
        var offset = 0;
        while (offset < samples.Length)
        {
            running.Wait(token);
            token.ThrowIfCancellationRequested();
            CheckDevice();
            var before = Math.Max(WrittenFrames, (long)ConsumedFrames);
            var accepted = device.Write(samples[offset..]);
            Interlocked.Exchange(ref writtenFrames, before + accepted);
            offset += accepted * AudioOutputFormat.Channels;
            if (accepted == 0)
                Thread.Sleep(2);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        cancellation.Cancel();
        packetQueue.Writer.TryComplete();
        if (runTask is { } task)
            await task.ConfigureAwait(false);
        device.Stop();
        device.Dispose();
        decoder?.Dispose();
        reusableFrame?.Dispose();
        resampler.Dispose();
        cancellation.Dispose();
        running.Dispose();
    }
}
