using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;

namespace TSCutter.GUI.Controls;

public class CustomSlider: Slider, IStyleable
{
    Type IStyleable.StyleKey => typeof(Slider);
    
    private bool _isDragging;

    public CustomSlider()
    {
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
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

    public event EventHandler<double> ValueChangedAfterMouseUp;

    protected virtual void OnValueChangedAfterMouseUp(double newValue)
    {
        if (!_isDragging)
            ValueChangedAfterMouseUp?.Invoke(this, newValue);
    }
}