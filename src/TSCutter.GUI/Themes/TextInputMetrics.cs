using System;
using Avalonia.Data.Converters;

namespace TSCutter.GUI.Themes;

public static class TextInputMetrics
{
    // 为中文回退字体预留行高，避免空值、拉丁文字和中文之间切换时改变布局。
    public static FuncValueConverter<double, double> LineHeight { get; } =
        new(fontSize => Math.Ceiling(fontSize * 1.5));
}
