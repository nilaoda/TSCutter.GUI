namespace TSCutter.GUI.Models;

public sealed class TsCheckTimelineStreamView(int pid, string displayText)
{
    public int Pid { get; } = pid;
    public string DisplayText { get; } = displayText;
}
