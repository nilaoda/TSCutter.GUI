using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.Controls;

public class CustomSlider: Slider, IStyleable
{
    Type IStyleable.StyleKey => typeof(Slider);
    
    private bool _isDragging;
    private readonly ToolTip _tooltip;

    public CustomSlider()
    {
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        _tooltip = new ToolTip
        {
            IsVisible = false
        };
        ToolTip.SetTip(this, _tooltip);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _isDragging = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isDragging = false;
        OnValueChangedAfterMouseUp(Value);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var point = e.GetPosition(this);

        // calc hover value
        var relativePosition = point.X / Bounds.Width;
        var value = Minimum + relativePosition * (Maximum - Minimum);

        // refresh Tooltip
        _tooltip.Content = CommonUtil.FormatSeconds(value);

        if (!_tooltip.IsVisible)
            _tooltip.IsVisible = true;

        ToolTip.SetTip(this, _tooltip);
    }

    public event EventHandler<double> ValueChangedAfterMouseUp;

    protected virtual void OnValueChangedAfterMouseUp(double newValue)
    {
        if (!_isDragging)
            ValueChangedAfterMouseUp?.Invoke(this, newValue);
    }
}