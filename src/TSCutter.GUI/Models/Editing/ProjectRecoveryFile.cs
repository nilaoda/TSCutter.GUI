using System;

namespace TSCutter.GUI.Models.Editing;

/// <summary>
/// 自动保存的恢复文件：当前剪辑状态的工程快照，加上它对应的工程文件路径。
/// 只写在应用数据目录，正常关闭、保存工程或放弃修改时删除；下次启动仍存在说明上次没有正常退出。
/// </summary>
public sealed class ProjectRecoveryFile
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public DateTime SavedUtc { get; set; }
    /// <summary>已打开或已保存的工程文件路径；从视频开始、尚未保存为工程时为 null。</summary>
    public string? ProjectPath { get; set; }
    public CutterProject Project { get; set; } = new();
}
