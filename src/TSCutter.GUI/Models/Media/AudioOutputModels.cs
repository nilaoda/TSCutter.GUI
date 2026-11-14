using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace TSCutter.GUI.Models.Media;

/// <summary>重建音轨（杜比 E 转码、多轨合并）的输出编码。PCM 在 TS 中按 SMPTE 302M 封装。</summary>
public enum AudioOutputCodec
{
    Ac3,
    Eac3,
    Aac,
    Flac,
    Pcm
}

/// <summary>输出声道：保持来源的声道布局，或下混为立体声、单声道。</summary>
public enum AudioOutputLayout
{
    Original,
    Stereo,
    Mono
}

/// <summary>一路重建输出的编码设置。</summary>
public class AudioOutput
{
    public bool Enabled { get; set; } = true;
    public AudioOutputCodec Codec { get; set; } = AudioOutputCodec.Ac3;
    public AudioOutputLayout Layout { get; set; } = AudioOutputLayout.Original;
    /// <summary>有损编码的码率（kbps）；FLAC 与 PCM 忽略。</summary>
    public int BitrateKbps { get; set; } = 448;
    /// <summary>ISO 639-2 语言代码，空表示不写语言描述符。</summary>
    public string Language { get; set; } = "";

    /// <summary>把编码设置复制到另一个输出（杜比 E 输出另带声道组）。</summary>
    public T CopyTo<T>(T target) where T : AudioOutput
    {
        target.Enabled = Enabled;
        target.Codec = Codec;
        target.Layout = Layout;
        target.BitrateKbps = BitrateKbps;
        target.Language = Language;
        return target;
    }

    public AudioOutput SnapshotOutput() => CopyTo(new AudioOutput());

    /// <summary>比较导出结果是否相同用的文本（只含影响输出的字段）。</summary>
    [JsonIgnore]
    public string Signature => $"{Codec},{Layout},{BitrateKbps},{Language}";
}

/// <summary>
/// 导出任务的音轨重建计划：杜比 E 转码与多轨合并。只含需要处理的项，加入任务时复制快照。
/// </summary>
public sealed class AudioRebuildPlan
{
    public List<DolbyETrackSettings> Tracks { get; set; } = [];
    public List<ChannelMergeSettings> Merges { get; set; } = [];

    [JsonIgnore]
    public bool HasWork => Tracks.Exists(track => track.HasWork) || Merges.Exists(merge => merge.HasWork);

    [JsonIgnore]
    public bool HasDolbyE => Tracks.Exists(track => track.HasWork);

    [JsonIgnore]
    public bool HasMerges => Merges.Exists(merge => merge.HasWork);

    public AudioRebuildPlan Snapshot() => new()
    {
        Tracks = Tracks.Select(track => track.Snapshot()).ToList(),
        Merges = Merges.Select(merge => merge.Snapshot()).ToList()
    };

    public static AudioRebuildPlan? Create(IEnumerable<DolbyETrackSettings> tracks, IEnumerable<ChannelMergeSettings>? merges = null)
    {
        var plan = new AudioRebuildPlan
        {
            Tracks = tracks.Where(track => track.HasWork).Select(track => track.Snapshot()).ToList(),
            Merges = (merges ?? []).Where(merge => merge.HasWork).Select(merge => merge.Snapshot()).ToList()
        };
        return plan.HasWork ? plan : null;
    }

    /// <summary>比较两个计划的导出结果是否相同用的文本。</summary>
    public static string Signature(AudioRebuildPlan? plan) => plan is null ? "" : string.Join("|",
        plan.Tracks.Select(track => $"E{track.Pid}:{track.KeepOriginal}:" + string.Join(";", track.Outputs
            .Where(output => output.Enabled).Select(output => $"{output.Program},{output.Signature}")))
            .Concat(plan.Merges.Select(merge => merge.Signature)));
}

/// <summary>编码的码率选项与默认值。</summary>
public static class AudioOutputCodecInfo
{
    public static bool IsLossless(AudioOutputCodec codec) => codec is AudioOutputCodec.Flac or AudioOutputCodec.Pcm;

    /// <summary>编码支持的最多声道数：有损编码（AC3、E-AC3、AAC）最多 5.1，7.1 来源下混为 5.1。</summary>
    public static int MaxChannels(AudioOutputCodec codec) => IsLossless(codec) ? 8 : 6;

    /// <summary>输出的实际声道数：受编码上限约束；PCM 按 302M 的声道对写入，单声道写成两路。</summary>
    public static int OutputChannels(AudioOutputCodec codec, AudioOutputLayout layout, int sourceChannels)
    {
        var channels = layout switch
        {
            AudioOutputLayout.Stereo => 2,
            AudioOutputLayout.Mono => 1,
            _ => Math.Min(sourceChannels, MaxChannels(codec))
        };
        return codec == AudioOutputCodec.Pcm && channels == 1 ? 2 : channels;
    }

    public static IReadOnlyList<int> Bitrates(AudioOutputCodec codec, int channels) => codec switch
    {
        AudioOutputCodec.Ac3 => channels switch
        {
            1 => [64, 96, 128, 192],
            2 => [128, 192, 224, 256, 320],
            <= 4 => [256, 384, 448],
            _ => [384, 448, 640]
        },
        AudioOutputCodec.Eac3 => channels switch
        {
            1 => [64, 96, 128],
            2 => [128, 192, 224, 256],
            <= 4 => [256, 384, 448],
            _ => [384, 448, 640, 768]
        },
        AudioOutputCodec.Aac => channels switch
        {
            1 => [64, 96, 128],
            2 => [128, 160, 192, 256, 320],
            <= 4 => [192, 256, 320],
            _ => [256, 320, 384, 512]
        },
        _ => []
    };

    public static int DefaultBitrate(AudioOutputCodec codec, int channels) => codec switch
    {
        AudioOutputCodec.Ac3 => channels switch { 1 => 96, 2 => 192, <= 4 => 384, _ => 448 },
        AudioOutputCodec.Eac3 => channels switch { 1 => 96, 2 => 192, <= 4 => 384, _ => 448 },
        AudioOutputCodec.Aac => channels switch { 1 => 96, 2 => 192, <= 4 => 256, _ => 384 },
        _ => 0
    };

    /// <summary>码率不在当前编码与声道的选项中时换成默认值。</summary>
    public static int NormalizeBitrate(AudioOutputCodec codec, int channels, int bitrate)
    {
        var options = Bitrates(codec, channels);
        return options.Count == 0 || options.Contains(bitrate) ? bitrate : DefaultBitrate(codec, channels);
    }

    public static string Name(AudioOutputCodec codec) => codec switch
    {
        AudioOutputCodec.Ac3 => "AC3",
        AudioOutputCodec.Eac3 => "E-AC3",
        AudioOutputCodec.Aac => "AAC",
        AudioOutputCodec.Flac => "FLAC",
        _ => "PCM"
    };

    /// <summary>规范化语言代码：三个小写字母，否则为空。</summary>
    public static string NormalizeLanguage(string? language)
    {
        var text = (language ?? "").Trim().ToLowerInvariant();
        return text.Length == 3 && text.All(char.IsAsciiLetterLower) ? text : "";
    }
}
