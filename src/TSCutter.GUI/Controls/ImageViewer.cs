using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using TSCutter.GUI.Rendering;

namespace TSCutter.GUI.Controls;

/// <summary>
/// This class provides image display functionality, 
/// supporting mouse drag for movement, mouse wheel for zooming in and out, 
/// and intelligent image scaling for high-DPI scenarios.
/// When AutoFit is enabled, the image automatically scales to fit the control size.
/// </summary>
public class ImageViewer : Control
{
    static ImageViewer()
    {
        AffectsRender<ImageViewer>(
            ImageProperty,
            GpuFrameProperty,
            SourcePixelSizeProperty,
            ZoomProperty,
            OffsetXProperty,
            OffsetYProperty,
            UseHighQualityInterpolationProperty);
    }

    public static readonly StyledProperty<Bitmap?> ImageProperty = AvaloniaProperty.Register<ImageViewer, Bitmap?>(nameof(Image));

    public static readonly StyledProperty<IGpuFrameLease?> GpuFrameProperty =
        AvaloniaProperty.Register<ImageViewer, IGpuFrameLease?>(nameof(GpuFrame));

    public static readonly StyledProperty<PixelSize> SourcePixelSizeProperty =
        AvaloniaProperty.Register<ImageViewer, PixelSize>(nameof(SourcePixelSize));

    public static readonly StyledProperty<double> ZoomProperty = AvaloniaProperty.Register<ImageViewer, double>(nameof(Zoom), 1.0);

    public static readonly StyledProperty<double> OffsetXProperty = AvaloniaProperty.Register<ImageViewer, double>(nameof(OffsetX));

    public static readonly StyledProperty<double> OffsetYProperty = AvaloniaProperty.Register<ImageViewer, double>(nameof(OffsetY));
    
    public static readonly StyledProperty<double> MaxZoomFactorProperty =
        AvaloniaProperty.Register<ImageViewer, double>(nameof(MaxZoomFactor), 4.0);
    
    public static readonly StyledProperty<double> MinZoomFactorProperty =
        AvaloniaProperty.Register<ImageViewer, double>(nameof(MinZoomFactor), 0.01);

    public static readonly StyledProperty<bool> UseHighQualityInterpolationProperty =
        AvaloniaProperty.Register<ImageViewer, bool>(nameof(UseHighQualityInterpolation));

    /// <summary>
    /// When true, the image automatically scales to fit the control bounds when the control is resized.
    /// Set to false when user manually zooms or drags, restored to true when FitToView is called.
    /// </summary>
    public static readonly StyledProperty<bool> AutoFitProperty = AvaloniaProperty.Register<ImageViewer, bool>(nameof(AutoFit), true);

    public Bitmap? Image
    {
        get => GetValue(ImageProperty);
        set => SetValue(ImageProperty, value);
    }

    public IGpuFrameLease? GpuFrame
    {
        get => GetValue(GpuFrameProperty);
        set => SetValue(GpuFrameProperty, value);
    }

    public PixelSize SourcePixelSize
    {
        get => GetValue(SourcePixelSizeProperty);
        set => SetValue(SourcePixelSizeProperty, value);
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

    /// <summary>静态图片缩小时使用高质量插值。视频预览默认关闭以避免增加逐帧渲染开销。</summary>
    public bool UseHighQualityInterpolation
    {
        get => GetValue(UseHighQualityInterpolationProperty);
        set => SetValue(UseHighQualityInterpolationProperty, value);
    }

    public bool AutoFit
    {
        get => GetValue(AutoFitProperty);
        set => SetValue(AutoFitProperty, value);
    }

    public ICommand FitCommand => new RelayCommand(FitToView, () => true);

    public ICommand ActualSizeCommand => new RelayCommand(ShowActualSize, () => true);

    /// <summary>
    /// Returns the physical pixel budget useful for a preview decode. The decoder
    /// still clamps this to the video's native dimensions, so a large viewer never
    /// causes an upscale.
    /// </summary>
    public PixelSize GetDecodeTargetSize()
    {
        var scalingFactor = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        return CalculateDecodeTargetSize(Bounds.Size, scalingFactor);
    }

    internal static PixelSize CalculateDecodeTargetSize(Size bounds, double scalingFactor)
    {
        scalingFactor = scalingFactor > 0 ? scalingFactor : 1.0;
        return new PixelSize(
            Math.Max(1, (int)Math.Ceiling(bounds.Width * scalingFactor)),
            Math.Max(1, (int)Math.Ceiling(bounds.Height * scalingFactor)));
    }
    
    private void FitToView()
    {
        if (Image is null && GpuFrame is null) return;

        var scalingFactor = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var contentSize = GetContentPixelSize(
            Image?.PixelSize ?? GpuFrame?.PixelSize ?? default,
            SourcePixelSize);
        Zoom = Math.Min(Bounds.Width / contentSize.Width, Bounds.Height / contentSize.Height) * scalingFactor;

        OffsetX = (Bounds.Width - contentSize.Width * Zoom / scalingFactor) / 2;
        OffsetY = (Bounds.Height - contentSize.Height * Zoom / scalingFactor) / 2;

        // Re-enable auto-fit when user manually triggers fit
        AutoFit = true;
    }

    private void ShowActualSize()
    {
        if (Image is null && GpuFrame is null) return;

        Zoom = 1.0;
        OffsetX = 0;
        OffsetY = 0;
        AutoFit = false;
    }

    private Size _previousBounds;

    /// <summary>
    /// Override ArrangeOverride to detect size changes and auto-fit the image.
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);

        if (AutoFit && (Image is not null || GpuFrame is not null) && finalSize != _previousBounds && finalSize.Width > 0 && finalSize.Height > 0)
        {
            _previousBounds = finalSize;
            // Use Dispatcher to defer the fit call, ensuring Bounds is updated
            Dispatcher.UIThread.Post(() =>
            {
                var scalingFactor = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
                var contentSize = GetContentPixelSize(
                    Image?.PixelSize ?? GpuFrame?.PixelSize ?? default,
                    SourcePixelSize);
                Zoom = Math.Min(Bounds.Width / contentSize.Width, Bounds.Height / contentSize.Height) * scalingFactor;
                OffsetX = (Bounds.Width - contentSize.Width * Zoom / scalingFactor) / 2;
                OffsetY = (Bounds.Height - contentSize.Height * Zoom / scalingFactor) / 2;
            });
        }

        return result;
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
        Application.Current!.ActualThemeVariantChanged += (sender, e) =>
        {
            // Re-Render after theme changed
            InvalidateVisual();
        };
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Image is null && GpuFrame is null) return;
        
        _lastDragPoint = e.GetPosition(this);
        _isDragging = true;
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (Image is null && GpuFrame is null) return;

        _isDragging = false;
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if ((Image is null && GpuFrame is null) || !_isDragging) return;
        
        var currentPoint = e.GetPosition(this);
        OffsetX += currentPoint.X - _lastDragPoint.X;
        OffsetY += currentPoint.Y - _lastDragPoint.Y;
        _lastDragPoint = currentPoint;
        AutoFit = false;
        InvalidateVisual();
        e.Handled = true;
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if ((Image is null && GpuFrame is null) || (DateTime.Now - _lastZoomTime).TotalMilliseconds < ZoomIntervalMs) return;
        
        var zoomDelta = e.Delta.Y > 0 ? 1.1 : 0.9;
        var zoomCenter = e.GetPosition(this);

        var newZoom = CalculateNextZoom(Zoom, zoomDelta, MinZoomFactor, MaxZoomFactor);
        if (Math.Abs(newZoom - Zoom) < double.Epsilon) return;

        var appliedZoomRatio = newZoom / Zoom;
        
        Zoom = newZoom;

        OffsetX -= (zoomCenter.X - OffsetX) * (appliedZoomRatio - 1);
        OffsetY -= (zoomCenter.Y - OffsetY) * (appliedZoomRatio - 1);

        AutoFit = false;
        InvalidateVisual();
        _lastZoomTime = DateTime.Now;
        e.Handled = true;
    }

    internal static double CalculateNextZoom(double zoom, double zoomDelta, double minimum, double maximum)
    {
        if (!double.IsFinite(zoom) || zoom <= 0 ||
            !double.IsFinite(zoomDelta) || zoomDelta <= 0)
        {
            return zoom;
        }

        var lower = Math.Max(double.Epsilon, Math.Min(minimum, maximum));
        var upper = Math.Max(lower, Math.Max(minimum, maximum));
        return Math.Clamp(zoom * zoomDelta, lower, upper);
    }

    public override void Render(DrawingContext dc)
    {
        base.Render(dc);
        
        // Draw the background
        var backgroundRect = new Rect(0, 0, Bounds.Width, Bounds.Height);
        var customColorBrush = new SolidColorBrush(Colors.Black);
        dc.FillRectangle(customColorBrush, backgroundRect);
        
        if (Image is null && GpuFrame is null) return;

        // Need calc with current System Scaling
            var scalingFactor = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var contentSize = GetContentPixelSize(
            Image?.PixelSize ?? GpuFrame?.PixelSize ?? default,
            SourcePixelSize);
        var imgWidth = contentSize.Width * Zoom / scalingFactor;
        var imgHeight = contentSize.Height * Zoom / scalingFactor;

        if (imgWidth <= 0 || imgHeight <= 0)
            return;

        // Define the destination rectangle where the image will be drawn
        var destRect = new Rect(OffsetX, OffsetY, imgWidth, imgHeight);

        // Get the bounds of the ImageViewer control
        var viewerBounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        var visibleRect = viewerBounds.Intersect(destRect);

        if (visibleRect is not { Width: > 0, Height: > 0 }) return;
        
        if (GpuFrame is not null)
        {
            // The native visual owns the GPU image; only its viewport changes
            // while the user zooms, pans, or resizes the control.
            if (!GpuFrame.TryAttach(this))
                return;
            GpuFrame.UpdateViewport(destRect);
            return;
        }

        if (Image is null)
            return;

        // Define the source rectangle based on the visible area
        var srcRect = MapVisibleRectToBitmap(visibleRect, destRect, Image.PixelSize);

        if (UseHighQualityInterpolation)
        {
            using (dc.PushRenderOptions(new RenderOptions
                   {
                       BitmapInterpolationMode = BitmapInterpolationMode.HighQuality
                   }))
            {
                dc.DrawImage(Image, srcRect, visibleRect);
            }
        }
        else
        {
            dc.DrawImage(Image, srcRect, visibleRect);
        }
    }

    internal static PixelSize GetContentPixelSize(PixelSize bitmapSize, PixelSize sourceSize) =>
        sourceSize.Width > 0 && sourceSize.Height > 0 ? sourceSize : bitmapSize;

    internal static Rect MapVisibleRectToBitmap(Rect visibleRect, Rect destinationRect, PixelSize bitmapSize)
    {
        if (destinationRect.Width <= 0 || destinationRect.Height <= 0 ||
            bitmapSize.Width <= 0 || bitmapSize.Height <= 0)
        {
            return default;
        }

        return new Rect(
            (visibleRect.X - destinationRect.X) / destinationRect.Width * bitmapSize.Width,
            (visibleRect.Y - destinationRect.Y) / destinationRect.Height * bitmapSize.Height,
            visibleRect.Width / destinationRect.Width * bitmapSize.Width,
            visibleRect.Height / destinationRect.Height * bitmapSize.Height);
    }
}
