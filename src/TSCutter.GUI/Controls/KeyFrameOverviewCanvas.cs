using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using TSCutter.GUI.Models;

namespace TSCutter.GUI.Controls;

public sealed class KeyFrameOverviewCanvas : Control
{
    public static readonly StyledProperty<IReadOnlyList<KeyFrameOverviewTile>?> ItemsProperty =
        AvaloniaProperty.Register<KeyFrameOverviewCanvas, IReadOnlyList<KeyFrameOverviewTile>?>(nameof(Items));

    public static readonly StyledProperty<double> TileWidthProperty =
        AvaloniaProperty.Register<KeyFrameOverviewCanvas, double>(nameof(TileWidth), 220);

    public static readonly StyledProperty<double> VerticalOffsetProperty =
        AvaloniaProperty.Register<KeyFrameOverviewCanvas, double>(nameof(VerticalOffset));

    public static readonly StyledProperty<double> ViewportHeightProperty =
        AvaloniaProperty.Register<KeyFrameOverviewCanvas, double>(nameof(ViewportHeight));

    private const double ImageAspect = 9d / 16d;
    private const double ImageLabelHeight = 38;
    private const double HorizontalGap = 12;
    private const double VerticalGap = 14;
    private IReadOnlyList<KeyFrameOverviewTile>? subscribedItems;

    static KeyFrameOverviewCanvas()
    {
        AffectsMeasure<KeyFrameOverviewCanvas>(ItemsProperty, TileWidthProperty);
        AffectsRender<KeyFrameOverviewCanvas>(ItemsProperty, TileWidthProperty, VerticalOffsetProperty, ViewportHeightProperty);
    }

    public KeyFrameOverviewCanvas()
    {
        PointerPressed += OnPointerPressed;
    }

    public IReadOnlyList<KeyFrameOverviewTile>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public double TileWidth
    {
        get => GetValue(TileWidthProperty);
        set => SetValue(TileWidthProperty, value);
    }

    public double VerticalOffset
    {
        get => GetValue(VerticalOffsetProperty);
        set => SetValue(VerticalOffsetProperty, value);
    }

    public double ViewportHeight
    {
        get => GetValue(ViewportHeightProperty);
        set => SetValue(ViewportHeightProperty, value);
    }

    public event Action<KeyFrameOverviewTile>? TileActivated;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsProperty)
        {
            if (subscribedItems is not null)
            {
                foreach (var item in subscribedItems)
                    item.PropertyChanged -= OnTilePropertyChanged;
            }

            subscribedItems = change.NewValue is IReadOnlyList<KeyFrameOverviewTile> items ? items : null;
            if (subscribedItems is not null)
            {
                foreach (var item in subscribedItems)
                    item.PropertyChanged += OnTilePropertyChanged;
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) && availableSize.Width > 0
            ? availableSize.Width
            : 900;
        var columns = GetColumnCount(width);
        var count = Items?.Count ?? 0;
        var rows = count == 0 ? 0 : (count + columns - 1) / columns;
        var rowHeight = TileWidth * ImageAspect + ImageLabelHeight;
        return new Size(width, Math.Max(0, rows * rowHeight + Math.Max(0, rows - 1) * VerticalGap));
    }

    public (int Start, int End) GetVisibleRange()
    {
        var count = Items?.Count ?? 0;
        if (count == 0 || Bounds.Width <= 0 || Bounds.Height <= 0)
            return (0, 0);

        var columns = GetColumnCount(Bounds.Width);
        var rowHeight = TileWidth * ImageAspect + ImageLabelHeight + VerticalGap;
        var firstRow = Math.Max(0, (int)Math.Floor(VerticalOffset / rowHeight) - 1);
        var viewportHeight = ViewportHeight > 0 ? ViewportHeight : Math.Min(Bounds.Height, 640);
        var lastRow = Math.Max(firstRow, (int)Math.Ceiling((VerticalOffset + viewportHeight) / rowHeight) + 1);
        return (
            Math.Min(count, firstRow * columns),
            Math.Min(count, (lastRow + 1) * columns));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.Transparent, bounds);

        var items = Items;
        if (items is null || items.Count == 0 || Bounds.Width <= 0)
            return;

        var columns = GetColumnCount(Bounds.Width);
        var imageHeight = TileWidth * ImageAspect;
        var rowHeight = imageHeight + ImageLabelHeight + VerticalGap;
        var horizontalOffset = GetHorizontalOffset(columns);
        var (start, end) = GetVisibleRange();
        for (var index = start; index < end; index++)
        {
            var item = items[index];
            var column = index % columns;
            var row = index / columns;
            var x = horizontalOffset + column * (TileWidth + HorizontalGap);
            // ScrollViewer already translates the child by VerticalOffset.
            // Applying it here as well makes tiles disappear after scrolling.
            var y = row * rowHeight;
            var imageRect = new Rect(x, y, TileWidth, imageHeight);
            var tileRect = new Rect(x, y, TileWidth, imageHeight + ImageLabelHeight);

            context.FillRectangle(new SolidColorBrush(Color.FromRgb(35, 37, 41)), tileRect);
            if (item.Thumbnail is { } thumbnail)
            {
                context.DrawImage(thumbnail, new Rect(0, 0, thumbnail.PixelSize.Width, thumbnail.PixelSize.Height), imageRect);
            }
            else
            {
                context.FillRectangle(new SolidColorBrush(Color.FromRgb(55, 58, 63)), imageRect);
                var placeholder = item.IsLoading ? "..." : item.ErrorText is null ? "" : "!";
                if (placeholder.Length > 0)
                    DrawCenteredText(context, placeholder, imageRect, 22, Brushes.White);
            }

            DrawText(context, $"#{item.Index + 1}  {item.TimestampText}",
                new Point(x + 7, y + imageHeight + 10), 12, Brushes.White);
        }
    }

    private void OnTilePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => InvalidateVisual();

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Items is null || e.ClickCount < 2)
            return;

        var point = e.GetPosition(this);
        var columns = GetColumnCount(Bounds.Width);
        var rowHeight = TileWidth * ImageAspect + ImageLabelHeight + VerticalGap;
        var horizontalOffset = GetHorizontalOffset(columns);
        var column = (int)Math.Floor((point.X - horizontalOffset) / (TileWidth + HorizontalGap));
        var row = (int)Math.Floor(point.Y / rowHeight);
        var index = row * columns + column;
        if (column < 0 || column >= columns || index < 0 || index >= Items.Count)
            return;

        var tileX = point.X - horizontalOffset - column * (TileWidth + HorizontalGap);
        var tileY = point.Y - row * rowHeight;
        if (tileX > TileWidth || tileY > ImageAspect * TileWidth + ImageLabelHeight)
            return;

        TileActivated?.Invoke(Items[index]);
        e.Handled = true;
    }

    private int GetColumnCount(double width) => Math.Max(1, (int)Math.Floor((width + HorizontalGap) / (TileWidth + HorizontalGap)));

    private double GetHorizontalOffset(int columns)
    {
        var contentWidth = columns * TileWidth + Math.Max(0, columns - 1) * HorizontalGap;
        return Math.Max(0, (Bounds.Width - contentWidth) / 2);
    }

    private static void DrawText(DrawingContext context, string text, Point origin, double size, IBrush brush)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, size, brush);
        context.DrawText(formatted, origin);
    }

    private static void DrawCenteredText(DrawingContext context, string text, Rect rect, double size, IBrush brush)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, size, brush);
        context.DrawText(formatted, new Point(
            rect.X + (rect.Width - formatted.Width) / 2,
            rect.Y + (rect.Height - formatted.Height) / 2));
    }
}
