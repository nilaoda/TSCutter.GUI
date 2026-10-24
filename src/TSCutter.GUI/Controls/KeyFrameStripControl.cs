using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using TSCutter.GUI.Models.Editing;
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
    public static readonly StyledProperty<IReadOnlyList<KeyFrameRetentionRange>?> RangesProperty =
        AvaloniaProperty.Register<KeyFrameStripControl, IReadOnlyList<KeyFrameRetentionRange>?>(nameof(Ranges));
    public static readonly StyledProperty<IBrush?> ClipBrushProperty =
        AvaloniaProperty.Register<KeyFrameStripControl, IBrush?>(nameof(ClipBrush));

    public IReadOnlyList<KeyFrameStripItem>? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? BorderBrush { get => GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }
    public IReadOnlyList<KeyFrameRetentionRange>? Ranges { get => GetValue(RangesProperty); set => SetValue(RangesProperty, value); }
    public IBrush? ClipBrush { get => GetValue(ClipBrushProperty); set => SetValue(ClipBrushProperty, value); }
    public event EventHandler<KeyFrameStripItem>? SeekRequested;
    private static readonly IBrush TypeBadgeBrush = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)).ToImmutable();

    static KeyFrameStripControl() => AffectsRender<KeyFrameStripControl>(ItemsProperty,
        TextBrushProperty, AccentBrushProperty, BorderBrushProperty, RangesProperty, ClipBrushProperty);

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
        var tiles = GetTiles();
        foreach (var (item, bounds) in tiles)
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
            var retentionBrush = GetRetentionBrush(item, out var retained);
            if (retained && retentionBrush is not null)
            {
                using var opacity = context.PushOpacity(0.16);
                context.FillRectangle(retentionBrush,
                    new Rect(bounds.X - 2, imageArea.Bottom + 2, bounds.Width + 4,
                        Bounds.Height - imageArea.Bottom - 2));
            }
            using (context.PushOpacity(item.IsCurrent ? 1 : 0.28))
                context.DrawRectangle(null, new Pen(item.IsCurrent ? AccentBrush ?? Brushes.DodgerBlue
                    : BorderBrush ?? Brushes.Gray, item.IsCurrent ? 2 : 1), bounds);
            var text = new FormattedText(item.IsPlaceholder ? "…" : CommonUtil.FormatSeconds(item.Timestamp.TotalSeconds),
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Inter"), 11,
                TextBrush ?? Brushes.Gray);
            context.DrawText(text, new Point(bounds.Center.X - text.Width / 2, bounds.Bottom - 19));
        }
        foreach (var (bounds, isStart, brush) in GetBoundaryMarkers(tiles))
        {
            var pen = new Pen(brush, 2);
            var x = isStart ? bounds.Left : bounds.Right;
            context.DrawLine(pen, new Point(x, bounds.Top), new Point(x, bounds.Bottom));
            context.DrawLine(pen, bounds.TopLeft, bounds.TopRight);
            context.DrawLine(pen, bounds.BottomLeft, bounds.BottomRight);
        }
    }

    internal IReadOnlyList<(Rect Bounds, bool IsStart, IBrush Brush)> GetBoundaryMarkers(
        IReadOnlyList<(KeyFrameStripItem Item, Rect Bounds)> tiles)
    {
        var markers = new List<(Rect Bounds, bool IsStart, IBrush Brush)>();
        foreach (var range in (Ranges ?? []).OrderBy(range => range.IsActive))
        {
            if (range.End < range.Start)
                continue;
            var brush = string.IsNullOrEmpty(range.Color) ? ClipBrush ?? Brushes.Green : ClipLabels.Brush(range.Color);
            foreach (var (item, bounds) in tiles)
            {
                if (MatchesBoundary(item, range.StartPts, range.Start))
                    markers.Add((new Rect(bounds.Left + 4, bounds.Bottom - 18, 4, 15), true, brush));
                if (!MatchesBoundary(item, range.EndPts, range.End))
                    continue;
                var x = bounds.Right - 8;
                markers.Add((new Rect(x, bounds.Bottom - 18, 4, 15), false, brush));
            }
        }
        return markers;
    }

    private static bool MatchesBoundary(KeyFrameStripItem item, long? pts, double time) =>
        !item.IsPlaceholder && (pts is { } exact ? item.Pts == exact : item.Timestamp.TotalSeconds == time);

    internal IBrush? GetRetentionBrush(KeyFrameStripItem item) => GetRetentionBrush(item, out _);

    private IBrush? GetRetentionBrush(KeyFrameStripItem item, out bool retained)
    {
        retained = false;
        if (item.IsPlaceholder)
            return null;
        KeyFrameRetentionRange? matching = null;
        foreach (var range in Ranges ?? [])
        {
            if (range.Contains(item.Pts, item.Timestamp.TotalSeconds) &&
                (matching is null || range.IsActive || !matching.Value.IsActive))
                matching = range;
        }
        if (matching is not { } clip)
            return BorderBrush ?? Brushes.Gray;
        retained = true;
        return string.IsNullOrEmpty(clip.Color) ? ClipBrush ?? Brushes.Green : ClipLabels.Brush(clip.Color);
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
