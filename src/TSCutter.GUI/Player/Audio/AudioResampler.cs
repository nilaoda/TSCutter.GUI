using System;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using static TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Player.Audio;

/// <summary>重采样的输出参数：全部音频统一转到 48kHz 立体声交错 f32。</summary>
public static class AudioOutputFormat
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public const AVSampleFormat SampleFormat = AVSampleFormat.AV_SAMPLE_FMT_FLT;
}

/// <summary>
/// swresample 封装：任意声道布局/采样率/样本格式的解码帧 → 48kHz 立体声交错 f32。
/// 多声道按 libswresample 默认矩阵下混。输入布局或采样率变化时自动重建。
/// </summary>
internal sealed unsafe class AudioResampler : IDisposable
{
    private AVChannelLayout outputLayout;
    private SwrContext* swr;
    private AVChannelLayout inputLayout;
    private AVSampleFormat inputFormat;
    private int inputRate;
    private bool configured;
    private float[] outputBuffer = new float[8192];

    public AudioResampler()
    {
        outputLayout.order = AVChannelOrder.AV_CHANNEL_ORDER_NATIVE;
        outputLayout.nb_channels = AudioOutputFormat.Channels;
        outputLayout.u.mask = 0x3; // AV_CH_LAYOUT_STEREO
    }

    /// <summary>
    /// 转换一帧；返回交错的 f32 采样（长度 = 帧数 × 声道数）。
    /// EOF 排空延迟时 inputFrame 传 null（必须已成功转换过至少一帧）。
    /// </summary>
    public double DelaySeconds => configured ? swr_get_delay(swr, inputRate) / (double)inputRate : 0;

    public ReadOnlySpan<float> Convert(Frame? inputFrame)
    {
        // Frame 的隐式转换不解引用 null，必须先判空（EOF 排空以 null 表示）。
        AVFrame* pointer = inputFrame is null ? null : (AVFrame*)inputFrame;
        return ConvertCore(pointer);
    }

    private ReadOnlySpan<float> ConvertCore(AVFrame* inputFrame)
    {
        if (inputFrame == null && !configured)
            return [];
        EnsureConfigured(inputFrame);
        var inSamples = inputFrame != null ? inputFrame->nb_samples : 0;
        var outSamplesUpper = (int)swr_get_delay(swr, AudioOutputFormat.SampleRate)
            + (inSamples > 0 ? (int)(inSamples * (long)AudioOutputFormat.SampleRate / inputRate) + 32 : 64);
        var requiredBytes = outSamplesUpper * AudioOutputFormat.Channels * sizeof(float);
        if (outputBuffer.Length < requiredBytes / sizeof(float))
            outputBuffer = new float[requiredBytes / sizeof(float)];

        fixed (float* outPtr = outputBuffer)
        {
            byte* outBytes = (byte*)outPtr;
            byte** inputPtr = null;
            if (inputFrame != null)
                inputPtr = inputFrame->extended_data;
            var result = swr_convert(swr, &outBytes, outSamplesUpper, inputPtr, inSamples);
            FFmpegException.Check(result);
            return outputBuffer.AsSpan(0, result * AudioOutputFormat.Channels);
        }
    }

    private void EnsureConfigured(AVFrame* inputFrame)
    {
        if (inputFrame == null)
        {
            if (!configured)
                throw new InvalidOperationException("Resampler has not been configured.");
            return;
        }
        var frame = inputFrame;
        var frameLayout = frame->ch_layout;
        var currentLayout = inputLayout;
        if (configured
            && frame->ch_layout.nb_channels == currentLayout.nb_channels
            && (AVSampleFormat)frame->format == inputFormat
            && frame->sample_rate == inputRate
            && av_channel_layout_compare(&frame->ch_layout, &currentLayout) == 0)
            return;

        ReleaseContext();
        SwrContext* allocated = swr_alloc();
        if (allocated == null)
            throw new OutOfMemoryException();
        // 类字段不取地址：复制到局部后传指针。
        var targetLayout = outputLayout;
        try
        {
            FFmpegException.Check(swr_alloc_set_opts2(&allocated,
                &targetLayout, AudioOutputFormat.SampleFormat, AudioOutputFormat.SampleRate,
                &frameLayout, (AVSampleFormat)frame->format, frame->sample_rate,
                0, null));
            FFmpegException.Check(swr_init(allocated));
            swr = allocated;
        }
        catch
        {
            SwrContext* failed = allocated;
            swr_free(&failed);
            ReleaseContext();
            throw;
        }
        fixed (AVChannelLayout* layout = &inputLayout)
            FFmpegException.Check(av_channel_layout_copy(layout, &frame->ch_layout));
        inputFormat = (AVSampleFormat)frame->format;
        inputRate = frame->sample_rate;
        configured = true;
    }

    private void ReleaseContext()
    {
        if (swr != null)
        {
            SwrContext* pointer = swr;
            swr_free(&pointer);
            swr = null;
        }
        fixed (AVChannelLayout* layout = &inputLayout)
            av_channel_layout_uninit(layout);
        configured = false;
    }

    public void Dispose() => ReleaseContext();
}
