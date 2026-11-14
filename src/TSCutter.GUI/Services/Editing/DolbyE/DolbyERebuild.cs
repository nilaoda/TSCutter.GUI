using System.Collections.Generic;
using System.Linq;
using TSCutter.GUI.Models.Media;
using TSCutter.GUI.Services.Editing.AudioRebuild;
using TSCutter.GUI.Utils.Media.DolbyE;

namespace TSCutter.GUI.Services.Editing.DolbyE;

/// <summary>杜比 E 转码在重组流中的音轨：杜比 E 轨本身是唯一的来源，每路输出取一个声道组。</summary>
internal static class DolbyERebuild
{
    /// <summary>计划中要转码的杜比 E 轨；未选的杜比 E 轨整条删去，不再转码。</summary>
    public static IEnumerable<AudioRebuildTrack> Tracks(AudioRebuildPlan plan, IReadOnlySet<int> excludedPids)
    {
        foreach (var settings in plan.Tracks.Where(track => track.HasWork && !excludedPids.Contains(track.Pid)))
        {
            var outputs = settings.Outputs.Where(output => output.Enabled).ToArray();
            // 输出声道按设置时读到的声道组配置确定；片中配置变化时由重采样转换到这个布局。
            var specs = outputs.Select(output => new AudioRebuildOutput(output,
                SourceLayout(ProgramTemplate(settings.ProgramConfig, output.Program)), $".P{output.Program}")).ToArray();
            yield return new AudioRebuildTrack([settings.Pid], settings.KeepOriginal, NewEntriesFirst: false, specs,
                (chains, _) => new DolbyETrackTranscoder(chains.Select((chain, index) => (chain, outputs[index].Program)).ToArray()),
                result => result.DolbyETranscoded = true);
        }
    }

    private static DolbyEProgram ProgramTemplate(int programConfig, int number)
    {
        var programs = DolbyEConfiguration.GetPrograms(programConfig);
        return number >= 1 && number <= programs.Count
            ? programs[number - 1]
            : new DolbyEProgram(number, 1, DolbyEProgramKind.Stereo);
    }

    private static AudioSourceLayout SourceLayout(DolbyEProgram program) =>
        new(program.ChannelCount, program.Mask, program.Kind == DolbyEProgramKind.Surround51);
}
