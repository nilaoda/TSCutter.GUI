using CommunityToolkit.Mvvm.ComponentModel;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.Models;

public sealed partial class TsCheckPidSummaryView : ObservableObject
{
    public TsCheckPidSummaryView(int pid)
    {
        Pid = pid;
        PidText = pid >= 0 ? $"0x{pid:X4}" : "-";
    }

    public int Pid { get; }
    public string PidText { get; }

    [ObservableProperty]
    private string _stream = string.Empty;

    [ObservableProperty]
    private long _packetCount;

    [ObservableProperty]
    private string _packetCountText = "0";

    [ObservableProperty]
    private string _packetPercentageText = "-";

    [ObservableProperty]
    private double _packetPercentage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrors))]
    private int _errorCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings))]
    private int _warningCount;

    public bool HasErrors => ErrorCount > 0;
    public bool HasWarnings => WarningCount > 0;

    public void Update(TsCheckPidProgress progress, long totalPacketCount, TsCheckTextFormatter text)
    {
        PacketCount = progress.PacketCount;
        PacketCountText = progress.PacketCount.ToString("N0");
        PacketPercentage = totalPacketCount > 0
            ? progress.PacketCount * 100.0 / totalPacketCount
            : 0;
        PacketPercentageText = totalPacketCount > 0 && progress.PacketCount > 0
            ? $"{PacketPercentage:0.00}%"
            : "-";
        ErrorCount = progress.ErrorCount;
        WarningCount = progress.WarningCount;
        Stream = text.FormatPidDescription(
            progress.Pid, progress.ProgramNumber, progress.StreamType, progress.MpegAudioLayer,
            progress.SupplementaryStreamType, progress.Language,
            progress.IsPcrPid, progress.IsPmtPid, progress.IsGlobal);
    }
}
