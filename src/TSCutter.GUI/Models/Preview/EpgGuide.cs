using System;
using System.Collections.Generic;

namespace TSCutter.GUI.Models.Preview;

/// <summary>EPG（EIT 现在/下一个节目表）中的一个节目。开始时间为 UTC，没有排期时为 null。</summary>
public sealed record EpgEvent(int EventId, string Title, string Description, DateTimeOffset? Start, TimeSpan Duration)
{
    public DateTimeOffset? End => Start + Duration;
}

/// <summary>
/// 录制中一个节目所占的时间轴范围（秒）。范围取自 EPG 切换“现在播出”节目的时刻，
/// 广播按排期切换，可能与实际播出相差数分钟。录制开始时已在播出的节目从时间轴起点算起，
/// 录制结束时仍在播出的节目算到时间轴终点。
/// </summary>
public sealed record EpgProgram(EpgEvent Event, double StartSeconds, double EndSeconds,
    bool StartsAtRecordingStart, bool EndsAtRecordingEnd)
{
    public string Title => Event.Title;
}

/// <summary>录制文件的 EPG：录制中的节目分段，以及读到的全部节目（含录制前后的现在/下一个节目）。</summary>
public sealed record EpgGuide(IReadOnlyList<EpgProgram> Programs, IReadOnlyList<EpgEvent> Events, TimeSpan? LocalOffset)
{
    public static EpgGuide Empty { get; } = new([], [], null);
    public bool HasPrograms => Programs.Count > 0;
}
