using System;
using Avalonia;
using Avalonia.Controls;

namespace TSCutter.GUI.Themes;

/// <summary>标题相对整窗居中，两侧空间不足时省略文字，避免覆盖图标和窗口按钮。</summary>
public sealed class Windows8TitlePanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count != 3)
            return base.MeasureOverride(availableSize);

        var icon = Children[0];
        var title = Children[1];
        var buttons = Children[2];
        icon.Measure(availableSize);
        buttons.Measure(availableSize);
        var inset = Math.Max(icon.DesiredSize.Width, buttons.DesiredSize.Width) + 4;
        title.Measure(new Size(Math.Max(0, availableSize.Width - inset * 2), availableSize.Height));
        return new Size(inset * 2 + title.DesiredSize.Width,
            Math.Max(title.DesiredSize.Height, Math.Max(icon.DesiredSize.Height, buttons.DesiredSize.Height)));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count != 3)
            return base.ArrangeOverride(finalSize);

        var icon = Children[0];
        var title = Children[1];
        var buttons = Children[2];
        var iconWidth = Math.Min(icon.DesiredSize.Width, finalSize.Width);
        var buttonWidth = Math.Min(buttons.DesiredSize.Width, finalSize.Width);
        var inset = Math.Min(Math.Max(iconWidth, buttonWidth) + 4, finalSize.Width / 2);
        icon.Arrange(new Rect(0, 0, iconWidth, finalSize.Height));
        title.Arrange(new Rect(inset, 0, Math.Max(0, finalSize.Width - inset * 2), finalSize.Height));
        buttons.Arrange(new Rect(finalSize.Width - buttonWidth, 0, buttonWidth, finalSize.Height));
        return finalSize;
    }
}
