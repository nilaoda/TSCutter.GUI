using System;
using System.Threading.Tasks;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Player.Audio;
using TSCutter.GUI.Tests.FFmpeg;
using Xunit;
using static TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Tests.Player;

/// <summary>swresample 重采样封装：下混、采样率转换与 EOF 排空。</summary>
public sealed unsafe class AudioResamplerTests
{
    private static AVChannelLayout CreateLayout(int channels)
    {
        var layout = new AVChannelLayout();
        av_channel_layout_default(&layout, channels);
        return layout;
    }

    /// <summary>
    /// 构造音频帧；generator(帧序号, 声道序号) 返回样本值，
    /// packed 格式写入交错缓冲，planar 格式按平面写入。
    /// </summary>
    private static Frame CreateAudioFrame(AVSampleFormat format, int sampleRate, AVChannelLayout layout,
        int samples, Func<int, int, float> generator)
    {
        var frame = new Frame();
        var raw = (AVFrame*)frame;
        raw->format = (int)format;
        raw->sample_rate = sampleRate;
        var layoutCopy = layout;
        FFmpegException.Check(av_channel_layout_copy(&raw->ch_layout, &layoutCopy));
        raw->nb_samples = samples;
        FFmpegException.Check(av_frame_get_buffer(raw, 0));
        var channels = layout.nb_channels;
        var planar = av_sample_fmt_is_planar(format) != 0;
        var planes = planar ? channels : 1;
        for (var plane = 0; plane < planes; plane++)
        {
            var data = (float*)raw->data[(uint)plane];
            for (var frameIndex = 0; frameIndex < samples; frameIndex++)
            {
                if (planar)
                    data[frameIndex] = generator(frameIndex, plane);
                else
                    for (var channel = 0; channel < channels; channel++)
                        data[frameIndex * channels + channel] = generator(frameIndex, channel);
            }
        }
        return frame;
    }

    [NativeRuntimeFact]
    public void ConvertsMono44kToStereo48kInterleaved()
    {
        using var resampler = new AudioResampler();
        const int sampleRate = 44_100;
        const int samples = 4410; // 100ms
        var layout = CreateLayout(1);
        using var frame = CreateAudioFrame(AVSampleFormat.AV_SAMPLE_FMT_FLT, sampleRate,
            layout, samples, (sample, _) => sample % 2 == 0 ? 0.5f : -0.5f);

        var output = resampler.Convert(frame);
        Assert.True(output.Length > 0);
        Assert.Equal(0, output.Length % AudioOutputFormat.Channels);
        var frames = output.Length / AudioOutputFormat.Channels;
        // 100ms @44.1k → 约 4800 帧 @48k（±10ms 容差）。
        Assert.InRange(frames, 4380, 4980);
        // 交错立体声：左右声道同为单声道输入。
        Assert.Equal(output[0], output[1], 3);
        // swr 默认矩阵对单声道下混使用 -3dB 衰减（0.5 × 0.707）。
        Assert.Equal(0.3536f, output[0], 2);
    }

    [NativeRuntimeFact]
    public void DrainsResamplerDelayAtEndOfStream()
    {
        using var resampler = new AudioResampler();
        var layout = CreateLayout(2);
        using var frame = CreateAudioFrame(AVSampleFormat.AV_SAMPLE_FMT_FLT, 48_000,
            layout, 1024, (sample, plane) => plane == 0 ? 0.25f : -0.25f);
        var first = resampler.Convert(frame);
        Assert.True(first.Length > 0);

        // EOF 排空：延迟缓冲中的样本应在空转换中产出，随后为空。
        var drained = resampler.Convert(null);
        Assert.True(resampler.Convert(null).IsEmpty);
        var totalFrames = first.Length / 2 + drained.Length / 2;
        Assert.InRange(totalFrames, 1000, 1060);
    }

    [NativeRuntimeFact]
    public void RebuildsContextWhenInputLayoutChanges()
    {
        using var resampler = new AudioResampler();
        var monoLayout = CreateLayout(1);
        var stereoLayout = CreateLayout(2);
        using var mono = CreateAudioFrame(AVSampleFormat.AV_SAMPLE_FMT_FLT, 48_000,
            monoLayout, 512, (_, _) => 0.1f);
        using var stereo = CreateAudioFrame(AVSampleFormat.AV_SAMPLE_FMT_FLT, 48_000,
            stereoLayout, 512, (sample, plane) => plane == 0 ? 0.2f : -0.2f);

        var monoOutput = resampler.Convert(mono);
        var stereoOutput = resampler.Convert(stereo);
        Assert.Equal(512 * 2, stereoOutput.Length);
        Assert.Equal(0.2f, stereoOutput[0], 3);
        Assert.Equal(-0.2f, stereoOutput[1], 3);
        Assert.Equal(monoOutput.Length, stereoOutput.Length);
    }
}
