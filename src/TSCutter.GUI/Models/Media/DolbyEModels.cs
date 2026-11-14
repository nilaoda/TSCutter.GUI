using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace TSCutter.GUI.Models.Media;

/// <summary>杜比 E 转码输出的编码。PCM 在 TS 中按 SMPTE 302M 封装。</summary>
public enum DolbyECodec
{
    Ac3,
    Eac3,
    Aac,
    Flac,
    Pcm
}

/// <summary>输出声道：保持声道组原有布局，或下混为立体声、单声道。</summary>
public enum DolbyELayout
{
    Original,
    Stereo,
    Mono
}

/// <summary>一条转码输出：从哪个声道组、用什么编码和声道。</summary>
public sealed class DolbyEOutput
{
    public bool Enabled { get; set; } = true;
    /// <summary>声道组序号，从 1 开始。</summary>
    public int Program { get; set; } = 1;
    public DolbyECodec Codec { get; set; } = DolbyECodec.Ac3;
    public DolbyELayout Layout { get; set; } = DolbyELayout.Original;
    /// <summary>有损编码的码率（kbps）；FLAC 与 PCM 忽略。</summary>
    public int BitrateKbps { get; set; } = 448;
    /// <summary>ISO 639-2 语言代码，空表示不写语言描述符。</summary>
    public string Language { get; set; } = "";

    public DolbyEOutput Snapshot() => new()
    {
        Enabled = Enabled, Program = Program, Codec = Codec, Layout = Layout, BitrateKbps = BitrateKbps,
        Language = Language
    };
}

/// <summary>一条杜比 E 轨（按 PID）的导出处理方式。</summary>
public sealed class DolbyETrackSettings
{
    public int Pid { get; set; }
    /// <summary>设置时读到的声道组配置（决定各声道组的声道布局）；-1 表示未知。</summary>
    public int ProgramConfig { get; set; } = -1;
    /// <summary>false 表示不处理，原样保留杜比 E 轨。</summary>
    public bool Transcode { get; set; }
    /// <summary>转码时是否同时保留原始杜比 E 轨；不保留时转码结果替换原轨。</summary>
    public bool KeepOriginal { get; set; }
    public List<DolbyEOutput> Outputs { get; set; } = [];

    [JsonIgnore]
    public bool HasWork => Transcode && Outputs.Any(output => output.Enabled);

    public DolbyETrackSettings Snapshot() => new()
    {
        Pid = Pid, ProgramConfig = ProgramConfig, Transcode = Transcode, KeepOriginal = KeepOriginal,
        Outputs = Outputs.Select(output => output.Snapshot()).ToList()
    };
}

/// <summary>导出任务的杜比 E 处理计划：只含需要转码的轨道，加入任务时复制快照。</summary>
public sealed class DolbyEExportPlan
{
    public List<DolbyETrackSettings> Tracks { get; set; } = [];

    [JsonIgnore]
    public bool HasWork => Tracks.Exists(track => track.HasWork);

    public DolbyEExportPlan Snapshot() => new() { Tracks = Tracks.Select(track => track.Snapshot()).ToList() };

    public static DolbyEExportPlan? Create(IEnumerable<DolbyETrackSettings> tracks)
    {
        var plan = new DolbyEExportPlan { Tracks = tracks.Where(track => track.HasWork).Select(track => track.Snapshot()).ToList() };
        return plan.HasWork ? plan : null;
    }
}

/// <summary>编码的码率选项与默认值。</summary>
public static class DolbyECodecInfo
{
    public static bool IsLossless(DolbyECodec codec) => codec is DolbyECodec.Flac or DolbyECodec.Pcm;

    /// <summary>编码支持的最多声道数：有损编码（AC3、E-AC3、AAC）最多 5.1，7.1 声道组下混为 5.1。</summary>
    public static int MaxChannels(DolbyECodec codec) => IsLossless(codec) ? 8 : 6;

    /// <summary>输出的实际声道数：受编码上限约束；PCM 按 302M 的声道对写入，单声道写成两路。</summary>
    public static int OutputChannels(DolbyECodec codec, DolbyELayout layout, int programChannels)
    {
        var channels = layout switch
        {
            DolbyELayout.Stereo => 2,
            DolbyELayout.Mono => 1,
            _ => Math.Min(programChannels, MaxChannels(codec))
        };
        return codec == DolbyECodec.Pcm && channels == 1 ? 2 : channels;
    }

    public static IReadOnlyList<int> Bitrates(DolbyECodec codec, int channels) => codec switch
    {
        DolbyECodec.Ac3 => channels switch
        {
            1 => [64, 96, 128, 192],
            2 => [128, 192, 224, 256, 320],
            <= 4 => [256, 384, 448],
            _ => [384, 448, 640]
        },
        DolbyECodec.Eac3 => channels switch
        {
            1 => [64, 96, 128],
            2 => [128, 192, 224, 256],
            <= 4 => [256, 384, 448],
            _ => [384, 448, 640, 768]
        },
        DolbyECodec.Aac => channels switch
        {
            1 => [64, 96, 128],
            2 => [128, 160, 192, 256, 320],
            <= 4 => [192, 256, 320],
            _ => [256, 320, 384, 512]
        },
        _ => []
    };

    public static int DefaultBitrate(DolbyECodec codec, int channels) => codec switch
    {
        DolbyECodec.Ac3 => channels switch { 1 => 96, 2 => 192, <= 4 => 384, _ => 448 },
        DolbyECodec.Eac3 => channels switch { 1 => 96, 2 => 192, <= 4 => 384, _ => 448 },
        DolbyECodec.Aac => channels switch { 1 => 96, 2 => 192, <= 4 => 256, _ => 384 },
        _ => 0
    };

    /// <summary>码率不在当前编码与声道的选项中时换成默认值。</summary>
    public static int NormalizeBitrate(DolbyECodec codec, int channels, int bitrate)
    {
        var options = Bitrates(codec, channels);
        return options.Count == 0 || options.Contains(bitrate) ? bitrate : DefaultBitrate(codec, channels);
    }

    public static string Name(DolbyECodec codec) => codec switch
    {
        DolbyECodec.Ac3 => "AC3",
        DolbyECodec.Eac3 => "E-AC3",
        DolbyECodec.Aac => "AAC",
        DolbyECodec.Flac => "FLAC",
        _ => "PCM"
    };
}
