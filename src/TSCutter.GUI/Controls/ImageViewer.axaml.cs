using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace TSCutter.GUI.Controls;

public partial class ImageViewer : UserControl
{
    private bool _isDragging;
    private Point _lastPosition;

    public ImageViewer()
    {
        InitializeComponent();
    }

    public static readonly StyledProperty<IImage?> ImageSourceProperty = AvaloniaProperty.Register<ImageViewer, IImage>(nameof(ImageSource));

    public IImage? ImageSource
    {
        get => GetValue(ImageSourceProperty);
        set => SetValue(ImageSourceProperty, value);
    }

    public static readonly StyledProperty<double> ZoomFactorProperty = AvaloniaProperty.Register<ImageViewer, double>(nameof(ZoomFactor), 1.0);

    public double ZoomFactor
    {
        get => GetValue(ZoomFactorProperty);
        set => SetValue(ZoomFactorProperty, value);
    }

    private void OnPointerWheelChanged(object sender, PointerWheelEventArgs e)
    {
        if (ImageSource is null) return;
        // Suppress default behavior
        e.Handled = true;
        
        var delta = e.Delta.Y;
        ZoomFactor *= delta > 0 ? 1.1 : 0.9;
    }

    private void OnPointerPressed(object sender, PointerPressedEventArgs e)
    {
        if (ImageSource is null) return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _isDragging = true;
            _lastPosition = e.GetPosition(this);
        }
    }

    private void OnPointerMoved(object sender, PointerEventArgs e)
    {
        if (ImageSource is null) return;
        if (_isDragging)
        {
            var currentPosition = e.GetPosition(this);
            var offset = currentPosition - _lastPosition;

            // Move the image within the canvas
            var left = Canvas.GetLeft(MainImage) + offset.X;
            var top = Canvas.GetTop(MainImage) + offset.Y;

            Canvas.SetLeft(MainImage, left);
            Canvas.SetTop(MainImage, top);

            _lastPosition = currentPosition;
        }
    }

    private void OnPointerReleased(object sender, PointerReleasedEventArgs e)
    {
        if (ImageSource is null) return;
        _isDragging = false;
    }
}