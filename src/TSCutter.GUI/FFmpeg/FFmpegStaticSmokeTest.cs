#if TSCUTTER_FFMPEG_STATIC
using System;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.Utils.Media;

namespace TSCutter.GUI.FFmpeg;

// Runs against the final published EXE on CI, before initializing the desktop UI.
internal static class FFmpegStaticSmokeTest
{
    internal static unsafe int Run(string path)
    {
        try
        {
            FFmpegNativeBootstrapper.Initialize();
            // Exercise the winpthreads clock initialization used while seeking.
            FFmpegException.Check(NativeMethods.av_usleep(1_000));
            Console.WriteLine("smoke: native sleep ok");
            if (NativeMethods.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264) == null
                || NativeMethods.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_HEVC) == null
                || NativeMethods.avcodec_find_encoder_by_name("libx264") == null
                || NativeMethods.avcodec_find_encoder_by_name("libx265") == null
                || NativeMethods.avfilter_get_by_name("subtitles") == null)
                throw new InvalidOperationException("Required FFmpeg codecs or subtitle filter are unavailable.");

            var ass = NativeMethods.ass_library_init();
            if (ass == 0) throw new InvalidOperationException("libass initialization failed.");
            try
            {
                var renderer = NativeMethods.ass_renderer_init(ass);
                if (renderer == 0) throw new InvalidOperationException("libass renderer initialization failed.");
                NativeMethods.ass_renderer_done(renderer);
            }
            finally { NativeMethods.ass_library_done(ass); }

            NativeMethods.bd_get_version(out var major, out var minor, out _);
            if (major != 1 || minor < 5) throw new InvalidOperationException("libbluray >= 1.5 is required.");
            Console.WriteLine("smoke: bluray ok");
            VerifyLogCallback();
            Console.WriteLine("smoke: native log callback ok");
            Console.WriteLine("smoke: x264 verify begin");
            VerifyEncoder("libx264");
            Console.WriteLine("smoke: x264 ok");
            Console.WriteLine("smoke: x265 verify begin");
            VerifyEncoder("libx265");
            VerifyEncoder("libx265", AVPixelFormat.AV_PIX_FMT_YUV420P10LE);
            VerifyEncoder("libx265", AVPixelFormat.AV_PIX_FMT_YUV420P12LE);
            Console.WriteLine("smoke: x265 8/10/12-bit ok");
            using var input = FormatContext.OpenInputUrl(path);
            input.LoadStreamInfo();
            var stream = input.GetAudioStream();
            using var decoder = new CodecContext(Codec.FindDecoderById(stream.Codecpar!.CodecId));
            decoder.FillParameters(stream.Codecpar);
            decoder.Open();
            using var frame = new Frame();
            foreach (var packet in input.ReadPackets())
            {
                if (packet.StreamIndex != stream.Index) continue;
                foreach (var decoded in decoder.DecodePacket(packet, frame))
                    if (((AVFrame*)decoded)->nb_samples > 0)
                    {
                        Console.WriteLine("FFmpeg static runtime verified.");
                        return 0;
                    }
            }
            throw new InvalidOperationException("The smoke-test audio did not decode.");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static unsafe void VerifyLogCallback()
    {
        var codec = NativeMethods.avcodec_find_encoder_by_name("libx264");
        var context = NativeMethods.avcodec_alloc_context3(codec);
        if (context == null) throw new OutOfMemoryException();
        try
        {
            context->width = context->height = 64;
            context->time_base = new AVRational { num = 1, den = 25 };
            context->pix_fmt = AVPixelFormat.AV_PIX_FMT_NONE;
            using var capture = new FFmpegLogCapture();
            if (NativeMethods.avcodec_open2(context, codec, null) >= 0 || capture.Messages.Length == 0)
                throw new InvalidOperationException("The native FFmpeg error callback did not capture an invalid pixel format.");
        }
        finally { NativeMethods.avcodec_free_context(&context); }
    }

    private static unsafe void VerifyEncoder(string name, AVPixelFormat format = AVPixelFormat.AV_PIX_FMT_YUV420P)
    {
        Console.WriteLine($"smoke: {name} alloc begin");
        var codec = NativeMethods.avcodec_find_encoder_by_name(name);
        var context = NativeMethods.avcodec_alloc_context3(codec);
        if (context == null) throw new OutOfMemoryException();
        Console.WriteLine($"smoke: {name} alloc ok");
        try
        {
            context->width = context->height = 64;
            context->pix_fmt = format;
            context->time_base = new AVRational { num = 1, den = 25 };
            context->thread_count = 1;
            FFmpegException.Check(NativeMethods.av_opt_set(context->priv_data, "preset", "ultrafast", 0));
            FFmpegException.Check(NativeMethods.av_opt_set(context->priv_data, "tune", "zerolatency", 0));
            if (name == "libx265")
                FFmpegException.Check(NativeMethods.av_opt_set(context->priv_data, "x265-params", "pools=none:frame-threads=1:log-level=error", 0));
            Console.WriteLine($"smoke: {name} open begin");
            FFmpegException.Check(NativeMethods.avcodec_open2(context, codec, null));
            Console.WriteLine($"smoke: {name} open ok");
            using var frame = Frame.CreateVideo(64, 64, context->pix_fmt);
            for (uint plane = 0; plane < 3; plane++)
            {
                var length = frame.Linesize[plane] * (plane == 0 ? 64 : 32);
                if (format == AVPixelFormat.AV_PIX_FMT_YUV420P)
                    new Span<byte>(frame.Data[plane], length).Fill(128);
                else
                    new Span<ushort>(frame.Data[plane], length / 2).Fill(
                        format == AVPixelFormat.AV_PIX_FMT_YUV420P10LE ? (ushort)512 : (ushort)2048);
            }
            ((AVFrame*)frame)->pts = 0;
            using var packet = new Packet();
            Console.WriteLine($"smoke: {name} send begin");
            FFmpegException.Check(NativeMethods.avcodec_send_frame(context, frame));
            var result = NativeMethods.avcodec_receive_packet(context, packet);
            if (result == NativeMethods.AVERROR_EAGAIN)
            {
                FFmpegException.Check(NativeMethods.avcodec_send_frame(context, null));
                result = NativeMethods.avcodec_receive_packet(context, packet);
            }
            FFmpegException.Check(result);
            if (((AVPacket*)packet)->size <= 0) throw new InvalidOperationException($"{name} did not encode a packet.");
            Console.WriteLine($"smoke: {name} encode ok");
        }
        finally { NativeMethods.avcodec_free_context(&context); }
    }
}
#endif
