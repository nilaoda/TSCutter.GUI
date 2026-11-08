namespace TSCutter.GUI.Models.Editing;

/// <summary>片段列表的含义：保留（导出片段本身，默认）或删除（片段是要从视频中删除的范围，导出其余部分）。</summary>
public enum ClipListMode
{
    Keep,
    Remove
}
