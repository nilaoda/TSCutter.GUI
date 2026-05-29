using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Classic.CommonControls;

namespace TSCutter.GUI.Controls;

/// <summary>
/// 纯手工双拇指 RangeSlider。Canvas 承载轨道、拇指和绿色高亮条。
/// 拖动拇指调整起止位置，拖动绿色高亮条整体平移区间（滑动窗口）。
/// </summary>
public class RangeSlider : Canvas
{
    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<RangeSlider, double>(nameof(Minimum), 0);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<RangeSlider, double>(nameof(Maximum), 100);

    public static readonly StyledProperty<double> RangeStartProperty =
        AvaloniaProperty.Register<RangeSlider, double>(nameof(RangeStart), 0);

    public static readonly StyledProperty<double> RangeEndProperty =
        AvaloniaProperty.Register<RangeSlider, double>(nameof(RangeEnd), 100);

    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double RangeStart { get => GetValue(RangeStartProperty); set => SetValue(RangeStartProperty, value); }
    public double RangeEnd { get => GetValue(RangeEndProperty); set => SetValue(RangeEndProperty, value); }

    private readonly Border _track;
    private readonly Border _highlight;
    private readonly Border _startThumb;
    private readonly Border _endThumb;
    private bool _draggingStart;
    private bool _draggingEnd;
    private bool _draggingRange;
    private double _dragRangeStart;
    private double _dragRangeEnd;
    private double _dragRangePointerX;
    private double _trackW;
    private const double ThumbW = 8;
    private const double ThumbH = 22;
    private const double TrackH = 12;
    private const double ControlH = 32;

    static RangeSlider()
    {
        MinimumProperty.Changed.AddClassHandler<RangeSlider>((s, _) => s.UpdateVisuals());
        MaximumProperty.Changed.AddClassHandler<RangeSlider>((s, _) => s.UpdateVisuals());
        RangeStartProperty.Changed.AddClassHandler<RangeSlider>((s, _) =>
        { Dispatcher.UIThread.Post(s.UpdateVisuals, DispatcherPriority.Loaded); });
        RangeEndProperty.Changed.AddClassHandler<RangeSlider>((s, _) =>
        { Dispatcher.UIThread.Post(s.UpdateVisuals, DispatcherPriority.Loaded); });
    }

    public RangeSlider()
    {
        Height = ControlH;
        Cursor = new Cursor(StandardCursorType.SizeWestEast);

        // 轨道背景（Z=0 底层）
        _track = new Border
        {
            Height = TrackH,
            CornerRadius = new CornerRadius(0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            IsHitTestVisible = false,
            ZIndex = 0,
        };

        // 高亮条（Z=1 在轨道上方）
        _highlight = new Border
        {
            Height = TrackH,
            CornerRadius = new CornerRadius(0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            IsHitTestVisible = true,
            Cursor = new Cursor(StandardCursorType.SizeAll),
            ZIndex = 1,
        };

        // 起始拇指
        _startThumb = new Border
        {
            Width = ThumbW,
            Height = ThumbH,
            CornerRadius = new CornerRadius(0),
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
            ZIndex = 2,
        };

        // 结束拇指
        _endThumb = new Border
        {
            Width = ThumbW,
            Height = ThumbH,
            CornerRadius = new CornerRadius(0),
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
            ZIndex = 2,
        };

        _startThumb.PointerPressed += (s, e) => { _draggingStart = true; e.Handled = true; };
        _startThumb.PointerReleased += (s, e) => _draggingStart = false;
        _endThumb.PointerPressed += (s, e) => { _draggingEnd = true; e.Handled = true; };
        _endThumb.PointerReleased += (s, e) => _draggingEnd = false;
        _highlight.PointerPressed += OnHighlightPressed;
        _highlight.PointerReleased += (s, e) => _draggingRange = false;

        Children.Add(_track);         // 底层 Z=0
        Children.Add(_highlight);    // 中层 Z=1
        Children.Add(_startThumb);   // 顶层 Z=2
        Children.Add(_endThumb);     // 顶层 Z=2

        // 统一处理鼠标移动：根据拖动状态更新对应拇指
        PointerMoved += OnPointerMoved;
        // 鼠标释放兜底
        PointerReleased += (s, e) => { _draggingStart = false; _draggingEnd = false; _draggingRange = false; };

        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        var thumbBg = this.FindResource(SystemColors.ControlColorKey) as IBrush
                      ?? new SolidColorBrush(Color.FromRgb(0x36, 0x36, 0x36));
        _startThumb.Background = thumbBg;
        _endThumb.Background = thumbBg;

        var thumbBorder = this.FindResource(SystemColors.ControlDarkColorKey) as IBrush
                          ?? new SolidColorBrush(Color.FromRgb(0x2c, 0x2c, 0x2c));
        _startThumb.BorderBrush = thumbBorder;
        _startThumb.BorderThickness = new Thickness(1);
        _endThumb.BorderBrush = thumbBorder;
        _endThumb.BorderThickness = new Thickness(1);

        _highlight.Background = new SolidColorBrush(Colors.Green);

        var trackColor = this.FindResource("SliderTrackFill") as IBrush
                         ?? new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
        _track.Background = trackColor;
    }

    private void OnHighlightPressed(object? sender, PointerPressedEventArgs e)
    {
        _draggingRange = true;
        _dragRangeStart = RangeStart;
        _dragRangeEnd = RangeEnd;
        _dragRangePointerX = e.GetPosition(this).X;
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_trackW <= 0) return;
        var pos = e.GetPosition(this);
        var ratio = Math.Clamp((pos.X - ThumbW / 2) / (_trackW - ThumbW), 0, 1);
        var val = Minimum + ratio * (Maximum - Minimum);

        if (_draggingStart)
        {
            if (val > RangeEnd) val = RangeEnd;
            RangeStart = val;
        }
        else if (_draggingEnd)
        {
            if (val < RangeStart) val = RangeStart;
            RangeEnd = val;
        }
        else if (_draggingRange)
        {
            var effectiveW = _trackW - ThumbW;
            if (effectiveW <= 0) return;
            var dx = pos.X - _dragRangePointerX;
            var dv = dx / effectiveW * (Maximum - Minimum);
            var newStart = _dragRangeStart + dv;
            var newEnd = _dragRangeEnd + dv;

            if (newStart < Minimum)
            {
                newEnd += Minimum - newStart;
                newStart = Minimum;
            }
            if (newEnd > Maximum)
            {
                newStart -= newEnd - Maximum;
                newEnd = Maximum;
            }

            newStart = Math.Max(Minimum, newStart);
            newEnd = Math.Min(Maximum, newEnd);

            RangeStart = newStart;
            RangeEnd = newEnd;
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);

        _trackW = finalSize.Width;
        SetLeft(_track, 0);
        SetTop(_track, (ControlH - TrackH) / 2);
        _track.Width = _trackW;

        SetTop(_highlight, (ControlH - TrackH) / 2);

        SetTop(_startThumb, (ControlH - ThumbH) / 2);
        SetTop(_endThumb, (ControlH - ThumbH) / 2);

        UpdateVisuals();
        return result;
    }

    private void UpdateVisuals()
    {
        if (_trackW <= ThumbW || Maximum <= Minimum) return;

        var range = Maximum - Minimum;
        var startRatio = Math.Clamp((RangeStart - Minimum) / range, 0, 1);
        var endRatio = Math.Clamp((RangeEnd - Minimum) / range, 0, 1);
        var effectiveW = _trackW - ThumbW;

        var startX = startRatio * effectiveW;
        var endX = endRatio * effectiveW;

        SetLeft(_startThumb, startX);
        SetLeft(_endThumb, endX);

        SetLeft(_highlight, startX + ThumbW / 2);
        _highlight.Width = Math.Max(0, endX - startX);
    }
}