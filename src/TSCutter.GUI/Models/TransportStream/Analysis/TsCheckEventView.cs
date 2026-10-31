using CommunityToolkit.Mvvm.ComponentModel;
using TSCutter.GUI.Utils.TransportStream.Analysis;


namespace TSCutter.GUI.Models.TransportStream.Analysis;

public sealed partial class TsCheckEventView : ObservableObject
{
    public TsCheckEventView(TsCheckEvent item, TsCheckTextFormatter text, TsCheckResult? result = null)
    {
        Item = item;
        Severity = text.FormatSeverity(item.Severity);
        SourceTime = text.FormatEventSourceTime(item);
        ZeroBasedTime = text.FormatEventZeroBasedTime(item);
        Type = text.FormatEventType(item);
        Pid = item.PidText;
        Stream = text.FormatPidDescription(item, result);
        RefreshAggregation();
        Message = text.FormatEventMessage(item);
    }

    public TsCheckEvent Item { get; }
    public string Severity { get; }
    public string SourceTime { get; }
    public string ZeroBasedTime { get; }
    public string Type { get; }
    public string Pid { get; }
    public string Stream { get; }
    public string Packet => Item.StartPacket == Item.EndPacket
        ? Item.StartPacket.ToString("N0")
        : $"{Item.StartPacket:N0}-{Item.EndPacket:N0}";
    public string Message { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Packet))]
    private int _occurrences;

    // 由 UI 进度回调刷新；没有新增合并时不发送绑定通知。
    internal void RefreshAggregation() => Occurrences = Item.Occurrences;
}
