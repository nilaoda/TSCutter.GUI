using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using TSCutter.GUI.ViewModels.Preview;

namespace TSCutter.GUI.Controls;

/// <summary>
/// 时间轴上方的节目带：按录制中的 EPG 节目或文件章节分段，与时间轴主轨道使用同一套横向坐标（含放大后的可见范围）。
/// 播放头所在的段突出显示；悬停显示详情，单击跳到段的开始，右键菜单作用于所点的段。
/// </summary>
public sealed class TimelineSegmentBandControl : Control
{
    // 与时间轴主轨道的左右留白一致，分段边界与时间刻度对齐。
    private const double HorizontalPadding = 6;
    private const double TextPadding = 4;

    public static readonly StyledProperty<IReadOnlyList<TimelineSegmentItem>?> SegmentsProperty =
        AvaloniaProperty.Register<TimelineSegmentBandControl, IReadOnlyList<TimelineSegmentItem>?>(nameof(Segments));
    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<TimelineSegmentBandControl, double>(nameof(Minimum));
    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<TimelineSegmentBandControl, double>(nameof(Maximum));
    public static readonly StyledProperty<double> ViewportStartProperty =
        AvaloniaProperty.Register<TimelineSegmentBandControl, double>(nameof(ViewportStart));
    public static readonly StyledProperty<double> ViewportEndProperty =
        AvaloniaProperty.Register<TimelineSegmentBandControl, double>(nameof(ViewportEnd));
    public static readonly StyledProperty<double> CurrentTimeProperty =
        AvaloniaProperty.Register<TimelineSegmentBandControl, double>(nameof(CurrentTime));
    public static readonly StyledProperty<IBrush?> CurrentBrushProperty =
        AvaloniaProperty.Register<TimelineSegmentBandControl, IBrush?>(nameof(CurrentBrush));
    public static readonly StyledProperty<IBrush?> SegmentBrushProperty =
        AvaloniaProperty.Register<TimelineSegmentBandControl, IBrush?>(nameof(SegmentBrush));
    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<TimelineSegmentBandControl, IBrush?>(nameof(TextBrush));
    public static readonly StyledProperty<IBrush?> MutedTextBrushProperty =
        AvaloniaProperty.Register<TimelineSegmentBandControl, IBrush?>(nameof(MutedTextBrush));
    public static readonly StyledProperty<IBrush?> SeparatorBrushProperty =
        AvaloniaProperty.Register<TimelineSegmentBandControl, IBrush?>(nameof(SeparatorBrush));
    /// <summary>右键所点的段，供右键菜单的命令参数使用。</summary>
    public static readonly StyledProperty<TimelineSegmentItem?> ContextSegmentProperty =
        AvaloniaProperty.Register<TimelineSegmentBandControl, TimelineSegmentItem?>(nameof(ContextSegment));

    private readonly ToolTip _tooltip = new();
    private readonly TextBlock _tooltipText = new() { MaxWidth = 320, TextWrapping = TextWrapping.Wrap };
    // 播放头所在段的序号；播放时间每帧变化，只有所在段变化时才重绘。
    private int _currentIndex = -1;
    // 指针所在的段，只在换段时更新提示。
    private TimelineSegmentItem? _hoverSegment;

    static TimelineSegmentBandControl()
    {
        AffectsRender<TimelineSegmentBandControl>(SegmentsProperty, MinimumProperty, MaximumProperty, ViewportStartProperty,
            ViewportEndProperty, CurrentBrushProperty, SegmentBrushProperty, TextBrushProperty,
            MutedTextBrushProperty, SeparatorBrushProperty);
    }

    public TimelineSegmentBandControl()
    {
        ClipToBounds = true;
        _tooltip.Content = _tooltipText;
        _tooltip.IsVisible = false;
        ToolTip.SetTip(this, _tooltip);
        PointerExited += (_, _) =>
        {
            _hoverSegment = null;
            _tooltip.IsVisible = false;
        };
        // 隧道阶段先于右键菜单处理：确定所点的段，空白处不弹出菜单。
        AddHandler(ContextRequestedEvent, OnContextRequested, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    public IReadOnlyList<TimelineSegmentItem>? Segments { get => GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double ViewportStart { get => GetValue(ViewportStartProperty); set => SetValue(ViewportStartProperty, value); }
    public double ViewportEnd { get => GetValue(ViewportEndProperty); set => SetValue(ViewportEndProperty, value); }
    public double CurrentTime { get => GetValue(CurrentTimeProperty); set => SetValue(CurrentTimeProperty, value); }
    public IBrush? CurrentBrush { get => GetValue(CurrentBrushProperty); set => SetValue(CurrentBrushProperty, value); }
    public IBrush? SegmentBrush { get => GetValue(SegmentBrushProperty); set => SetValue(SegmentBrushProperty, value); }
    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public IBrush? MutedTextBrush { get => GetValue(MutedTextBrushProperty); set => SetValue(MutedTextBrushProperty, value); }
    public IBrush? SeparatorBrush { get => GetValue(SeparatorBrushProperty); set => SetValue(SeparatorBrushProperty, value); }
    public TimelineSegmentItem? ContextSegment { get => GetValue(ContextSegmentProperty); set => SetValue(ContextSegmentProperty, value); }

    /// <summary>单击某一段时请求跳到它的开始。</summary>
    public event EventHandler<TimelineSegmentItem>? SeekRequested;

    // 可见范围与时间轴相同：放大时取视口，否则取整个时间轴。
    private double VisibleStart => ViewportEnd > ViewportStart ? ViewportStart : Minimum;
    private double VisibleEnd => ViewportEnd > ViewportStart ? ViewportEnd : Maximum;

    private Rect TrackBounds => new(HorizontalPadding, 0, Math.Max(0, Bounds.Width - HorizontalPadding * 2), Bounds.Height);

    private double TimeToX(double time, Rect track)
    {
        var duration = Math.Max(VisibleEnd - VisibleStart, 0.000001);
        return track.Left + Math.Clamp((time - VisibleStart) / duration, 0, 1) * track.Width;
    }

    internal TimelineSegmentItem? HitTest(Point point)
    {
        var track = TrackBounds;
        if (Segments is not { Count: > 0 } segments || track.Width <= 0 || !track.Contains(point))
            return null;
        var time = VisibleStart + (point.X - track.Left) / track.Width * (VisibleEnd - VisibleStart);
        foreach (var item in segments)
            if (time >= item.StartSeconds && time < item.EndSeconds)
                return item;
        // 终点本身算最后一段。
        return segments[^1].EndSeconds <= time + 0.000001 ? segments[^1] : null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != CurrentTimeProperty && change.Property != SegmentsProperty)
            return;
        var index = -1;
        if (Segments is { Count: > 0 } segments)
        {
            for (var i = 0; i < segments.Count && index < 0; i++)
                if (CurrentTime >= segments[i].StartSeconds && CurrentTime < segments[i].EndSeconds)
                    index = i;
            // 终点本身算最后一段（到结尾的段）。
            if (index < 0 && segments[^1].EndsAtEnd && CurrentTime >= segments[^1].EndSeconds)
                index = segments.Count - 1;
        }
        if (index == _currentIndex)
            return;
        _currentIndex = index;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var item = HitTest(e.GetPosition(this));
        if (ReferenceEquals(item, _hoverSegment))
            return;
        _hoverSegment = item;
        _tooltipText.Text = item?.Tooltip ?? string.Empty;
        _tooltip.IsVisible = item is not null;
        // 提示已显示时换到另一段：关闭后在指针处重新打开，提示跟到新的段旁边；移到空白处则关闭。
        if (!ToolTip.GetIsOpen(this))
            return;
        ToolTip.SetIsOpen(this, false);
        if (item is not null)
            ToolTip.SetIsOpen(this, true);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var item = HitTest(e.GetPosition(this));
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsRightButtonPressed)
        {
            ContextSegment = item;
            return;
        }
        if (properties.IsLeftButtonPressed && item is not null)
        {
            SeekRequested?.Invoke(this, item);
            e.Handled = true;
        }
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.TryGetPosition(this, out var point))
            ContextSegment = HitTest(point);
        if (ContextSegment is null)
            e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        var track = TrackBounds;
        if (Segments is not { Count: > 0 } segments || track.Width <= 0 || track.Height <= 0)
            return;
        var separator = SeparatorBrush is { } separatorBrush ? new Pen(separatorBrush, 1) : null;
        for (var index = 0; index < segments.Count; index++)
        {
            var item = segments[index];
            if (item.EndSeconds <= VisibleStart || item.StartSeconds >= VisibleEnd)
                continue;
            var left = TimeToX(item.StartSeconds, track);
            var right = TimeToX(item.EndSeconds, track);
            var rect = new Rect(left, track.Top, Math.Max(1, right - left), track.Height);
            var current = index == _currentIndex;
            if (SegmentBrush is { } segmentBrush)
                context.FillRectangle(segmentBrush, rect);
            // 当前段用强调色的浅色叠加，文字在浅色、深色主题下都清晰。
            if (current && CurrentBrush is { } currentBrush)
                using (context.PushOpacity(0.35))
                    context.FillRectangle(currentBrush, rect);
            if (separator is not null && item.StartSeconds > Minimum && left > track.Left)
                context.DrawLine(separator, new Point(left + 0.5, track.Top), new Point(left + 0.5, track.Bottom));
            if (rect.Width < 24)
                continue;
            var text = new FormattedText(item.Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                Typeface.Default, 11, current ? TextBrush : MutedTextBrush ?? TextBrush)
            {
                MaxTextWidth = Math.Max(1, rect.Width - TextPadding * 2),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis
            };
            using (context.PushClip(rect))
                context.DrawText(text, new Point(rect.Left + TextPadding, rect.Top + (rect.Height - text.Height) / 2));
        }
    }
}
