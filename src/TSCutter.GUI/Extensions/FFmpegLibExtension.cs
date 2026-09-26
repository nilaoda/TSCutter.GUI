using System;
using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Common;
using Sdcb.FFmpeg.Formats;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Utils;
using static Sdcb.FFmpeg.Raw.ffmpeg;

namespace TSCutter.GUI.Extensions;

public static unsafe class FFmpegLibExtension
{
    private const int AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX = 0x01;

    static int ThrowIfError(this int errorCode, string? message = null)
    {
        if (errorCode < 0)
        {
            throw FFmpegException.FromErrorCode(errorCode, message);
        }
        return errorCode;
    }

    static long ThrowIfError(this long errorCode, string? message = null)
    {
        if (errorCode < 0)
        {
            throw FFmpegException.FromErrorCode((int)errorCode, message);
        }
        return errorCode;
    }
    
    /// <summary>
    /// <see cref="avformat_seek_file"/>
    /// </summary>
    public static void SeekFile(this FormatContext formatContext, long timestamp, long minTimestamp, long maxTimestamp, int streamIndex = -1, AVSEEK_FLAG flags = AVSEEK_FLAG.Backward)
        => avformat_seek_file(formatContext, streamIndex,  minTimestamp, timestamp, maxTimestamp, (int)flags).ThrowIfError();

    /// <summary>
    /// <see cref="avcodec_flush_buffers"/>
    /// </summary>
    public static void FlushBuffers(this CodecContext c) => avcodec_flush_buffers(c);
    
    /// <summary>
    /// <see cref="avio_size"/>
    /// </summary>
    public static long GetFileSize(this FormatContext formatContext) => avio_size(formatContext.Pb!);

    /// <summary>
    /// 判断解码器是否支持通过指定硬件设备上下文输出硬件帧。
    /// </summary>
    public static bool SupportsHardwareDevice(this Codec codec, AVHWDeviceType deviceType)
    {
        AVCodec* rawCodec = codec;
        for (var index = 0; ; index++)
        {
            var config = avcodec_get_hw_config(rawCodec, index);
            if (config == null)
                return false;

            if (config->device_type == deviceType &&
                (config->methods & AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) != 0)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// 创建硬件设备并将其所有权交给解码器上下文。
    /// </summary>
    public static void AttachHardwareDevice(this CodecContext codecContext, AVHWDeviceType deviceType)
    {
        AVBufferRef* deviceContext = null;
        av_hwdevice_ctx_create(ref deviceContext, deviceType, null, null, 0)
            .ThrowIfError($"Failed to create {deviceType} hardware device.");

        if (deviceContext == null)
            throw new InvalidOperationException($"Failed to create {deviceType} hardware device.");

        // hw_device_ctx 设置后由 AVCodecContext 持有并在释放解码器时解除引用。
        AVCodecContext* rawCodecContext = codecContext;
        rawCodecContext->hw_device_ctx = deviceContext;
    }

    /// <summary>
    /// 将 GPU 中的硬件帧复制到 CPU 内存，供现有的 swscale 绘制链路使用。
    /// </summary>
    public static Frame TransferToSoftwareFrame(this Frame hardwareFrame)
    {
        var softwareFrame = new Frame();
        try
        {
            hardwareFrame.TransferToSoftwareFrame(softwareFrame);
            return softwareFrame;
        }
        catch
        {
            softwareFrame.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 只将像素数据传输到可复用的软件帧。这里故意不复制帧属性：
    /// av_frame_copy_props 会向已有目标追加 side data，带有逐帧 HDR
    /// 或类似元数据的流会因此持续增长内存。
    /// </summary>
    public static void TransferToSoftwareFrame(this Frame hardwareFrame, Frame softwareFrame)
    {
        // av_hwframe_transfer_data 可能分配新的目标缓冲区。目标帧会在拖动预览
        // 和总览路径中复用，因此先释放旧缓冲区，避免每次硬件到 CPU 的传输
        // 都保留一张完整分辨率的帧。
        softwareFrame.Unref();
        AVFrame* source = hardwareFrame;
        AVFrame* destination = softwareFrame;
        av_hwframe_transfer_data(destination, source, 0)
            .ThrowIfError("Failed to transfer hardware frame to system memory.");
    }

}
