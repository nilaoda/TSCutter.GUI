namespace TSCutter.GUI.Player;

/// <summary>播放会话的运行状态。</summary>
public enum PlaybackState
{
    /// <summary>会话尚未开始或已停止。</summary>
    Idle,
    /// <summary>正在建立输入、解码锚点或预滚。</summary>
    Starting,
    /// <summary>按主时钟连续呈现画面。</summary>
    Playing,
    /// <summary>时钟冻结、呈现暂停，会话资源保留。</summary>
    Paused,
    /// <summary>到达文件尾或播放区间终点，保留最后显示画面。</summary>
    Ended,
}
