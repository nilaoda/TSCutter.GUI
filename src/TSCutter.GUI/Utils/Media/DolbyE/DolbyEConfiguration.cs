using System;
using System.Collections.Generic;
using System.Linq;

namespace TSCutter.GUI.Utils.Media.DolbyE;

/// <summary>杜比 E 声道组的声道构成。</summary>
public enum DolbyEProgramKind
{
    /// <summary>5.1：L R C LFE Ls Rs。</summary>
    Surround51,
    /// <summary>4 声道：L R C S。</summary>
    Quad,
    Stereo,
    Mono,
    /// <summary>7.1：L R C LFE Ls Rs Lrs Rrs。</summary>
    Surround71,
    /// <summary>7.1 屏幕声道：L R C LFE Ls Rs Lc Rc。</summary>
    Surround71Screen
}

/// <summary>
/// 一个声道组：占用的杜比 E 声道（从 1 开始连续编号）及其布局。
/// <see cref="NativeOffsets"/> 按 FFmpeg 原生声道顺序列出各声道相对首声道的偏移，<see cref="Mask"/> 为对应的布局掩码。
/// </summary>
public sealed record DolbyEProgram(int Number, int FirstChannel, DolbyEProgramKind Kind)
{
    public int ChannelCount => Kind switch
    {
        DolbyEProgramKind.Surround51 => 6,
        DolbyEProgramKind.Quad => 4,
        DolbyEProgramKind.Stereo => 2,
        DolbyEProgramKind.Mono => 1,
        _ => 8
    };

    public int LastChannel => FirstChannel + ChannelCount - 1;

    public string LayoutName => Kind switch
    {
        DolbyEProgramKind.Surround51 => "5.1",
        DolbyEProgramKind.Quad => "4.0",
        DolbyEProgramKind.Stereo => "2.0",
        DolbyEProgramKind.Mono => "1.0",
        _ => "7.1"
    };

    public string ChannelNames => Kind switch
    {
        DolbyEProgramKind.Surround51 => "L R C LFE Ls Rs",
        DolbyEProgramKind.Quad => "L R C S",
        DolbyEProgramKind.Stereo => "L R",
        DolbyEProgramKind.Mono => "M",
        DolbyEProgramKind.Surround71 => "L R C LFE Ls Rs Lrs Rrs",
        _ => "L R C LFE Ls Rs Lc Rc"
    };

    // 原生顺序：FL FR FC LFE BL BR FLC FRC BC SL SR。
    public IReadOnlyList<int> NativeOffsets => Kind switch
    {
        DolbyEProgramKind.Surround51 => [0, 1, 2, 3, 4, 5],
        DolbyEProgramKind.Quad => [0, 1, 2, 3],
        DolbyEProgramKind.Stereo => [0, 1],
        DolbyEProgramKind.Mono => [0],
        // 7.1：Lrs/Rrs 为 BL/BR，排在 SL/SR 之前；屏幕声道 Lc/Rc 为 FLC/FRC，同样排在 SL/SR 之前。
        _ => [0, 1, 2, 3, 6, 7, 4, 5]
    };

    public ulong Mask => Kind switch
    {
        DolbyEProgramKind.Surround51 => 0x60F,
        DolbyEProgramKind.Quad => 0x107,
        DolbyEProgramKind.Stereo => 0x3,
        DolbyEProgramKind.Mono => 0x4,
        DolbyEProgramKind.Surround71 => 0x63F,
        _ => 0x6CF
    };
}

/// <summary>24 种声道组配置（program configuration）的声道组与声道分配。</summary>
public static class DolbyEConfiguration
{
    public const int MaxProgramConfig = 23;

    private static readonly int[][] ProgramChannels =
    [
        [6, 2], [6, 1, 1], [4, 4], [4, 2, 2], [4, 2, 1, 1], [4, 1, 1, 1, 1], [2, 2, 2, 2], [2, 2, 2, 1, 1],
        [2, 2, 1, 1, 1, 1], [2, 1, 1, 1, 1, 1, 1], [1, 1, 1, 1, 1, 1, 1, 1], [6], [4, 2], [4, 1, 1], [2, 2, 2],
        [2, 2, 1, 1], [2, 1, 1, 1, 1], [1, 1, 1, 1, 1, 1], [4], [2, 2], [2, 1, 1], [1, 1, 1, 1], [8], [8]
    ];

    private static readonly IReadOnlyList<DolbyEProgram>[] Programs = Enumerable.Range(0, MaxProgramConfig + 1)
        .Select(BuildPrograms).ToArray();

    public static bool IsValid(int programConfig) => programConfig is >= 0 and <= MaxProgramConfig;

    public static IReadOnlyList<DolbyEProgram> GetPrograms(int programConfig) =>
        IsValid(programConfig) ? Programs[programConfig] : [];

    public static int ChannelCount(int programConfig) =>
        IsValid(programConfig) ? ProgramChannels[programConfig].Sum() : 0;

    /// <summary>配置名称，如“5.1 + 2.0”；7.1 屏幕声道配置另行本地化。</summary>
    public static string Name(int programConfig) =>
        string.Join(" + ", GetPrograms(programConfig).Select(program => program.LayoutName));

    /// <summary>
    /// 杜比 E 声道号（从 1 开始）在解码器输出帧中的位置。只有一个声道组的 8 声道配置（22、23）按 FFmpeg 7.1 顺序重排：
    /// 输出依次为声道 1 2 3 4 7 8 5 6；其他配置的输出就是声道号顺序。
    /// </summary>
    public static int DecoderIndex(int programConfig, int channel)
    {
        if (programConfig is 22 or 23)
            return channel switch { 5 => 6, 6 => 7, 7 => 4, 8 => 5, _ => channel - 1 };
        return channel - 1;
    }

    private static IReadOnlyList<DolbyEProgram> BuildPrograms(int programConfig)
    {
        var result = new List<DolbyEProgram>();
        var first = 1;
        foreach (var count in ProgramChannels[programConfig])
        {
            var kind = count switch
            {
                6 => DolbyEProgramKind.Surround51,
                4 => DolbyEProgramKind.Quad,
                2 => DolbyEProgramKind.Stereo,
                1 => DolbyEProgramKind.Mono,
                _ => programConfig == 23 ? DolbyEProgramKind.Surround71Screen : DolbyEProgramKind.Surround71
            };
            result.Add(new DolbyEProgram(result.Count + 1, first, kind));
            first += count;
        }
        return result;
    }

    /// <summary>帧率代码 1–5 对应的帧率文字；解码采样率见 <see cref="DolbyEFrameHeader.SampleRate"/>。</summary>
    public static string FrameRateName(int frameRateCode) => frameRateCode switch
    {
        1 => "23.976",
        2 => "24",
        3 => "25",
        4 => "29.97",
        5 => "30",
        _ => "?"
    };

    internal static int SampleRateForCode(int frameRateCode) => frameRateCode switch
    {
        1 => 42965,
        2 => 43008,
        3 => 44800,
        4 => 53706,
        5 => 53760,
        _ => 0
    };
}
