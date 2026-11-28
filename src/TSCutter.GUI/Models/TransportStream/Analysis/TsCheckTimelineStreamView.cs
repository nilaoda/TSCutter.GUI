namespace TSCutter.GUI.Models.TransportStream.Analysis;

public sealed class TsCheckTimelineStreamView(int pid, string displayText)
{
    public const int AvPayloadSeries = -2;
    public const int TotalTsSeries = -1;

    public int Pid { get; } = pid;
    public string DisplayText { get; } = displayText;
}
