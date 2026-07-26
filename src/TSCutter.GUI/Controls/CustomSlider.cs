using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TSCutter.GUI.Models;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.Controls;

public class CustomSlider : Grid
{
    private readonly Slider _innerSlider;
    private readonly Canvas _highlightCanvas;
    private readonly Border _highlightContainer;
    private Track? _track;
    private Thumb? _thumb;
    private RepeatButton? _decreaseButton;
    private bool _syncingValue;

    public static readonly StyledProperty<double> MinimumProperty =
        Slider.MinimumProperty.AddOwner<CustomSlider>();

    public static readonly StyledProperty<double> MaximumProperty =
        Slider.MaximumProperty.AddOwner<CustomSlider>();

    public static readonly StyledProperty<double> ValueProperty =
        Slider.ValueProperty.AddOwner<CustomSlider>();

    public static readonly StyledProperty<double> RangeStartProperty =
        AvaloniaProperty.Register<CustomSlider, double>(nameof(RangeStart), -1);

    public static readonly StyledProperty<double> RangeEndProperty =
        AvaloniaProperty.Register<CustomSlider, double>(nameof(RangeEnd), -1);

    public static readonly StyledProperty<IReadOnlyList<ClipTimelineRange>?> RangesProperty =
        AvaloniaProperty.Register<CustomSlider, IReadOnlyList<ClipTimelineRange>?>(nameof(Ranges));

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double RangeStart
    {
        get => GetValue(RangeStartProperty);
        set => SetValue(RangeStartProperty, value);
    }

    public double RangeEnd
    {
        get => GetValue(RangeEndProperty);
        set => SetValue(RangeEndProperty, value);
    }

    public IReadOnlyList<ClipTimelineRange>? Ranges
    {
        get => GetValue(RangesProperty);
        set => SetValue(RangesProperty, value);
    }

    static CustomSlider()
    {
        MinimumProperty.Changed.AddClassHandler<CustomSlider>((s, e) =>
        {
            s._innerSlider.Minimum = (double)e.NewValue!;
            s.UpdateHighlightPosition();
        });
        MaximumProperty.Changed.AddClassHandler<CustomSlider>((s, e) =>
        {
            s._innerSlider.Maximum = (double)e.NewValue!;
            s.UpdateHighlightPosition();
        });
        ValueProperty.Changed.AddClassHandler<CustomSlider>((s, e) =>
        {
            if (!s._syncingValue)
            {
                s._syncingValue = true;
                s._innerSlider.Value = (double)e.NewValue!;
                s._syncingValue = false;
            }
        });
        RangeStartProperty.Changed.AddClassHandler<CustomSlider>((s, _) => s.UpdateHighlightPosition());
        RangeEndProperty.Changed.AddClassHandler<CustomSlider>((s, _) => s.UpdateHighlightPosition());
        RangesProperty.Changed.AddClassHandler<CustomSlider>((s, _) => s.UpdateHighlightPosition());
    }

    public CustomSlider()
    {
        RowDefinitions = new RowDefinitions("Auto,5");

        _innerSlider = new Slider
        {
            Minimum = 0,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        Grid.SetRow(_innerSlider, 0);

        _innerSlider.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        _innerSlider.AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
        _innerSlider.AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);

        var tooltip = new ToolTip { IsVisible = false };
        ToolTip.SetTip(_innerSlider, tooltip);

        _innerSlider.PropertyChanged += InnerSliderPropertyChanged;
        _innerSlider.TemplateApplied += InnerSlider_TemplateApplied;

        _highlightCanvas = new Canvas
        {
            IsHitTestVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
        };

        // 固定高度的容器，始终保持 5px 高度，避免布局抖动
        _highlightContainer = new Border
        {
            IsHitTestVisible = false,
            Height = 5,
            Child = _highlightCanvas,
        };
        Grid.SetRow(_highlightContainer, 1);

        Children.Add(_innerSlider);
        Children.Add(_highlightContainer);
    }

    private void InnerSliderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Slider.ValueProperty && !_syncingValue)
        {
            _syncingValue = true;
            Value = _innerSlider.Value;
            _syncingValue = false;
        }
    }

    private bool _isDragging;

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _isDragging = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isDragging = false;
        OnValueChangedAfterMouseUp(_innerSlider.Value);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var point = e.GetPosition(_innerSlider);
        var relativePosition = point.X / _innerSlider.Bounds.Width;
        var value = _innerSlider.Minimum + relativePosition * (_innerSlider.Maximum - _innerSlider.Minimum);

        if (_innerSlider.GetValue(ToolTip.TipProperty) is ToolTip tooltip)
        {
            tooltip.Content = CommonUtil.FormatSeconds(value);
            if (!tooltip.IsVisible)
                tooltip.IsVisible = true;
        }
    }

    public event EventHandler<double> ValueChangedAfterMouseUp;

    protected virtual void OnValueChangedAfterMouseUp(double newValue)
    {
        if (!_isDragging)
            ValueChangedAfterMouseUp?.Invoke(this, newValue);
    }

    private void InnerSlider_TemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        _track = FindVisualChild<Track>(_innerSlider);
        if (_track != null)
        {
            _thumb = FindVisualChild<Thumb>(_track);
            // Track 内第一个 RepeatButton 是 decrease button，其宽度 = Thumb 左边缘偏移
            _decreaseButton = FindDirectChild<RepeatButton>(_track);
        }
        Dispatcher.UIThread.Post(UpdateHighlightPosition, DispatcherPriority.Loaded);
    }

    private static T? FindVisualChild<T>(Visual parent) where T : Visual
    {
        var queue = new Queue<Visual>();
        queue.Enqueue(parent);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current is T found) return found;
            foreach (var child in current.GetVisualChildren())
                queue.Enqueue(child);
        }
        return null;
    }

    private static T? FindDirectChild<T>(Visual parent) where T : Visual
    {
        foreach (var child in parent.GetVisualChildren())
            if (child is T found) return found;
        return null;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateHighlightPosition();
    }

    private void UpdateHighlightPosition()
    {
        if (_track == null || _thumb == null || Maximum <= Minimum)
        {
            _highlightCanvas.Opacity = 0;
            return;
        }

        // 直接获取 Thumb 当前中心在 Grid 坐标系中的精确位置
        var thumbCenterCurrent = _thumb.TranslatePoint(new Point(_thumb.Bounds.Width / 2, 0), this);
        if (thumbCenterCurrent == null) return;

        var thumbWidth = _thumb.Bounds.Width;
        var trackWidth = _track.Bounds.Width;
        var effectiveWidth = trackWidth - thumbWidth;
        if (effectiveWidth <= 0)
        {
            _highlightCanvas.Opacity = 0;
            return;
        }

        // 用当前 Thumb 位置和当前 Value 反推 Thumb 在 Value=Min 时的基准中心位置
        var currentRatio = Math.Clamp((Value - Minimum) / (Maximum - Minimum), 0, 1);
        var baseCenterX = thumbCenterCurrent.Value.X - currentRatio * effectiveWidth;

        _highlightCanvas.Width = Bounds.Width;
        _highlightCanvas.Children.Clear();

        var ranges = Ranges is { Count: > 0 }
            ? Ranges
            : RangeStart >= 0 && RangeEnd >= 0
                ? [new ClipTimelineRange(RangeStart, RangeEnd, true)]
                : null;
        if (ranges is null)
        {
            _highlightCanvas.Opacity = 0;
            return;
        }

        var valueRange = Maximum - Minimum;
        // 非活动区间先绘制，活动区间最后绘制，重叠时仍能看出当前编辑对象。
        foreach (var item in ranges.OrderBy(range => range.IsActive))
        {
            if (item.End <= item.Start)
                continue;
            var startRatio = Math.Clamp((item.Start - Minimum) / valueRange, 0, 1);
            var endRatio = Math.Clamp((item.End - Minimum) / valueRange, 0, 1);
            var left = baseCenterX + startRatio * effectiveWidth;
            var width = Math.Max(1, (endRatio - startRatio) * effectiveWidth);
            var highlight = new Border
            {
                Width = width,
                Height = 5,
                Background = new SolidColorBrush(Colors.Green),
                Opacity = item.IsActive ? 1 : 0.55,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(highlight, left);
            Canvas.SetTop(highlight, 0);
            _highlightCanvas.Children.Add(highlight);
        }
        _highlightCanvas.Opacity = _highlightCanvas.Children.Count > 0 ? 1 : 0;
    }
}
