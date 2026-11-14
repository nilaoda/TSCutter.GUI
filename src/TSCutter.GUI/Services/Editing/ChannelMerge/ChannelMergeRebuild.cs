using System.Collections.Generic;
using System.Linq;
using TSCutter.GUI.Models.Media;
using TSCutter.GUI.Services.Editing.AudioRebuild;

namespace TSCutter.GUI.Services.Editing.ChannelMerge;

/// <summary>
/// 多轨合并在重组流中的音轨：三条来源合成一条 5.1，前置来源为锚定 PID；新音轨在节目表中排在前置来源之前，
/// 播放器默认选它。
/// </summary>
internal static class ChannelMergeRebuild
{
    /// <summary>
    /// 计划中要合并的音轨。前置来源未选时不合并；替换来源的合并连同其他来源一并删去（它们已并入新音轨），
    /// 这些来源加入 <paramref name="excludedPids"/>。
    /// </summary>
    public static IEnumerable<AudioRebuildTrack> Tracks(AudioRebuildPlan plan, ISet<int> excludedPids)
    {
        var tracks = new List<AudioRebuildTrack>();
        foreach (var settings in plan.Merges.Where(merge => merge.HasWork))
        {
            if (excludedPids.Contains(settings.AnchorPid))
            {
                if (!settings.KeepOriginal)
                    excludedPids.UnionWith(settings.SourcePids);
                continue;
            }
            var outputs = settings.Outputs.Where(output => output.Enabled)
                .Select(output => new AudioRebuildOutput(output, AudioSourceLayout.Surround51, ".51")).ToArray();
            var swaps = settings.Sources.Select(source => source.Swap).ToArray();
            tracks.Add(new AudioRebuildTrack(settings.SourcePids.ToArray(), settings.KeepOriginal, NewEntriesFirst: true, outputs,
                (chains, sources) => new ChannelMergeTranscoder(chains, sources.Select((source, index) => (source, swaps[index])).ToArray()),
                result => result.ChannelsMerged = true));
        }
        return tracks;
    }
}
