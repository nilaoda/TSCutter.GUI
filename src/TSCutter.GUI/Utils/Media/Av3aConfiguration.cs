using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Utils.Media;

/// <summary>从 AV3A 压缩帧头恢复缺少的封装配置，不解码音频。</summary>
internal static unsafe class Av3aConfiguration
{
    /// <returns>读取过音轨并恢复配置时返回 true；调用方继续读包前需要重新定位。</returns>
    public static bool Recover(FormatContext input, IEnumerable<int> indices, CancellationToken token,
        Func<int, Exception> unsupportedTrack)
    {
        var recovered = false;
        foreach (var index in indices)
        {
            AVStream* stream = input.Streams[index];
            var parameters = stream->codecpar;
            if (parameters->extradata_size > 0 || input.Streams[index].Codecpar is not
                { IsAv3a: true, HasReliableCodecParameters: true })
                continue;
            token.ThrowIfCancellationRequested();
            var discards = input.Streams.Select(item => ((AVStream*)item)->discard).ToArray();
            var parser = FF.av_parser_init((int)parameters->codec_id);
            var context = FF.avcodec_alloc_context3(null);
            try
            {
                if (parser == null)
                    throw unsupportedTrack(index);
                if (context == null)
                    throw new OutOfMemoryException();
                FFmpegException.Check(FF.avcodec_parameters_to_context(context, parameters));
                // 只读取缺少配置的音轨，从完整帧头取得配置，不解码音频，也不扫描视频负载。
                foreach (var item in input.Streams)
                    ((AVStream*)item)->discard = item.Index == index ? AVDiscard.AVDISCARD_DEFAULT : AVDiscard.AVDISCARD_ALL;
                input.SeekFrame(stream->start_time == FF.AV_NOPTS_VALUE ? 0 : stream->start_time, index);
                foreach (var packet in input.ReadPackets().Take(256))
                {
                    token.ThrowIfCancellationRequested();
                    AVPacket* raw = packet;
                    if (raw->stream_index != index)
                        continue;
                    var data = raw->data;
                    var remaining = raw->size;
                    while (remaining > 0 && context->extradata_size == 0)
                    {
                        byte* parsed = null;
                        var parsedSize = 0;
                        var used = FF.av_parser_parse2(parser, context, &parsed, &parsedSize,
                            data, remaining, raw->pts, raw->dts, raw->pos);
                        FFmpegException.Check(used);
                        if (used == 0)
                            break;
                        data += used;
                        remaining -= used;
                    }
                    if (context->extradata_size > 0)
                        break;
                }
                if (context->extradata_size == 0)
                    throw unsupportedTrack(index);
                FFmpegException.Check(FF.avcodec_parameters_from_context(parameters, context));
                recovered = true;
            }
            finally
            {
                for (var i = 0; i < discards.Length; i++)
                    ((AVStream*)input.Streams[i])->discard = discards[i];
                if (parser != null)
                    FF.av_parser_close(parser);
                if (context != null)
                    FF.avcodec_free_context(&context);
            }
        }
        return recovered;
    }
}
