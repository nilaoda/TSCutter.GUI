using System;
using TSCutter.GUI.FFmpeg;

namespace TSCutter.GUI.Utils.Media.DolbyE;

/// <summary>
/// 播放与波形用：只输出杜比 E 中的一个声道组。帧为 fltp、原生声道顺序，采样率随帧率（如 44.8 kHz），
/// 由播放的重采样统一转为 48 kHz。片中配置变化后没有这个声道组的帧不输出。
/// </summary>
internal sealed class DolbyEProgramDecoder : IDisposable
{
    private readonly DolbyEDecoder decoder;
    private readonly int program;
    private readonly Frame programFrame = new();

    private DolbyEProgramDecoder(DolbyEDecoder decoder, int program)
    {
        this.decoder = decoder;
        this.program = program;
    }

    public static DolbyEProgramDecoder? TryCreate(MediaStream stream, int program) =>
        DolbyEDecoder.TryCreate(stream) is { } decoder ? new DolbyEProgramDecoder(decoder, program) : null;

    public void Flush() => decoder.Flush();

    /// <summary>送入一个 302M 包（null 表示结束）；回调中的帧只在回调期间有效。</summary>
    public void Decode(Packet? packet, Action<Frame> onFrame) =>
        decoder.Decode(packet, (frame, header) =>
        {
            if (DolbyEDecoder.TryExtractProgram(frame, header, program, programFrame))
            {
                onFrame(programFrame);
                programFrame.Unref();
            }
        });

    public void Dispose()
    {
        programFrame.Dispose();
        decoder.Dispose();
    }
}
