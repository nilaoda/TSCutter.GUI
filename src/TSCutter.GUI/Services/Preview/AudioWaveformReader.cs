using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.Extensions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Discs;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Player.Audio;
using TSCutter.GUI.Utils.Media;
using TSCutter.GUI.Utils.Media.DolbyE;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Services.Preview;

/// <summary>导航条波形的来源：与播放相同的输入、视频与所选音轨，以及时间轴起点。</summary>
internal sealed record AudioWaveformSource(string Path, DiscInput? Disc, long TsSyncOffset,
    PreviewStreamSelection? Video, PreviewStreamSelection Audio, double TimelineOriginSeconds, int? DolbyEProgram = null,
    IReadOnlyList<PreviewChannelMergeSource>? ChannelMerge = null);

/// <summary>
/// 读取导航条波形：按时间轴整秒读取所选音轨，解码后按毫秒记录峰值（各声道绝对值的最大值）。
/// 与播放相同地打开输入（文件或光盘）并按视频流定位；只解码音频，视频包只用来判断读到了哪里，
/// 音轨中断或提前结束时也能及时停止，读取量不超过所请求的时间范围。
/// </summary>
internal sealed class AudioWaveformReader : IDisposable
{
    public const int BucketsPerSecond = 1000;
    // 复用时音频与同一时刻的视频在文件中的位置可能错开约一秒，范围前后各多读一些。
    private const double MarginSeconds = 1.5;
    private const int MaxInvalidPackets = 16;

    private readonly FormatContext input;
    private readonly MediaStream video;
    private readonly MediaStream audio;
    private readonly CodecContext? decoder;
    private readonly DolbyEProgramDecoder? dolbyE;
    private readonly ChannelMergePreviewDecoder? merge;
    private readonly Frame frame = new();
    private AudioResampler resampler = new();
    private readonly double origin;

    private AudioWaveformReader(AudioWaveformSource source, FormatContext input, MediaStream video, MediaStream audio,
        CodecContext? decoder, DolbyEProgramDecoder? dolbyE, ChannelMergePreviewDecoder? merge = null)
    {
        this.merge = merge;
        Source = source;
        this.input = input;
        this.video = video;
        this.audio = audio;
        this.decoder = decoder;
        this.dolbyE = dolbyE;
        origin = source.TimelineOriginSeconds;
    }

    public AudioWaveformSource Source { get; }

    /// <summary>打开输入与音频解码器；音轨找不到或解码器打不开时返回 null（整条波形不显示）。</summary>
    /// <remarks>
    /// 不经中断回调取消：中断会让输入上下文停在错误状态，之后的定位与读取都会失败。
    /// 视频包不丢弃，每次读取都很快返回一个包，在包之间检查取消即可及时停止。
    /// </remarks>
    public static AudioWaveformReader? TryOpen(AudioWaveformSource source)
    {
        var input = source.Disc is { } disc
            ? InterruptibleInputFormatContext.Open(disc, static () => false)
            : InterruptibleInputFormatContext.Open(source.Path, static () => false, source.TsSyncOffset);
        try
        {
            input.LoadStreamInfo();
            var video = PreviewStreamUtil.ResolveVideo(input, source.Video);
            if (PreviewStreamUtil.ResolveAudio(input, source.Audio) is not { } audio)
            {
                input.Dispose();
                return null;
            }
            // 多轨合并试听：画三条来源合成后的波形。
            if (source.ChannelMerge is { Count: 3 } sources)
            {
                var streams = new List<(MediaStream, bool)>();
                foreach (var item in sources)
                {
                    if (PreviewStreamUtil.ResolveAudio(input, source.Audio with { StreamId = item.Pid }) is not { } stream)
                    {
                        input.Dispose();
                        return null;
                    }
                    streams.Add((stream, item.Swap));
                }
                if (ChannelMergePreviewDecoder.TryCreate(streams) is not { } merge)
                {
                    input.Dispose();
                    return null;
                }
                return new AudioWaveformReader(source, input, video, streams[0].Item1, null, null, merge);
            }
            // 杜比 E 只画所选声道组的波形。
            if (source.DolbyEProgram is { } program)
            {
                if (DolbyEProgramDecoder.TryCreate(audio, program) is not { } dolbyE)
                {
                    input.Dispose();
                    return null;
                }
                return new AudioWaveformReader(source, input, video, audio, null, dolbyE);
            }
            if (AudioPlaybackPipeline.TryOpenDecoder(audio) is not { } decoder)
            {
                input.Dispose();
                return null;
            }
            return new AudioWaveformReader(source, input, video, audio, decoder, null);
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 读取 [firstSecond, endSecond) 中的各整秒，每读完一秒回调一次（数组下标为毫秒，没有声音的毫秒为 NaN）。
    /// 已回调的数组之后不再修改。
    /// </summary>
    public void Read(int firstSecond, int endSecond, Action<int, float[]> completed, CancellationToken cancellationToken)
    {
        var seconds = new float[endSecond - firstSecond][];
        for (var i = 0; i < seconds.Length; i++)
        {
            seconds[i] = new float[BucketsPerSecond];
            Array.Fill(seconds[i], float.NaN);
        }
        var emitted = 0;
        void EmitUntil(double time)
        {
            while (emitted < seconds.Length && firstSecond + emitted + 1 <= time)
            {
                completed(firstSecond + emitted, seconds[emitted]);
                emitted++;
            }
        }

        var videoBase = video.TimeBase;
        input.SeekFrame((long)Math.Floor((firstSecond - MarginSeconds + origin) * videoBase.den / videoBase.num),
            video.Index);
        decoder?.FlushBuffers();
        dolbyE?.Flush();
        merge?.Flush();
        resampler.Dispose();
        resampler = new AudioResampler();
        var stopAt = endSecond + MarginSeconds;
        var invalid = 0;
        foreach (var packet in input.ReadPackets())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (packet.StreamIndex == video.Index)
            {
                if (packet.Pts != FF.AV_NOPTS_VALUE && Seconds(packet.Pts, videoBase) > stopAt)
                    break;
                continue;
            }
            if (packet.StreamIndex != audio.Index)
            {
                // 合并的其他来源只送去解码，读取范围以前置来源为准。
                if (merge is not null && merge.IsSource(packet.StreamIndex))
                    DecodeIgnoringInvalid(packet, seconds, firstSecond, ref invalid);
                continue;
            }
            if (packet.Pts != FF.AV_NOPTS_VALUE && Seconds(packet.Pts, audio.TimeBase) > stopAt)
                break;
            try
            {
                Decode(packet, seconds, firstSecond);
                invalid = 0;
            }
            catch (FFmpegException exception) when (exception.ErrorCode == FF.AVERROR_INVALIDDATA
                                                    && ++invalid < MaxInvalidPackets)
            {
                // 定位可能落在音频 PES 中间；丢弃无法解码的残包，后续完整包仍可恢复。
            }
            if (packet.Pts != FF.AV_NOPTS_VALUE)
                EmitUntil(Seconds(packet.Pts, audio.TimeBase) - MarginSeconds);
        }
        cancellationToken.ThrowIfCancellationRequested();
        // 合并：读取结束后不再等待其他来源，剩下的帧全部合并。
        merge?.Decode(null, decoded => Accumulate(decoded, seconds, firstSecond));
        EmitUntil(double.PositiveInfinity);
    }

    private double Seconds(long pts, AVRational timeBase) => pts * (double)timeBase.num / timeBase.den - origin;

    private void DecodeIgnoringInvalid(Packet packet, float[][] seconds, int firstSecond, ref int invalid)
    {
        try
        {
            Decode(packet, seconds, firstSecond);
            invalid = 0;
        }
        catch (FFmpegException exception) when (exception.ErrorCode == FF.AVERROR_INVALIDDATA
                                                && ++invalid < MaxInvalidPackets)
        {
            // 定位可能落在音频 PES 中间；丢弃无法解码的残包。
        }
    }

    private void Decode(Packet packet, float[][] seconds, int firstSecond)
    {
        if (merge is not null)
        {
            merge.Decode(packet, decoded => Accumulate(decoded, seconds, firstSecond));
            return;
        }
        if (dolbyE is not null)
        {
            dolbyE.Decode(packet, decoded => Accumulate(decoded, seconds, firstSecond));
            return;
        }
        foreach (var decoded in decoder!.DecodePacket(packet, frame, preserveRemainingOutput: true))
            Accumulate(decoded, seconds, firstSecond);
    }

    private void Accumulate(Frame decoded, float[][] seconds, int firstSecond)
    {
        var pts = decoded.BestEffortTimestamp;
        if (pts == FF.AV_NOPTS_VALUE)
            pts = decoded.Pts;
        if (pts == FF.AV_NOPTS_VALUE)
            return;
        // 与播放相同地扣除重采样延迟，使波形与声音输出对齐。
        var start = Seconds(pts, audio.TimeBase) - resampler.DelaySeconds;
        var samples = resampler.Convert(decoded);
        var channels = AudioOutputFormat.Channels;
        for (var index = 0; index + channels <= samples.Length; index += channels)
        {
            var bucket = (long)Math.Floor((start + index / channels / (double)AudioOutputFormat.SampleRate)
                                          * BucketsPerSecond) - (long)firstSecond * BucketsPerSecond;
            if (bucket < 0)
                continue;
            var second = bucket / BucketsPerSecond;
            if (second >= seconds.Length)
                break;
            var peak = 0f;
            for (var channel = 0; channel < channels; channel++)
                peak = Math.Max(peak, Math.Abs(samples[index + channel]));
            ref var slot = ref seconds[second][bucket % BucketsPerSecond];
            if (float.IsNaN(slot) || peak > slot)
                slot = peak;
        }
    }

    public void Dispose()
    {
        frame.Dispose();
        resampler.Dispose();
        decoder?.Dispose();
        dolbyE?.Dispose();
        merge?.Dispose();
        input.Dispose();
    }
}
