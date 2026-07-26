using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using TSCutter.GUI.Models;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.Controls;

public sealed class TimelineZoomRequestEventArgs(double zoomLevel, double anchorTime) : EventArgs
{
    public double ZoomLevel { get; } = zoomLevel;
    public double AnchorTime { get; } = anchorTime;
}

/// <summary>
/// 主界面的自绘时间轴。刻度、剪辑区间、播放头、完整文件概览和缩放操作
/// 使用同一套坐标换算，范围变化时不依赖子控件重新布局。
/// </summary>
public sealed class TimelineControl : Control
{
    private const double MainHeight = 31;
    private const double OverviewTop = 35;
    private const double OverviewHeight = 10;
    private const double HorizontalPadding = 6;
    private const double ControlAreaWidth = 218;
    private const double FitButtonWidth = 28;
    private const double ZoomTrackWidth = 96;
    private const double ZoomButtonWidth = 16;
    private const double ZoomStep = 0.08;

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<TimelineControl, double>(nameof(Minimum));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<TimelineControl, double>(nameof(Maximum));

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<TimelineControl, double>(nameof(Value));

    public static readonly StyledProperty<double> ViewportStartProperty =
        AvaloniaProperty.Register<TimelineControl, double>(nameof(ViewportStart));

    public static readonly StyledProperty<double> ViewportEndProperty =
        AvaloniaProperty.Register<TimelineControl, double>(nameof(ViewportEnd));

    public static readonly StyledProperty<double> ZoomLevelProperty =
        AvaloniaProperty.Register<TimelineControl, double>(nameof(ZoomLevel));

    public static readonly StyledProperty<IReadOnlyList<ClipTimelineRange>?> RangesProperty =
        AvaloniaProperty.Register<TimelineControl, IReadOnlyList<ClipTimelineRange>?>(nameof(Ranges));

    public static readonly StyledProperty<IBrush?> BackgroundBrushProperty =
        AvaloniaProperty.Register<TimelineControl, IBrush?>(nameof(BackgroundBrush));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<TimelineControl, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> EmptyTrackBrushProperty =
        AvaloniaProperty.Register<TimelineControl, IBrush?>(nameof(EmptyTrackBrush));

    public static readonly StyledProperty<IBrush?> BorderDarkBrushProperty =
        AvaloniaProperty.Register<TimelineControl, IBrush?>(nameof(BorderDarkBrush));

    public static readonly StyledProperty<IBrush?> BorderLightBrushProperty =
        AvaloniaProperty.Register<TimelineControl, IBrush?>(nameof(BorderLightBrush));

    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<TimelineControl, IBrush?>(nameof(TextBrush));

    public static readonly StyledProperty<IBrush?> MutedTextBrushProperty =
        AvaloniaProperty.Register<TimelineControl, IBrush?>(nameof(MutedTextBrush));

    public static readonly StyledProperty<IBrush?> ClipBrushProperty =
        AvaloniaProperty.Register<TimelineControl, IBrush?>(nameof(ClipBrush));

    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<TimelineControl, IBrush?>(nameof(AccentBrush));

    public static readonly StyledProperty<IBrush?> ViewportBrushProperty =
        AvaloniaProperty.Register<TimelineControl, IBrush?>(nameof(ViewportBrush));

    public static readonly StyledProperty<string> FitTipProperty =
        AvaloniaProperty.Register<TimelineControl, string>(nameof(FitTip), string.Empty);

    public static readonly StyledProperty<string> ZoomTipProperty =
        AvaloniaProperty.Register<TimelineControl, string>(nameof(ZoomTip), string.Empty);

    public static readonly StyledProperty<string> ScrollTipProperty =
        AvaloniaProperty.Register<TimelineControl, string>(nameof(ScrollTip), string.Empty);

    private enum HitPart
    {
        None,
        Main,
        Overview,
        Viewport,
        Fit,
        ZoomOut,
        Zoom,
        ZoomIn
    }

    private enum DragMode
    {
        None,
        Seek,
        Pan,
        Zoom
    }

    private readonly ToolTip _tooltip = new() { IsVisible = false };
    private HitPart _hoveredPart;
    private HitPart _pressedPart;
    private DragMode _dragMode;
    private double _dragStartX;
    private double _dragStartViewport;
    private double _dragValue;
    private bool _hasDragValue;
    private double _pendingSeekValue;
    private bool _hasPendingSeekValue;

    static TimelineControl()
    {
        AffectsRender<TimelineControl>(
            MinimumProperty, MaximumProperty, ValueProperty,
            ViewportStartProperty, ViewportEndProperty, ZoomLevelProperty, RangesProperty,
            BackgroundBrushProperty, TrackBrushProperty, EmptyTrackBrushProperty, BorderDarkBrushProperty,
            BorderLightBrushProperty, TextBrushProperty, MutedTextBrushProperty,
            ClipBrushProperty, AccentBrushProperty, ViewportBrushProperty);
    }

    public TimelineControl()
    {
        ClipToBounds = true;
        Focusable = true;
        ToolTip.SetTip(this, _tooltip);
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Tunnel);
        PointerExited += OnPointerExited;
        PointerCaptureLost += OnPointerCaptureLost;
    }

    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double ViewportStart { get => GetValue(ViewportStartProperty); set => SetValue(ViewportStartProperty, value); }
    public double ViewportEnd { get => GetValue(ViewportEndProperty); set => SetValue(ViewportEndProperty, value); }
    public double ZoomLevel { get => GetValue(ZoomLevelProperty); set => SetValue(ZoomLevelProperty, value); }
    public IReadOnlyList<ClipTimelineRange>? Ranges { get => GetValue(RangesProperty); set => SetValue(RangesProperty, value); }
    public IBrush? BackgroundBrush { get => GetValue(BackgroundBrushProperty); set => SetValue(BackgroundBrushProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? EmptyTrackBrush { get => GetValue(EmptyTrackBrushProperty); set => SetValue(EmptyTrackBrushProperty, value); }
    public IBrush? BorderDarkBrush { get => GetValue(BorderDarkBrushProperty); set => SetValue(BorderDarkBrushProperty, value); }
    public IBrush? BorderLightBrush { get => GetValue(BorderLightBrushProperty); set => SetValue(BorderLightBrushProperty, value); }
    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public IBrush? MutedTextBrush { get => GetValue(MutedTextBrushProperty); set => SetValue(MutedTextBrushProperty, value); }
    public IBrush? ClipBrush { get => GetValue(ClipBrushProperty); set => SetValue(ClipBrushProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? ViewportBrush { get => GetValue(ViewportBrushProperty); set => SetValue(ViewportBrushProperty, value); }
    public string FitTip { get => GetValue(FitTipProperty); set => SetValue(FitTipProperty, value); }
    public string ZoomTip { get => GetValue(ZoomTipProperty); set => SetValue(ZoomTipProperty, value); }
    public string ScrollTip { get => GetValue(ScrollTipProperty); set => SetValue(ScrollTipProperty, value); }

    public event EventHandler<double>? SeekRequested;
    public event EventHandler<double>? PanRequested;
    public event EventHandler<TimelineZoomRequestEventArgs>? ZoomRequested;
    public event EventHandler? FitRequested;

    public void CompletePendingSeek()
    {
        if (!_hasPendingSeekValue)
            return;
        _hasPendingSeekValue = false;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsFinite(availableSize.Width) ? availableSize.Width : 640,
        48);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        var background = BackgroundBrush ?? Brushes.Transparent;
        var track = Maximum > Minimum
            ? TrackBrush ?? Brushes.Black
            : EmptyTrackBrush ?? TrackBrush ?? Brushes.Black;
        var dark = BorderDarkBrush ?? Brushes.DimGray;
        var light = BorderLightBrush ?? Brushes.Gray;
        var text = TextBrush ?? Brushes.White;
        var muted = MutedTextBrush ?? Brushes.Gray;
        var clip = ClipBrush ?? Brushes.Green;
        var accent = AccentBrush ?? Brushes.DodgerBlue;
        var viewport = ViewportBrush ?? accent;

        context.FillRectangle(background, new Rect(Bounds.Size));
        var mainBounds = GetMainBounds();
        DrawSunkenRect(context, mainBounds, track, dark, light);
        DrawRuler(context, mainBounds, muted);
        DrawVisibleClips(context, mainBounds, clip);
        DrawPlayhead(context, mainBounds, accent);
        DrawOverview(context, track, dark, light, clip, viewport);
        DrawControls(context, background, track, dark, light, text, muted, accent);
    }

    private void DrawRuler(DrawingContext context, Rect bounds, IBrush brush)
    {
        var duration = VisibleDuration;
        if (duration <= 0 || bounds.Width <= 0)
            return;

        var majorInterval = NiceInterval(duration / Math.Max(1, bounds.Width / 96));
        var minorInterval = majorInterval / 5;
        var firstMinor = Math.Ceiling(VisibleStart / minorInterval) * minorInterval;
        var minorPen = new Pen(brush, 1);
        var startLabel = CreateText(FormatRulerTime(VisibleStart), 10, brush);
        var endLabel = CreateText(FormatRulerTime(VisibleEnd), 10, brush);
        var startLabelX = bounds.Left + 3;
        var endLabelX = bounds.Right - endLabel.Width - 3;
        var labelY = bounds.Top + 2;
        for (var time = firstMinor; time <= VisibleEnd + minorInterval * 0.1; time += minorInterval)
        {
            var x = TimeToMainX(time, bounds);
            var majorIndex = Math.Round(time / majorInterval);
            var isMajor = Math.Abs(time - majorIndex * majorInterval) < minorInterval * 0.05;
            using (context.PushOpacity(isMajor ? 0.72 : 0.32))
                context.DrawLine(minorPen,
                    new Point(x, bounds.Top + (isMajor ? 15 : 20)),
                    new Point(x, bounds.Bottom - 7));
            if (!isMajor)
                continue;

            var label = CreateText(FormatRulerTime(time), 10, brush);
            var labelX = Math.Clamp(x - label.Width / 2, bounds.Left + 3, bounds.Right - label.Width - 3);
            if (labelX > startLabelX + startLabel.Width + 6
                && labelX + label.Width < endLabelX - 6)
                context.DrawText(label, new Point(labelX, labelY));
        }
        context.DrawText(startLabel, new Point(startLabelX, labelY));
        if (endLabelX > startLabelX + startLabel.Width + 8)
            context.DrawText(endLabel, new Point(endLabelX, labelY));
    }

    private void DrawVisibleClips(DrawingContext context, Rect bounds, IBrush brush)
    {
        if (Ranges is not { Count: > 0 } || VisibleDuration <= 0)
            return;

        foreach (var range in Ranges.OrderBy(item => item.IsActive))
        {
            var start = Math.Max(range.Start, VisibleStart);
            var end = Math.Min(range.End, VisibleEnd);
            if (end <= start)
                continue;
            var left = TimeToMainX(start, bounds);
            var right = TimeToMainX(end, bounds);
            using var opacity = context.PushOpacity(range.IsActive ? 0.95 : 0.52);
            context.FillRectangle(brush, new Rect(left, bounds.Bottom - 7, Math.Max(1, right - left), 5));
        }
    }

    private void DrawPlayhead(DrawingContext context, Rect bounds, IBrush brush)
    {
        var value = _hasDragValue
            ? _dragValue
            : _hasPendingSeekValue
                ? _pendingSeekValue
                : Value;
        if (value < VisibleStart || value > VisibleEnd || VisibleDuration <= 0)
            return;
        var x = TimeToMainX(value, bounds);
        context.DrawLine(new Pen(brush, 1.5),
            new Point(x, bounds.Top + 14), new Point(x, bounds.Bottom - 1));
        context.FillRectangle(brush, new Rect(x - 3.5, bounds.Top + 12, 7, 5));
    }

    private void DrawOverview(
        DrawingContext context,
        IBrush track,
        IBrush dark,
        IBrush light,
        IBrush clip,
        IBrush viewport)
    {
        var bounds = GetOverviewBounds();
        if (bounds.Width <= 0)
            return;
        DrawSunkenRect(context, bounds, track, dark, light);
        if (Maximum <= Minimum)
            return;

        if (IsZoomed && Ranges is { Count: > 0 })
        {
            foreach (var range in Ranges)
            {
                var start = Math.Clamp(range.Start, Minimum, Maximum);
                var end = Math.Clamp(range.End, Minimum, Maximum);
                if (end <= start)
                    continue;
                var left = FullTimeToOverviewX(start, bounds);
                var right = FullTimeToOverviewX(end, bounds);
                using var opacity = context.PushOpacity(range.IsActive ? 0.8 : 0.42);
                context.FillRectangle(clip,
                    new Rect(left, bounds.Bottom - 4, Math.Max(1, right - left), 2));
            }
        }

        if (IsZoomed)
        {
            var viewportBounds = GetViewportIndicatorBounds();
            using (context.PushOpacity(0.18))
                context.FillRectangle(viewport, viewportBounds);
            context.DrawRectangle(new Pen(viewport, 1.3), viewportBounds);
            if (viewportBounds.Width >= 22)
            {
                using var opacity = context.PushOpacity(0.65);
                context.DrawLine(new Pen(viewport, 1),
                    new Point(viewportBounds.Left + 3, viewportBounds.Top + 2),
                    new Point(viewportBounds.Left + 3, viewportBounds.Bottom - 2));
                context.DrawLine(new Pen(viewport, 1),
                    new Point(viewportBounds.Right - 3, viewportBounds.Top + 2),
                    new Point(viewportBounds.Right - 3, viewportBounds.Bottom - 2));
            }
        }
    }

    private void DrawControls(
        DrawingContext context,
        IBrush background,
        IBrush track,
        IBrush dark,
        IBrush light,
        IBrush text,
        IBrush muted,
        IBrush accent)
    {
        var canInteract = Maximum > Minimum;
        var fitBounds = GetFitBounds();
        DrawRaisedRect(context, fitBounds, background, dark, light, _pressedPart == HitPart.Fit);
        DrawFitGlyph(context, fitBounds, IsZoomed ? text : muted);

        var zoomBounds = GetZoomBounds();
        var centerY = zoomBounds.Center.Y;
        var zoomOutBounds = GetZoomOutBounds();
        var zoomInBounds = GetZoomInBounds();
        DrawZoomGlyph(context, zoomOutBounds, false,
            canInteract && (_hoveredPart == HitPart.ZoomOut || _pressedPart == HitPart.ZoomOut),
            canInteract ? text : muted, accent);
        DrawZoomGlyph(context, zoomInBounds, true,
            canInteract && (_hoveredPart == HitPart.ZoomIn || _pressedPart == HitPart.ZoomIn),
            canInteract ? text : muted, accent);
        DrawSunkenRect(context, zoomBounds, track, dark, light);
        var thumbX = zoomBounds.Left + Math.Clamp(ZoomLevel, 0, 1) * zoomBounds.Width;
        var thumb = new Rect(thumbX - 2.5, zoomBounds.Top - 2, 5, zoomBounds.Height + 4);
        DrawRaisedRect(context, thumb, background, dark, light, _pressedPart == HitPart.Zoom);

        var factor = VisibleDuration > 0 ? FullDuration / VisibleDuration : 1;
        DrawScaleFactor(context, factor, centerY, IsZoomed ? accent : muted);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Maximum <= Minimum || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        Focus();
        var point = e.GetPosition(this);
        var part = HitTest(point);
        _pressedPart = part;
        switch (part)
        {
            case HitPart.Main:
                _dragMode = DragMode.Seek;
                _dragValue = MainXToTime(point.X);
                _hasDragValue = true;
                break;
            case HitPart.Viewport:
                _dragMode = DragMode.Pan;
                _dragStartX = point.X;
                _dragStartViewport = ViewportStart;
                break;
            case HitPart.Overview:
                _dragMode = DragMode.Pan;
                _dragStartX = point.X;
                _dragStartViewport = ClampViewportStart(
                    OverviewXToFullTime(point.X) - VisibleDuration / 2);
                PanRequested?.Invoke(this, _dragStartViewport);
                break;
            case HitPart.Zoom:
                _dragMode = DragMode.Zoom;
                RaiseZoomFromX(point.X);
                break;
            case HitPart.Fit:
                if (e.ClickCount >= 1 && IsZoomed)
                    FitRequested?.Invoke(this, EventArgs.Empty);
                break;
            case HitPart.ZoomOut:
                RaiseZoomLevel(ZoomLevel - ZoomStep);
                break;
            case HitPart.ZoomIn:
                RaiseZoomLevel(ZoomLevel + ZoomStep);
                break;
        }

        if (_dragMode != DragMode.None)
            e.Pointer.Capture(this);
        InvalidateVisual();
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var point = e.GetPosition(this);
        if (_dragMode == DragMode.Seek)
        {
            _dragValue = MainXToTime(point.X);
            _hasDragValue = true;
            InvalidateVisual();
        }
        else if (_dragMode == DragMode.Pan)
        {
            var overview = GetOverviewBounds();
            var delta = overview.Width > 0
                ? (point.X - _dragStartX) / overview.Width * FullDuration
                : 0;
            PanRequested?.Invoke(this, ClampViewportStart(_dragStartViewport + delta));
        }
        else if (_dragMode == DragMode.Zoom)
        {
            RaiseZoomFromX(point.X);
        }
        else
        {
            _hoveredPart = HitTest(point);
            UpdateTooltip(point, _hoveredPart);
            Cursor = _hoveredPart switch
            {
                HitPart.Viewport or HitPart.Overview => new Cursor(StandardCursorType.SizeWestEast),
                HitPart.Fit or HitPart.ZoomOut or HitPart.ZoomIn => new Cursor(StandardCursorType.Hand),
                HitPart.Zoom => new Cursor(StandardCursorType.SizeWestEast),
                _ => new Cursor(StandardCursorType.Arrow)
            };
            InvalidateVisual();
        }
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragMode == DragMode.Seek && _hasDragValue)
        {
            // 解码完成前保留用户点击的位置，避免播放头短暂返回旧位置。
            _pendingSeekValue = _dragValue;
            _hasPendingSeekValue = true;
            SeekRequested?.Invoke(this, _dragValue);
        }
        ResetDrag(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (Maximum <= Minimum)
            return;
        var modifiers = e.KeyModifiers;
        if (modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta))
        {
            var point = e.GetPosition(this);
            var anchor = point.Y < MainHeight ? MainXToTime(point.X) : GetDefaultZoomAnchor();
            var level = Math.Clamp(ZoomLevel + e.Delta.Y * 0.055, 0, 1);
            ZoomRequested?.Invoke(this, new TimelineZoomRequestEventArgs(level, anchor));
            e.Handled = true;
        }
        else if (modifiers.HasFlag(KeyModifiers.Shift) && IsZoomed)
        {
            var delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
            PanRequested?.Invoke(this,
                ClampViewportStart(ViewportStart - delta * VisibleDuration * 0.1));
            e.Handled = true;
        }
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (_dragMode != DragMode.None)
            return;
        _hoveredPart = HitPart.None;
        _pressedPart = HitPart.None;
        _tooltip.IsVisible = false;
        InvalidateVisual();
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) =>
        ResetDrag(null);

    private void ResetDrag(IPointer? pointer)
    {
        pointer?.Capture(null);
        _dragMode = DragMode.None;
        _pressedPart = HitPart.None;
        _hasDragValue = false;
        InvalidateVisual();
    }

    private void RaiseZoomFromX(double x)
    {
        var bounds = GetZoomBounds();
        var level = bounds.Width > 0 ? Math.Clamp((x - bounds.Left) / bounds.Width, 0, 1) : 0;
        RaiseZoomLevel(level);
    }

    private void RaiseZoomLevel(double level)
    {
        ZoomRequested?.Invoke(this,
            new TimelineZoomRequestEventArgs(Math.Clamp(level, 0, 1), GetDefaultZoomAnchor()));
    }

    private void UpdateTooltip(Point point, HitPart part)
    {
        _tooltip.Content = part switch
        {
            HitPart.Main => CommonUtil.FormatSeconds(MainXToTime(point.X)),
            HitPart.Viewport or HitPart.Overview => ScrollTip,
            HitPart.Fit => FitTip,
            HitPart.ZoomOut or HitPart.Zoom or HitPart.ZoomIn => ZoomTip,
            _ => string.Empty
        };
        _tooltip.IsVisible = part != HitPart.None;
    }

    private HitPart HitTest(Point point)
    {
        if (Maximum <= Minimum)
            return HitPart.None;
        if (IsZoomed && GetFitBounds().Contains(point))
            return HitPart.Fit;
        if (GetZoomOutBounds().Contains(point))
            return HitPart.ZoomOut;
        if (GetZoomInBounds().Contains(point))
            return HitPart.ZoomIn;
        if (GetZoomHitBounds().Contains(point))
            return HitPart.Zoom;
        if (GetViewportIndicatorBounds().Contains(point) && IsZoomed)
            return HitPart.Viewport;
        if (IsZoomed && GetOverviewBounds().Contains(point))
            return HitPart.Overview;
        if (GetMainBounds().Contains(point))
            return HitPart.Main;
        return HitPart.None;
    }

    private Rect GetMainBounds() => new(
        HorizontalPadding, 1,
        Math.Max(0, Bounds.Width - HorizontalPadding * 2), MainHeight - 2);

    private Rect GetOverviewBounds() => new(
        HorizontalPadding, OverviewTop,
        Math.Max(0, Bounds.Width - ControlAreaWidth - HorizontalPadding * 2),
        OverviewHeight);

    private Rect GetFitBounds() => new(
        Math.Max(HorizontalPadding, Bounds.Width - ControlAreaWidth + 4),
        OverviewTop - 2, FitButtonWidth, OverviewHeight + 4);

    private Rect GetZoomOutBounds() => new(
        Math.Max(HorizontalPadding, Bounds.Width - ControlAreaWidth + 40),
        OverviewTop - 2, ZoomButtonWidth, OverviewHeight + 4);

    private Rect GetZoomBounds() => new(
        Math.Max(HorizontalPadding, Bounds.Width - ControlAreaWidth + 60),
        OverviewTop + 2, ZoomTrackWidth, 6);

    private Rect GetZoomInBounds() => new(
        Math.Max(HorizontalPadding, Bounds.Width - ControlAreaWidth + 160),
        OverviewTop - 2, ZoomButtonWidth, OverviewHeight + 4);

    private Rect GetZoomHitBounds()
    {
        var bounds = GetZoomBounds();
        return new Rect(
            bounds.X - 7,
            bounds.Y - 5,
            bounds.Width + 14,
            bounds.Height + 10);
    }

    private Rect GetViewportIndicatorBounds()
    {
        var overview = GetOverviewBounds();
        if (FullDuration <= 0 || overview.Width <= 0)
            return overview;
        var left = FullTimeToOverviewX(ViewportStart, overview);
        var right = FullTimeToOverviewX(ViewportEnd, overview);
        var width = Math.Clamp(right - left, 6, overview.Width);
        left = Math.Clamp(left, overview.Left, overview.Right - width);
        return new Rect(left, overview.Top + 1, width, overview.Height - 2);
    }

    private double MainXToTime(double x)
    {
        var bounds = GetMainBounds();
        var ratio = bounds.Width > 0 ? Math.Clamp((x - bounds.Left) / bounds.Width, 0, 1) : 0;
        return VisibleStart + ratio * VisibleDuration;
    }

    private double TimeToMainX(double time, Rect bounds) =>
        bounds.Left + Math.Clamp((time - VisibleStart) / Math.Max(VisibleDuration, 0.000001), 0, 1) * bounds.Width;

    private double OverviewXToFullTime(double x)
    {
        var bounds = GetOverviewBounds();
        var ratio = bounds.Width > 0 ? Math.Clamp((x - bounds.Left) / bounds.Width, 0, 1) : 0;
        return Minimum + ratio * FullDuration;
    }

    private double FullTimeToOverviewX(double time, Rect bounds) =>
        bounds.Left + Math.Clamp((time - Minimum) / Math.Max(FullDuration, 0.000001), 0, 1) * bounds.Width;

    private double ClampViewportStart(double value) =>
        Math.Clamp(value, Minimum, Math.Max(Minimum, Maximum - VisibleDuration));

    private double GetDefaultZoomAnchor() =>
        Value >= VisibleStart && Value <= VisibleEnd
            ? Value
            : VisibleStart + VisibleDuration / 2;

    private double VisibleStart => ViewportEnd > ViewportStart ? ViewportStart : Minimum;
    private double VisibleEnd => ViewportEnd > ViewportStart ? ViewportEnd : Maximum;
    private double VisibleDuration => Math.Max(0, VisibleEnd - VisibleStart);
    private double FullDuration => Math.Max(0, Maximum - Minimum);
    private bool IsZoomed => ZoomLevel > 0.0001 && VisibleDuration < FullDuration - 0.0001;

    private static void DrawSunkenRect(
        DrawingContext context, Rect rect, IBrush fill, IBrush dark, IBrush light)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;
        context.FillRectangle(fill, rect);
        context.DrawLine(new Pen(dark, 1), rect.TopLeft, rect.TopRight);
        context.DrawLine(new Pen(dark, 1), rect.TopLeft, rect.BottomLeft);
        context.DrawLine(new Pen(light, 1), rect.BottomLeft, rect.BottomRight);
        context.DrawLine(new Pen(light, 1), rect.TopRight, rect.BottomRight);
    }

    private static void DrawRaisedRect(
        DrawingContext context, Rect rect, IBrush fill, IBrush dark, IBrush light, bool pressed)
    {
        context.FillRectangle(fill, rect);
        var topLeft = pressed ? dark : light;
        var bottomRight = pressed ? light : dark;
        context.DrawLine(new Pen(topLeft, 1), rect.TopLeft, rect.TopRight);
        context.DrawLine(new Pen(topLeft, 1), rect.TopLeft, rect.BottomLeft);
        context.DrawLine(new Pen(bottomRight, 1), rect.BottomLeft, rect.BottomRight);
        context.DrawLine(new Pen(bottomRight, 1), rect.TopRight, rect.BottomRight);
    }

    private static void DrawFitGlyph(DrawingContext context, Rect bounds, IBrush brush)
    {
        var center = bounds.Center;
        var pen = new Pen(brush, 1.2);
        context.DrawLine(pen, new Point(center.X - 5, center.Y), new Point(center.X + 5, center.Y));
        context.DrawLine(pen, new Point(center.X - 5, center.Y), new Point(center.X - 2, center.Y - 3));
        context.DrawLine(pen, new Point(center.X - 5, center.Y), new Point(center.X - 2, center.Y + 3));
        context.DrawLine(pen, new Point(center.X + 5, center.Y), new Point(center.X + 2, center.Y - 3));
        context.DrawLine(pen, new Point(center.X + 5, center.Y), new Point(center.X + 2, center.Y + 3));
    }

    private static void DrawZoomGlyph(
        DrawingContext context,
        Rect bounds,
        bool isPlus,
        bool highlighted,
        IBrush textBrush,
        IBrush accentBrush)
    {
        var brush = highlighted ? accentBrush : textBrush;
        var center = bounds.Center;
        var pen = new Pen(brush, 1.2);
        context.DrawLine(pen, new Point(center.X - 3.5, center.Y), new Point(center.X + 3.5, center.Y));
        if (isPlus)
            context.DrawLine(pen, new Point(center.X, center.Y - 3.5), new Point(center.X, center.Y + 3.5));
    }

    private void DrawScaleFactor(DrawingContext context, double factor, double centerY, IBrush brush)
    {
        var value = factor.ToString("0.0", CultureInfo.InvariantCulture);
        const double suffixGap = 3;
        const double suffixSize = 6;
        var width = value.Sum(character => character == '.' ? 2.5 : 6d)
                    + suffixGap + suffixSize;
        var x = Bounds.Width - width - 5;
        var y = centerY - 4.5;
        var pen = new Pen(brush, 1.2);

        foreach (var character in value)
        {
            if (character == '.')
            {
                context.FillRectangle(brush, new Rect(x, y + 8, 1.4, 1.4));
                x += 2.5;
                continue;
            }

            DrawSegmentDigit(context, character - '0', x, y, pen);
            x += 6;
        }

        x += suffixGap;
        var suffixY = centerY - suffixSize / 2;
        context.DrawLine(pen, new Point(x, suffixY), new Point(x + suffixSize, suffixY + suffixSize));
        context.DrawLine(pen, new Point(x + suffixSize, suffixY), new Point(x, suffixY + suffixSize));
    }

    private static void DrawSegmentDigit(
        DrawingContext context, int digit, double x, double y, Pen pen)
    {
        // 七段线条足以表达缩放倍率，并避免控制区为了短文本初始化字体回退。
        var segments = digit switch
        {
            0 => 0b0111111,
            1 => 0b0000110,
            2 => 0b1011011,
            3 => 0b1001111,
            4 => 0b1100110,
            5 => 0b1101101,
            6 => 0b1111101,
            7 => 0b0000111,
            8 => 0b1111111,
            9 => 0b1101111,
            _ => 0
        };

        if ((segments & 0b0000001) != 0)
            context.DrawLine(pen, new Point(x, y), new Point(x + 4, y));
        if ((segments & 0b0000010) != 0)
            context.DrawLine(pen, new Point(x + 4, y), new Point(x + 4, y + 4.5));
        if ((segments & 0b0000100) != 0)
            context.DrawLine(pen, new Point(x + 4, y + 4.5), new Point(x + 4, y + 9));
        if ((segments & 0b0001000) != 0)
            context.DrawLine(pen, new Point(x, y + 9), new Point(x + 4, y + 9));
        if ((segments & 0b0010000) != 0)
            context.DrawLine(pen, new Point(x, y + 4.5), new Point(x, y + 9));
        if ((segments & 0b0100000) != 0)
            context.DrawLine(pen, new Point(x, y), new Point(x, y + 4.5));
        if ((segments & 0b1000000) != 0)
            context.DrawLine(pen, new Point(x, y + 4.5), new Point(x + 4, y + 4.5));
    }

    private static double NiceInterval(double raw)
    {
        if (raw <= 0 || !double.IsFinite(raw))
            return 1;
        var exponent = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var normalized = raw / exponent;
        var factor = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return factor * exponent;
    }

    private static string FormatRulerTime(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes:00}:{value.Seconds:00}";
    }

    private FormattedText CreateText(string text, double size, IBrush brush)
        => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, size, brush);
}
