using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TSCutter.GUI.FFmpeg;

namespace TSCutter.GUI.Utils.Media.DolbyE;

/// <summary>一条杜比 E 轨：所在 302M 轨的 PID、语言，以及开头读到的帧头。</summary>
public sealed record DolbyETrackInfo(int StreamIndex, int Pid, string Language, DolbyEFrameHeader Header, int AesPair)
{
    public IReadOnlyList<DolbyEProgram> Programs => DolbyEConfiguration.GetPrograms(Header.ProgramConfig);
}

/// <summary>
/// 打开文件时识别 302M 轨中的杜比 E：另开一个输入，只读开头的 302M 包并拆帧读帧头，不解码音频。
/// 片中声道组配置变化不在这里检查，导出时逐帧按实际配置处理。
/// </summary>
internal static class DolbyEProbe
{
    // 每条轨读到两个可解析的帧头即可确认；读包上限防止长时间没有 302M 数据的文件拖慢打开。
    private const int HeadersNeeded = 2;
    private const int MaxPackets = 40_000;
    private const int MaxCarrierPackets = 200;

    /// <summary>按 PID 返回识别出的杜比 E 轨；没有 302M 轨或读取失败时返回空。</summary>
    public static IReadOnlyDictionary<int, DolbyETrackInfo> Probe(FormatContext opened, string path, long tsSyncOffset,
        CancellationToken token)
    {
        if (!opened.Streams.Any(DolbyEDecoder.IsCarrier) || !DolbyEDecoder.IsDecoderAvailable)
            return new Dictionary<int, DolbyETrackInfo>();
        try
        {
            using var input = InterruptibleInputFormatContext.Open(path, () => token.IsCancellationRequested, tsSyncOffset);
            input.LoadStreamInfo();
            return ProbeCore(input, token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Dolby E probe failed: {exception.Message}");
            return new Dictionary<int, DolbyETrackInfo>();
        }
    }

    internal static IReadOnlyDictionary<int, DolbyETrackInfo> ProbeCore(FormatContext input, CancellationToken token)
    {
        var decoders = new Dictionary<int, (MediaStream Stream, DolbyEDecoder Decoder, int Headers, int Packets)>();
        try
        {
            foreach (var stream in input.Streams.Where(DolbyEDecoder.IsCarrier))
                if (DolbyEDecoder.TryCreate(stream, headersOnly: true) is { } decoder)
                    decoders[stream.Index] = (stream, decoder, 0, 0);
            var result = new Dictionary<int, DolbyETrackInfo>();
            var packets = 0;
            foreach (var packet in input.ReadPackets())
            {
                token.ThrowIfCancellationRequested();
                if (++packets > MaxPackets || decoders.Count == 0)
                    break;
                if (!decoders.TryGetValue(packet.StreamIndex, out var entry))
                    continue;
                entry.Decoder.Decode(packet, null);
                entry.Packets++;
                entry.Headers = entry.Decoder.HeaderCount;
                decoders[packet.StreamIndex] = entry;
                if (entry.Headers >= HeadersNeeded && entry.Decoder.LastHeader is { } found)
                {
                    entry.Stream.Metadata.TryGetValue("language", out var language);
                    result[entry.Stream.Id] = new DolbyETrackInfo(entry.Stream.Index, entry.Stream.Id, language, found,
                        entry.Decoder.AesPair);
                    entry.Decoder.Dispose();
                    decoders.Remove(packet.StreamIndex);
                }
                else if (entry.Packets >= MaxCarrierPackets)
                {
                    entry.Decoder.Dispose();
                    decoders.Remove(packet.StreamIndex);
                }
            }
            // 读到文件尾或达到上限时，读到过一个帧头的轨道也算杜比 E（片段很短时）。
            foreach (var (_, entry) in decoders)
                if (entry.Decoder.LastHeader is { } header)
                {
                    entry.Stream.Metadata.TryGetValue("language", out var language);
                    result[entry.Stream.Id] = new DolbyETrackInfo(entry.Stream.Index, entry.Stream.Id, language, header,
                        entry.Decoder.AesPair);
                }
            return result;
        }
        finally
        {
            foreach (var (_, entry) in decoders)
                entry.Decoder.Dispose();
        }
    }
}
