using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.Input;

namespace TSCutter.GUI.Controls;

public class ImageViewer : Control
{
    static ImageViewer()
    {
        AffectsRender<ImageViewer>(ImageProperty, ZoomProperty, OffsetXProperty, OffsetYProperty);
    }

    public static readonly StyledProperty<Bitmap?> ImageProperty = AvaloniaProperty.Register<ImageViewer, Bitmap?>(nameof(Image));

    public static readonly StyledProperty<double> ZoomProperty = AvaloniaProperty.Register<ImageViewer, double>(nameof(Zoom), 1.0);

    public static readonly StyledProperty<double> OffsetXProperty = AvaloniaProperty.Register<ImageViewer, double>(nameof(OffsetX));

    public static readonly StyledProperty<double> OffsetYProperty = AvaloniaProperty.Register<ImageViewer, double>(nameof(OffsetY));
    
    public static readonly StyledProperty<double> MaxZoomFactorProperty = AvaloniaProperty.Register<ImageViewer, double>(nameof(MaxZoomFactor));
    
    public static readonly StyledProperty<double> MinZoomFactorProperty = AvaloniaProperty.Register<ImageViewer, double>(nameof(MinZoomFactor));

    public Bitmap? Image
    {
        get => GetValue(ImageProperty);
        set => SetValue(ImageProperty, value);
    }

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public double OffsetX
    {
        get => GetValue(OffsetXProperty);
        set => SetValue(OffsetXProperty, value);
    }

    public double OffsetY
    {
        get => GetValue(OffsetYProperty);
        set => SetValue(OffsetYProperty, value);
    }

    public double MaxZoomFactor
    {
        get => GetValue(MaxZoomFactorProperty);
        set => SetValue(MaxZoomFactorProperty, value);
    }

    public double MinZoomFactor
    {
        get => GetValue(MinZoomFactorProperty);
        set => SetValue(MinZoomFactorProperty, value);
    }

    public ICommand FitCommand => new RelayCommand(FitToView, () => true);
    
    private void FitToView()
    {
        if (Image is null) return;

        Zoom = Math.Min(Bounds.Width / Image.PixelSize.Width, Bounds.Height / Image.PixelSize.Height);

        OffsetX = (Bounds.Width - Image.PixelSize.Width * Zoom) / 2;
        OffsetY = 0;
    }

    private Point _lastDragPoint;
    private bool _isDragging;
    private DateTime _lastZoomTime = DateTime.Now;
    private const int ZoomIntervalMs = 16; // 60 FPS
    public ImageViewer()
    {
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Tunnel);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Image is null) return;
        
        _lastDragPoint = e.GetPosition(this);
        _isDragging = true;
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (Image is null) return;

        _isDragging = false;
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (Image is null || !_isDragging) return;
        
        var currentPoint = e.GetPosition(this);
        OffsetX += currentPoint.X - _lastDragPoint.X;
        OffsetY += currentPoint.Y - _lastDragPoint.Y;
        _lastDragPoint = currentPoint;
        InvalidateVisual();
        e.Handled = true;
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (Image is null || (DateTime.Now - _lastZoomTime).TotalMilliseconds < ZoomIntervalMs) return;
        
        var zoomDelta = e.Delta.Y > 0 ? 1.1 : 0.9;
        var zoomCenter = e.GetPosition(this);

        var newZoom = Zoom * zoomDelta;

        if (newZoom > MaxZoomFactor) newZoom = MaxZoomFactor;
        if (newZoom < MinZoomFactor) newZoom = MinZoomFactor;
        
        if (newZoom == MaxZoomFactor || newZoom == MinZoomFactor) return;
        
        Zoom = newZoom;

        OffsetX -= (zoomCenter.X - OffsetX) * (zoomDelta - 1);
        OffsetY -= (zoomCenter.Y - OffsetY) * (zoomDelta - 1);

        InvalidateVisual();
        _lastZoomTime = DateTime.Now;
        e.Handled = true;
    }

    public override void Render(DrawingContext dc)
    {
        base.Render(dc);
        
        // Draw the background
        var backgroundRect = new Rect(0, 0, Bounds.Width, Bounds.Height);
        var customColorBrush = new SolidColorBrush(Color.FromRgb(40, 40, 44));
        dc.FillRectangle(customColorBrush, backgroundRect);
        
        if (Image is null) return;

        var imgWidth = Image.PixelSize.Width * Zoom;
        var imgHeight = Image.PixelSize.Height * Zoom;

        // Define the destination rectangle where the image will be drawn
        var destRect = new Rect(OffsetX, OffsetY, imgWidth, imgHeight);

        // Get the bounds of the ImageViewer control
        var viewerBounds = new Rect(0, 0, Bounds.Width, Bounds.Height);

        // Calculate the intersection of the image rectangle with the viewer bounds
        var visibleRect = viewerBounds.Intersect(destRect);

        if (visibleRect is not { Width: > 0, Height: > 0 }) return;
        
        // Define the source rectangle based on the visible area
        var srcRect = new Rect(
            (visibleRect.X - OffsetX) / Zoom,
            (visibleRect.Y - OffsetY) / Zoom,
            visibleRect.Width / Zoom,
            visibleRect.Height / Zoom);

        // Draw the image using DrawImage
        dc.DrawImage(Image, srcRect, visibleRect);
    }
}