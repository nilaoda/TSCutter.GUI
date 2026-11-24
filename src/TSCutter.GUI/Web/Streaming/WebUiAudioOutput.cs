using System;
using System.Buffers.Binary;
using TSCutter.GUI.Player.Audio;

namespace TSCutter.GUI.Web;

internal sealed class WebUiAudioOutput : IAudioOutput
{
    private const int Channels = AudioOutputFormat.Channels;
    private const int Capacity = AudioOutputFormat.SampleRate / 4;
    internal const int PacketFrames = AudioOutputFormat.SampleRate / 50;
    private readonly object gate = new();
    private readonly float[] samples = new float[Capacity * Channels];
    private int readFrame;
    private int framesBuffered;
    private ulong consumed;
    private ulong underflow;
    private double volume = 1;
    private bool playing;
    private bool disposed;
    private bool resetPending = true;

    public ulong ConsumedFrames
    {
        get
        {
            lock (gate)
                return consumed;
        }
    }
    public ulong UnderflowFrames
    {
        get
        {
            lock (gate)
                return underflow;
        }
    }
    public bool IsPlaying
    {
        get
        {
            lock (gate)
                return playing;
        }
    }

    public bool Start()
    {
        lock (gate)
        {
            if (disposed)
                return false;
            playing = true;
            return true;
        }
    }

    public bool Stop()
    {
        lock (gate)
        {
            if (disposed)
                return false;
            playing = false;
            resetPending = true;
            return true;
        }
    }

    public bool Flush()
    {
        lock (gate)
        {
            if (disposed)
                return false;
            readFrame = 0;
            framesBuffered = 0;
            resetPending = true;
            return true;
        }
    }

    public void SetVolume(double value)
    {
        lock (gate)
            volume = Math.Clamp(value, 0, 1);
    }

    public int Write(ReadOnlySpan<float> source)
    {
        lock (gate)
        {
            if (disposed)
                return 0;
            var frames = Math.Min(source.Length / Channels, Capacity - framesBuffered);
            var writeFrame = (readFrame + framesBuffered) % Capacity;
            var first = Math.Min(frames, Capacity - writeFrame) * Channels;
            source[..first].CopyTo(samples.AsSpan(writeFrame * Channels));
            source.Slice(first, frames * Channels - first).CopyTo(samples);
            framesBuffered += frames;
            return frames;
        }
    }

    // 首字节区分音频与画面。只有两个字节时，浏览器只清空待播音频。
    internal int ReadPacket(Span<byte> destination, bool includeAudio = true)
    {
        lock (gate)
        {
            destination[0] = (byte)'A';
            destination[1] = resetPending ? (byte)1 : (byte)0;
            var resetLength = resetPending ? 2 : 0;
            // 静音时只消费环形缓冲并推进时钟；重新启用声音后先清空浏览器的旧音频。
            resetPending = !includeAudio;
            if (!playing || disposed)
                return includeAudio ? resetLength : 0;

            var frames = Math.Min(PacketFrames, framesBuffered);
            var sampleCount = frames * Channels;
            if (includeAudio)
            {
                var cursor = readFrame * Channels;
                var pcm = destination[2..];
                for (var index = 0; index < sampleCount; index++)
                {
                    var amplitude = (short)Math.Clamp(samples[cursor] * volume * 32768, -32768, 32767);
                    BinaryPrimitives.WriteInt16LittleEndian(pcm[(index * 2)..], amplitude);
                    if (++cursor == samples.Length)
                        cursor = 0;
                }
            }
            readFrame = (readFrame + frames) % Capacity;
            framesBuffered -= frames;
            consumed += (ulong)frames;
            underflow += (ulong)(PacketFrames - frames);
            if (!includeAudio)
                return 0;
            return frames == 0 ? resetLength : 2 + sampleCount * 2;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            playing = false;
            disposed = true;
            resetPending = true;
        }
    }
}
