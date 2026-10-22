using System;
using System.Text;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;

namespace TSCutter.GUI.Utils.Media;

public static partial class MediaInfoBuilder
{
    private static unsafe bool WriteAv3aChannels(StringBuilder sb, CodecParameters parameters)
    {
        if (!parameters.IsAv3a || !parameters.HasReliableCodecParameters) return false;
        AVCodecParameters* raw = parameters;
        return raw->extradata != null && raw->extradata_size == 10 &&
            WriteAv3aChannels(sb, new ReadOnlySpan<byte>(raw->extradata, raw->extradata_size));
    }

    internal static bool WriteAv3aChannels(StringBuilder sb, ReadOnlySpan<byte> config)
    {
        // 共享运行库将 dca3 或 AATF 帧头整理为十字节配置；不解析任意私有数据。
        if (config.Length != 10 || config[0] != 2 || config[1] >= 9 || config[2] > 1 ||
            config[3] > 3 || config[7] > 2 || (config[8] == 0 && config[9] == 0)) return false;

        var contentType = config[3];
        var channels = 0;
        var layout = "";
        if (contentType is 0 or 2)
        {
            (channels, layout) = config[4] switch
            {
                0 => (1, "mono"), 1 => (2, "stereo"), 2 => (6, "5.1"), 3 => (8, "7.1"),
                6 => (4, "4.0"), 7 => (8, "5.1.2"), 8 => (10, "5.1.4"),
                9 => (10, "7.1.2"), 10 => (12, "7.1.4"), _ => (0, "")
            };
            if (channels == 0 || (contentType == 2 && config[4] == 0)) return false;
        }
        if (contentType is 1 or 2 && config[5] is 0 or > 128) return false;
        if (contentType == 3)
        {
            if (config[6] is < 1 or > 3) return false;
            channels = (config[6] + 1) * (config[6] + 1);
            layout = $"Ambisonics (order {config[6]})";
        }

        AppendField(sb, "Format profile", contentType switch
        {
            0 => "Channel-based", 1 => "Objects", 2 => "Channels + Objects", _ => "HOA"
        });
        // 混合音频的原生声道数包含对象，声道床与对象必须分别说明。
        if (channels > 0)
        {
            AppendField(sb, "Channel(s)", channels + " channels");
            AppendField(sb, "Channel layout", layout);
        }
        if (contentType is 1 or 2) AppendField(sb, "Number of objects", config[5].ToString());
        return true;
    }
}
