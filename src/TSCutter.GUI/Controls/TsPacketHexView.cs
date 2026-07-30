using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TSCutter.GUI.Models;

namespace TSCutter.GUI.Controls;

public sealed class TsPacketHexView : Control
{
    private const int BytesPerRow = 16;
    private const double RowHeight = 19;
    private const double OffsetWidth = 48;
    private const double ByteWidth = 27;
    private static readonly string[] PreferredMonospaceFonts =
        ["Cascadia Mono", "Consolas", "Menlo", "Monaco", "DejaVu Sans Mono", "Liberation Mono", "Noto Sans Mono"];
    private static Typeface? _monospaceTypeface;

    public static readonly StyledProperty<byte[]?> PacketProperty =
        AvaloniaProperty.Register<TsPacketHexView, byte[]?>(nameof(Packet));

    public static readonly StyledProperty<TsPacketFieldItem?> SelectedFieldProperty =
        AvaloniaProperty.Register<TsPacketHexView, TsPacketFieldItem?>(nameof(SelectedField));

    public static readonly StyledProperty<IBrush?> BackgroundBrushProperty =
        AvaloniaProperty.Register<TsPacketHexView, IBrush?>(nameof(BackgroundBrush));

    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<TsPacketHexView, IBrush?>(nameof(TextBrush));

    public static readonly StyledProperty<IBrush?> MutedTextBrushProperty =
        AvaloniaProperty.Register<TsPacketHexView, IBrush?>(nameof(MutedTextBrush));

    public static readonly StyledProperty<IBrush?> SelectionBrushProperty =
        AvaloniaProperty.Register<TsPacketHexView, IBrush?>(nameof(SelectionBrush));

    public static readonly StyledProperty<IBrush?> SelectionTextBrushProperty =
        AvaloniaProperty.Register<TsPacketHexView, IBrush?>(nameof(SelectionTextBrush));

    static TsPacketHexView()
    {
        AffectsRender<TsPacketHexView>(
            PacketProperty, SelectedFieldProperty, BackgroundBrushProperty, TextBrushProperty,
            MutedTextBrushProperty, SelectionBrushProperty, SelectionTextBrushProperty);
    }

    public byte[]? Packet { get => GetValue(PacketProperty); set => SetValue(PacketProperty, value); }
    public TsPacketFieldItem? SelectedField { get => GetValue(SelectedFieldProperty); set => SetValue(SelectedFieldProperty, value); }
    public IBrush? BackgroundBrush { get => GetValue(BackgroundBrushProperty); set => SetValue(BackgroundBrushProperty, value); }
    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public IBrush? MutedTextBrush { get => GetValue(MutedTextBrushProperty); set => SetValue(MutedTextBrushProperty, value); }
    public IBrush? SelectionBrush { get => GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }
    public IBrush? SelectionTextBrush { get => GetValue(SelectionTextBrushProperty); set => SetValue(SelectionTextBrushProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        var asciiWidth = CreateText(new string('0', BytesPerRow), Brushes.Transparent).Width;
        var contentWidth = OffsetWidth + BytesPerRow * ByteWidth + 10 + asciiWidth + 8;
        return new Size(contentWidth, 12 * RowHeight + 8);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(BackgroundBrush ?? Brushes.Transparent, new Rect(Bounds.Size));
        if (Packet is not { Length: > 0 } packet)
            return;

        var textBrush = TextBrush ?? Brushes.Black;
        var mutedBrush = MutedTextBrush ?? Brushes.Gray;
        var selectionBrush = SelectionBrush ?? Brushes.DodgerBlue;
        var selectionTextBrush = SelectionTextBrush ?? Brushes.White;
        // 高亮只根据解析器给出的字节区间绘制，不复制或改写当前 188 字节包。
        var selectedStart = SelectedField?.StartByte ?? -1;
        var selectedEnd = selectedStart + (SelectedField?.ByteLength ?? 0);

        Span<char> ascii = stackalloc char[BytesPerRow];
        for (var row = 0; row * BytesPerRow < packet.Length; row++)
        {
            var rowStart = row * BytesPerRow;
            var y = 4 + row * RowHeight;
            context.DrawText(CreateText($"{rowStart:X4}", mutedBrush), new Point(4, y));

            for (var column = 0; column < BytesPerRow; column++)
            {
                var index = rowStart + column;
                if (index >= packet.Length)
                    break;
                var x = OffsetWidth + column * ByteWidth;
                var selected = index >= selectedStart && index < selectedEnd;
                var cellBounds = new Rect(x - 2, y - 1, 24, RowHeight - 1);
                if (selected)
                    context.FillRectangle(selectionBrush, cellBounds);
                var byteText = CreateText($"{packet[index]:X2}", selected ? selectionTextBrush : textBrush);
                context.DrawText(byteText, new Point(
                    cellBounds.X + (cellBounds.Width - byteText.Width) / 2,
                    cellBounds.Y + (cellBounds.Height - byteText.Height) / 2));
            }

            var asciiX = OffsetWidth + BytesPerRow * ByteWidth + 10;
            var asciiLength = Math.Min(BytesPerRow, packet.Length - rowStart);
            for (var column = 0; column < asciiLength; column++)
            {
                var value = packet[rowStart + column];
                ascii[column] = value is >= 32 and <= 126 ? (char)value : '.';
            }
            context.DrawText(CreateText(ascii[..asciiLength].ToString(), mutedBrush), new Point(asciiX, y));
        }
    }

    private static FormattedText CreateText(string text, IBrush brush) => new(
        text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        _monospaceTypeface ??= ResolveMonospaceTypeface(), 11, brush);

    private static Typeface ResolveMonospaceTypeface()
    {
        var fontManager = FontManager.Current;
        foreach (var preferredName in PreferredMonospaceFonts)
        {
            foreach (var family in fontManager.SystemFonts)
            {
                if (string.Equals(family.Name, preferredName, StringComparison.OrdinalIgnoreCase))
                    return new Typeface(family);
            }
        }

        // 已知字体均不存在时，通过字体度量选择当前平台提供的固定字宽字体。
        foreach (var family in fontManager.SystemFonts)
        {
            var typeface = new Typeface(family);
            if (fontManager.TryGetGlyphTypeface(typeface, out var glyphTypeface) &&
                glyphTypeface.Metrics.IsFixedPitch)
                return typeface;
        }
        return new Typeface("monospace");
    }
}
