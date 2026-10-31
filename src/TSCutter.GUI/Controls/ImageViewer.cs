using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.Input;
using TSCutter.GUI.Rendering;
using TSCutter.GUI.Rendering.Gpu;
using TSCutter.GUI.Models.Preview;

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
            SubtitleOverlayProperty,
            GpuFrameProperty,
            SourcePixelSizeProperty,
            ZoomProperty,
            OffsetXProperty,
            OffsetYProperty,
            ShowNavigatorProperty,
            UseHighQualityInterpolationProperty);
    }

    public static readonly StyledProperty<Bitmap?> ImageProperty = AvaloniaProperty.Register<ImageViewer, Bitmap?>(nameof(Image));

    public static readonly StyledProperty<SubtitleOverlay?> SubtitleOverlayProperty =
        AvaloniaProperty.Register<ImageViewer, SubtitleOverlay?>(nameof(SubtitleOverlay));

    public SubtitleOverlay? SubtitleOverlay
    {
        get => GetValue(SubtitleOverlayProperty);
        set => SetValue(SubtitleOverlayProperty, value);
    }

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

    public static readonly StyledProperty<bool> ShowNavigatorProperty =
        AvaloniaProperty.Register<ImageViewer, bool>(nameof(ShowNavigator), true);

    /// <summary>
    /// When true, the image automatically scales to fit the control bounds when the control is resized.
    /// Set to false when user manually zooms or drags, restored to true when FitToView is called.
    /// </summary>
    public static readonly StyledProperty<bool> AutoFitProperty = AvaloniaProperty.Register<ImageViewer, bool>(nameof(AutoFit), true);

    /// <summary>
    /// When true, loading a new image restores auto-fit and calculates the fitted
    /// transform before the image is rendered for the first time.
    /// </summary>
    public static readonly StyledProperty<bool> FitOnSourceChangedProperty =
        AvaloniaProperty.Register<ImageViewer, bool>(nameof(FitOnSourceChanged));

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

    public bool ShowNavigator
    {
        get => GetValue(ShowNavigatorProperty);
        set => SetValue(ShowNavigatorProperty, value);
    }

    public bool AutoFit
    {
        get => GetValue(AutoFitProperty);
        set => SetValue(AutoFitProperty, value);
    }

    public bool FitOnSourceChanged
    {
        get => GetValue(FitOnSourceChangedProperty);
        set => SetValue(FitOnSourceChangedProperty, value);
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

        AutoFit = true;
        ApplyFitTransform(Bounds.Size);
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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if ((!ShowNavigator && _isNavigatorDragging) || (Image is null && GpuFrame is null))
            StopDragging();

        var sourceChanged = change.Property == ImageProperty || change.Property == GpuFrameProperty;
        var geometryChanged = sourceChanged || change.Property == SourcePixelSizeProperty;
        if (!geometryChanged || (Image is null && GpuFrame is null))
            return;

        if (sourceChanged && FitOnSourceChanged)
            AutoFit = true;

        if (AutoFit)
            ApplyFitTransform(Bounds.Size);
    }

    /// <summary>
    /// Override ArrangeOverride to detect size changes and auto-fit the image.
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);

        var boundsChanged = finalSize != _previousBounds;
        _previousBounds = finalSize;
        if (AutoFit && (Image is not null || GpuFrame is not null) && boundsChanged)
            ApplyFitTransform(finalSize);

        return result;
    }

    private void ApplyFitTransform(Size viewportSize)
    {
        var contentSize = GetContentPixelSize(
            Image?.PixelSize ?? GpuFrame?.PixelSize ?? default,
            SourcePixelSize);
        var scalingFactor = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var transform = CalculateFitTransform(viewportSize, contentSize, scalingFactor);
        if (transform is not { } value)
            return;

        Zoom = value.Zoom;
        OffsetX = value.OffsetX;
        OffsetY = value.OffsetY;
    }

    internal static (double Zoom, double OffsetX, double OffsetY)? CalculateFitTransform(
        Size viewportSize,
        PixelSize contentSize,
        double scalingFactor)
    {
        if (viewportSize.Width <= 0 || viewportSize.Height <= 0
            || contentSize.Width <= 0 || contentSize.Height <= 0)
        {
            return null;
        }

        scalingFactor = scalingFactor > 0 ? scalingFactor : 1.0;
        var zoom = Math.Min(
            viewportSize.Width / contentSize.Width,
            viewportSize.Height / contentSize.Height) * scalingFactor;
        return (
            zoom,
            (viewportSize.Width - contentSize.Width * zoom / scalingFactor) / 2,
            (viewportSize.Height - contentSize.Height * zoom / scalingFactor) / 2);
    }

    private Point _lastDragPoint;
    private bool _isDragging;
    private bool _isNavigatorDragging;
    private bool _navigatorMoved;
    private Vector _navigatorDragOffset;
    private IPointer? _dragPointer;
    private DateTime _lastZoomTime = DateTime.Now;
    private const int ZoomIntervalMs = 16; // 60 FPS
    public ImageViewer()
    {
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Tunnel);
        ActualThemeVariantChanged += (sender, e) =>
        {
            // Re-Render after theme changed
            InvalidateVisual();
        };
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((Image is null && GpuFrame is null) || _dragPointer is not null) return;

        var point = e.GetPosition(this);
        var navigator = GetNavigatorLayout();
        if (navigator is { } layout && layout.Bounds.Contains(point))
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;
            _isNavigatorDragging = true;
            _navigatorMoved = false;
            _lastDragPoint = point;
            _navigatorDragOffset = layout.Viewport.Contains(point)
                ? point - layout.Viewport.Center
                : default;
            if (!layout.Viewport.Contains(point))
                NavigateTo(point, layout);
        }
        else
        {
            _lastDragPoint = point;
            _isDragging = true;
        }
        _dragPointer = e.Pointer;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(_dragPointer, e.Pointer)) return;
        if (_isNavigatorDragging && !_navigatorMoved && GetNavigatorLayout() is { } layout &&
            layout.Bounds.Contains(e.GetPosition(this)))
            NavigateTo(e.GetPosition(this), layout);
        StopDragging();
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if ((Image is null && GpuFrame is null) || !ReferenceEquals(_dragPointer, e.Pointer)) return;

        var currentPoint = e.GetPosition(this);
        if (_isNavigatorDragging)
        {
            if (GetNavigatorLayout() is { } layout)
            {
                var delta = currentPoint - _lastDragPoint;
                _navigatorMoved |= delta.X * delta.X + delta.Y * delta.Y > 9;
                if (_navigatorMoved)
                    NavigateTo(currentPoint - _navigatorDragOffset, layout);
            }
            else
                StopDragging();
            e.Handled = true;
            return;
        }
        if (!_isDragging) return;
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
        if (GetNavigatorLayout() is { } layout && layout.Bounds.Contains(zoomCenter))
            zoomCenter = new Point(Bounds.Width / 2, Bounds.Height / 2);

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

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        StopDragging();
    }

    private void StopDragging()
    {
        var pointer = _dragPointer;
        _dragPointer = null;
        _isDragging = false;
        _isNavigatorDragging = false;
        if (pointer?.Captured == this)
            pointer.Capture(null);
    }

    private Rect GetImageDestinationRect()
    {
        var scalingFactor = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var contentSize = GetContentPixelSize(
            Image?.PixelSize ?? GpuFrame?.PixelSize ?? default, SourcePixelSize);
        return new Rect(OffsetX, OffsetY,
            contentSize.Width * Zoom / scalingFactor, contentSize.Height * Zoom / scalingFactor);
    }

    private NavigatorLayout? GetNavigatorLayout() => ShowNavigator && (Image is not null || GpuFrame is SkiaVideoFrame)
        ? CalculateNavigatorLayout(Bounds.Size, GetImageDestinationRect())
        : null;

    private void NavigateTo(Point point, NavigatorLayout layout)
    {
        var position = new Point(
            (point.X - layout.Image.X) / layout.Image.Width,
            (point.Y - layout.Image.Y) / layout.Image.Height);
        var offset = CalculateNavigatorOffset(position, Bounds.Size, GetImageDestinationRect().Size);
        AutoFit = false;
        OffsetX = offset.X;
        OffsetY = offset.Y;
    }

    internal readonly record struct NavigatorLayout(Rect Bounds, Rect Image, Rect Viewport);

    internal static NavigatorLayout? CalculateNavigatorLayout(Size viewportSize, Rect destination)
    {
        const double margin = 12;
        const double padding = 5;
        const double tolerance = 0.5;
        if (viewportSize.Width <= 0 || viewportSize.Height <= 0 ||
            destination.Width <= 0 || destination.Height <= 0 ||
            !double.IsFinite(destination.X) || !double.IsFinite(destination.Y) ||
            !double.IsFinite(destination.Width) || !double.IsFinite(destination.Height))
            return null;
        if (destination.X >= -tolerance && destination.Y >= -tolerance &&
            destination.Right <= viewportSize.Width + tolerance &&
            destination.Bottom <= viewportSize.Height + tolerance)
            return null;

        var maximumWidth = Math.Min(120, viewportSize.Width / 4);
        var maximumHeight = Math.Min(80, viewportSize.Height / 4);
        if (maximumWidth < 40 || maximumHeight < 30)
            return null;
        var scale = Math.Min(maximumWidth / destination.Width, maximumHeight / destination.Height);
        var size = new Size(destination.Width * scale, destination.Height * scale);
        var image = new Rect(viewportSize.Width - margin - padding - size.Width,
            viewportSize.Height - margin - padding - size.Height, size.Width, size.Height);
        var visible = new Rect(viewportSize).Intersect(destination);
        var viewport = visible is { Width: > 0, Height: > 0 }
            ? new Rect(image.X + (visible.X - destination.X) * scale,
                image.Y + (visible.Y - destination.Y) * scale,
                visible.Width * scale, visible.Height * scale)
            : default;
        return new NavigatorLayout(image.Inflate(padding), image, viewport);
    }

    internal static Point CalculateNavigatorOffset(Point position, Size viewport, Size image)
    {
        // 小地图导航保持画面覆盖窗口；未放大的轴仍居中，避免产生额外留白。
        return new Point(
            image.Width <= viewport.Width ? (viewport.Width - image.Width) / 2 :
                Math.Clamp(viewport.Width / 2 - Math.Clamp(position.X, 0, 1) * image.Width,
                    viewport.Width - image.Width, 0),
            image.Height <= viewport.Height ? (viewport.Height - image.Height) / 2 :
                Math.Clamp(viewport.Height / 2 - Math.Clamp(position.Y, 0, 1) * image.Height,
                    viewport.Height - image.Height, 0));
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
        SkiaVideoFramePresenter.Probe(dc, backgroundRect);
        
        if (Image is null && GpuFrame is null) return;

        var destRect = GetImageDestinationRect();
        if (destRect.Width <= 0 || destRect.Height <= 0)
            return;

        // Get the bounds of the ImageViewer control
        var viewerBounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        var visibleRect = viewerBounds.Intersect(destRect);

        if (GpuFrame is not null)
        {
            if (visibleRect is not { Width: > 0, Height: > 0 }) return;
            if (GpuFrame is SkiaVideoFrame shaderFrame)
            {
                shaderFrame.Draw(dc, destRect, visibleRect);
                DrawSubtitles(dc, destRect, visibleRect);
                if (GetNavigatorLayout() is { } navigator)
                    RenderNavigator(dc, navigator);
                return;
            }
            // The native visual owns the GPU image; only its viewport changes
            // while the user zooms, pans, or resizes the control.
            if (!GpuFrame.TryAttach(this))
                return;
            GpuFrame.UpdateViewport(destRect);
            DrawSubtitles(dc, destRect, visibleRect);
            return;
        }

        if (Image is null)
            return;

        if (visibleRect is { Width: > 0, Height: > 0 })
        {
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
                dc.DrawImage(Image, srcRect, visibleRect);
        }
        DrawSubtitles(dc, destRect, visibleRect);
        if (GetNavigatorLayout() is { } layout)
            RenderNavigator(dc, layout);
    }

    private static readonly IBrush SubtitleBackdropBrush = new ImmutableSolidColorBrush(Color.FromArgb(148, 0, 0, 0));

    private void DrawSubtitles(DrawingContext dc, Rect destination, Rect visible)
    {
        if (SubtitleOverlay is not { CanvasSize.Width: > 0, CanvasSize.Height: > 0 } overlay) return;
        if (overlay.Backdrop is { } backdrop && visible is { Width: > 0, Height: > 0 })
        {
            var bounds = GetSubtitlePartRect(destination, overlay.CanvasSize, backdrop.Bounds);
            using (dc.PushClip(visible))
                dc.DrawRectangle(SubtitleBackdropBrush, null, bounds,
                    backdrop.CornerRadius * destination.Width / overlay.CanvasSize.Width,
                    backdrop.CornerRadius * destination.Height / overlay.CanvasSize.Height);
        }
        // 字幕独立使用高质量采样，缩小显示时保留字形边缘，不依赖视频的插值设置。
        using (dc.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
        {
            foreach (var part in overlay.Parts)
            {
                var bounds = GetSubtitlePartRect(destination, overlay.CanvasSize,
                    new Rect(part.X, part.Y, part.Bitmap.PixelSize.Width, part.Bitmap.PixelSize.Height));
                var clipped = visible.Intersect(bounds);
                if (clipped is { Width: > 0, Height: > 0 })
                    dc.DrawImage(part.Bitmap, MapVisibleRectToBitmap(clipped, bounds, part.Bitmap.PixelSize), clipped);
            }
        }
    }

    // 字幕和视频共用目标矩形，缩放、平移及非方形像素校正都保持对齐。
    internal static Rect GetSubtitlePartRect(Rect destination, PixelSize canvas, Rect part) => new(
        destination.X + part.X / canvas.Width * destination.Width,
        destination.Y + part.Y / canvas.Height * destination.Height,
        part.Width / canvas.Width * destination.Width,
        part.Height / canvas.Height * destination.Height);

    private static readonly IBrush NavigatorBackground = new ImmutableSolidColorBrush(Color.FromArgb(220, 24, 24, 24));
    private static readonly IBrush NavigatorMask = new ImmutableSolidColorBrush(Color.FromArgb(110, 0, 0, 0));
    private static readonly IPen NavigatorBorder = new ImmutablePen(new ImmutableSolidColorBrush(Colors.Gray), 1);
    private static readonly IPen NavigatorViewportBorder = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#8CCBFF")), 1.5);

    private void RenderNavigator(DrawingContext dc, NavigatorLayout layout)
    {
        dc.DrawRectangle(NavigatorBackground, NavigatorBorder, layout.Bounds, 4, 4);
        // 复用当前位图，平移与缩放只更新坐标，不生成或缓存新的缩略图。
        if (GpuFrame is SkiaVideoFrame shaderFrame)
            shaderFrame.Draw(dc, layout.Image, layout.Image);
        else if (Image is not null)
            dc.DrawImage(Image, new Rect(0, 0, Image.PixelSize.Width, Image.PixelSize.Height), layout.Image);
        if (layout.Viewport is not { Width: > 0, Height: > 0 })
        {
            dc.FillRectangle(NavigatorMask, layout.Image);
            return;
        }
        var image = layout.Image;
        var viewport = layout.Viewport;
        dc.FillRectangle(NavigatorMask, new Rect(image.X, image.Y, image.Width, Math.Max(0, viewport.Y - image.Y)));
        dc.FillRectangle(NavigatorMask, new Rect(image.X, viewport.Bottom, image.Width, Math.Max(0, image.Bottom - viewport.Bottom)));
        dc.FillRectangle(NavigatorMask, new Rect(image.X, viewport.Y, Math.Max(0, viewport.X - image.X), viewport.Height));
        dc.FillRectangle(NavigatorMask, new Rect(viewport.Right, viewport.Y, Math.Max(0, image.Right - viewport.Right), viewport.Height));
        dc.DrawRectangle(null, NavigatorViewportBorder, viewport);
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
