namespace TSCutter.GUI.Models.Preview;

/// <summary>容器或光盘标题中的一个章节，起止为时间轴秒数；Number 从 1 起。</summary>
public sealed record MediaChapter(int Number, string? Title, double StartSeconds, double EndSeconds);
