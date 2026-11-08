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
    /// <summary>删除模式下要删除的画面所用的颜色。</summary>
    public static readonly StyledProperty<IBrush?> RemovedBrushProperty =
        AvaloniaProperty.Register<KeyFrameStripControl, IBrush?>(nameof(RemovedBrush));

    public IReadOnlyList<KeyFrameStripItem>? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? BorderBrush { get => GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }
    public IReadOnlyList<KeyFrameRetentionRange>? Ranges { get => GetValue(RangesProperty); set => SetValue(RangesProperty, value); }
    public IBrush? ClipBrush { get => GetValue(ClipBrushProperty); set => SetValue(ClipBrushProperty, value); }
    public IBrush? RemovedBrush { get => GetValue(RemovedBrushProperty); set => SetValue(RemovedBrushProperty, value); }
    public event EventHandler<KeyFrameStripItem>? SeekRequested;
    /// <summary>滚轮滚动的格数：正数前进（向下滚），负数后退（向上滚）。</summary>
    public event EventHandler<int>? WheelStepRequested;
    /// <summary>间隔不足该值的滚轮事件忽略（触控板、平滑滚动等连续事件流），与图片预览的滚轮缩放相同。</summary>
    internal const ulong WheelIntervalMs = 16;
    private ulong? lastWheelStep;
    private static readonly IBrush TypeBadgeBrush = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)).ToImmutable();

    static KeyFrameStripControl() => AffectsRender<KeyFrameStripControl>(ItemsProperty,
        TextBrushProperty, AccentBrushProperty, BorderBrushProperty, RangesProperty, ClipBrushProperty, RemovedBrushProperty);

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
            var brush = RangeBrush(range);
            foreach (var (item, bounds) in tiles)
            {
                if (MatchesBoundary(item, range.StartPts, range.Start))
                    markers.Add((new Rect(bounds.Left + 4, bounds.Bottom - 18, 4, 15), true, brush));
                if (!MatchesBoundary(item, range.EndPts, range.End))
                    continue;
                // 删除范围的终点是恢复播放的画面，本身保留：收口画在它左侧与前一帧之间 4px 的缝隙里。
                var x = range.Removed ? bounds.Left - 4 : bounds.Right - 8;
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
        return RangeBrush(clip);
    }

    // 删除范围统一用删除颜色，不随片段标注颜色变化，避免与保留片段混淆。
    private IBrush RangeBrush(KeyFrameRetentionRange range) => range.Removed ? RemovedBrush ?? Brushes.Red
        : string.IsNullOrEmpty(range.Color) ? ClipBrush ?? Brushes.Green : ClipLabels.Brush(range.Color);

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

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        e.Handled |= ApplyWheel(e.Delta.Y, e.Timestamp);
    }

    /// <summary>
    /// 处理一次滚轮滚动：与图片预览的滚轮缩放相同，每个事件按方向移动一格，不按增量大小换算——
    /// macOS 普通滚轮每格的增量随滚动加速变化且常小于 1（Avalonia 换算为 scrollingDeltaY / 5），
    /// 按增量累计会出现有时滚两格才移动一次。距上一次移动不足 <see cref="WheelIntervalMs"/> 毫秒的事件忽略。
    /// 解码期间主窗口的遮罩拦截输入，落在导航条上的滚轮由窗口转交到这里。返回是否处理了这次滚动。
    /// </summary>
    internal bool ApplyWheel(double deltaY, ulong timestamp)
    {
        if (!IsEffectivelyEnabled || deltaY == 0)
            return false;
        if (lastWheelStep is { } last && timestamp >= last && timestamp - last < WheelIntervalMs)
            return true;
        lastWheelStep = timestamp;
        // 向上滚 Delta.Y 为正（后退），向下滚为负（前进）。
        WheelStepRequested?.Invoke(this, deltaY < 0 ? 1 : -1);
        return true;
    }
}
