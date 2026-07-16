using TSCutter.GUI.Utils;

namespace TSCutter.GUI.Models;

public sealed class TsCheckEventView
{
    public TsCheckEventView(TsCheckEvent item, TsCheckTextFormatter text, TsCheckResult? result = null)
    {
        Severity = text.FormatSeverity(item.Severity);
        SourceTime = text.FormatEventSourceTime(item);
        ZeroBasedTime = text.FormatEventZeroBasedTime(item);
        Type = text.FormatEventType(item);
        Pid = item.PidText;
        Stream = text.FormatPidDescription(item, result);
        Packet = item.StartPacket == item.EndPacket
            ? item.StartPacket.ToString("N0")
            : $"{item.StartPacket:N0}-{item.EndPacket:N0}";
        Message = text.FormatEventMessage(item);
    }

    public string Severity { get; }
    public string SourceTime { get; }
    public string ZeroBasedTime { get; }
    public string Type { get; }
    public string Pid { get; }
    public string Stream { get; }
    public string Packet { get; }
    public string Message { get; }
}
