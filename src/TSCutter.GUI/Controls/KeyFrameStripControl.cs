using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using TSCutter.GUI.Utils;
using TSCutter.GUI.ViewModels.Preview;

namespace TSCutter.GUI.Controls;

/// <summary>随容器宽度显示关键帧，保持图片比例，并标出指针所在的 GOP。</summary>
public sealed class KeyFrameStripControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<KeyFrameStripItem>?> ItemsProperty =
        AvaloniaProperty.Register<KeyFrameStripControl, IReadOnlyList<KeyFrameStripItem>?>(nameof(Items));
    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<KeyFrameStripControl, IBrush?>(nameof(TextBrush));
    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<KeyFrameStripControl, IBrush?>(nameof(AccentBrush));
    public static readonly StyledProperty<IBrush?> BorderBrushProperty =
        AvaloniaProperty.Register<KeyFrameStripControl, IBrush?>(nameof(BorderBrush));

    public IReadOnlyList<KeyFrameStripItem>? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? BorderBrush { get => GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }
    public event EventHandler<KeyFrameStripItem>? SeekRequested;
    private static readonly IBrush TypeBadgeBrush = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)).ToImmutable();

    static KeyFrameStripControl() => AffectsRender<KeyFrameStripControl>(ItemsProperty,
        TextBrushProperty, AccentBrushProperty, BorderBrushProperty);

    public KeyFrameStripControl()
    {
        Height = 80;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    internal int VisibleCapacity => Math.Clamp((int)((Bounds.Width - 8) / 108),
        1, KeyFrameStripViewModel.MaximumVisibleCount);

    internal IReadOnlyList<(KeyFrameStripItem Item, Rect Bounds)> GetTiles()
    {
        if (Items is not { Count: > 0 } frames || Bounds.Width < 24)
            return [];
        var count = Math.Min(frames.Count, VisibleCapacity);
        var current = frames.Select((item, index) => (item, index)).FirstOrDefault(pair => pair.item.IsCurrent).index;
        var start = Math.Clamp(current - count / 2, 0, frames.Count - count);
        var width = Math.Min(112, (Bounds.Width - 8) / count);
        var left = (Bounds.Width - count * width) / 2;
        return Enumerable.Range(start, count).Select((index, offset) =>
            (frames[index], new Rect(left + offset * width + 2, 3, width - 4, 73))).ToArray();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        foreach (var (item, bounds) in GetTiles())
        {
            var imageArea = new Rect(bounds.X + 3, bounds.Y + 3, bounds.Width - 6, bounds.Height - 24);
            var badgeArea = imageArea;
            if (item.Bitmap is { } bitmap)
            {
                var size = bitmap.Size;
                var scale = Math.Min(imageArea.Width / size.Width, imageArea.Height / size.Height);
                var destination = new Rect(imageArea.Center.X - size.Width * scale / 2,
                    imageArea.Center.Y - size.Height * scale / 2, size.Width * scale, size.Height * scale);
                context.DrawImage(bitmap, new Rect(size), destination);
                badgeArea = destination;
            }
            if (!item.IsPlaceholder && item.KeyFrameType is { Length: > 0 } frameType)
            {
                var label = new FormattedText(frameType, CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Inter"), 9, Brushes.White);
                var badge = new Rect(badgeArea.Right - label.Width - 8, badgeArea.Top,
                    label.Width + 8, label.Height + 2);
                if (badge.Left >= badgeArea.Left && badge.Bottom <= badgeArea.Bottom)
                {
                    context.DrawRectangle(TypeBadgeBrush, null, badge, 2, 2);
                    context.DrawText(label, new Point(badge.X + 4, badge.Y + 1));
                }
            }
            context.DrawRectangle(null, new Pen(item.IsCurrent ? AccentBrush ?? Brushes.DodgerBlue
                : BorderBrush ?? Brushes.Gray, item.IsCurrent ? 2 : 1), bounds);
            var text = new FormattedText(item.IsPlaceholder ? "…" : CommonUtil.FormatSeconds(item.Timestamp.TotalSeconds),
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Inter"), 11,
                TextBrush ?? Brushes.Gray);
            context.DrawText(text, new Point(bounds.Center.X - text.Width / 2, bounds.Bottom - 19));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEffectivelyEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        foreach (var (item, bounds) in GetTiles())
        {
            if (item.IsPlaceholder || !bounds.Contains(e.GetPosition(this)))
                continue;
            SeekRequested?.Invoke(this, item);
            e.Handled = true;
            break;
        }
    }
}
