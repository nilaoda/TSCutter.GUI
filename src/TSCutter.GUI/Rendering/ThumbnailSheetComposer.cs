using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using TSCutter.GUI.Models;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.Rendering;

/// <summary>
/// 把一组缩略图合成为一张"缩略图总览"成图：顶部信息带 + N×M 网格。
///
/// 设计要点：
/// - 整张图一次性绘制到单个 <see cref="RenderTargetBitmap"/>，因此布局必须先算准尺寸，
///   再逐格绘制，避免逐格编码后二次拼接。
/// - 成图尺寸可能很大（例如 6×8 格、单图 640px 宽），必须先经过
///   <see cref="ThumbnailSheetLayout"/> 校验，防止超出后端纹理上限。
/// - 所有文本都走 <see cref="FormattedText"/> + 显式解析的字族，保证中文文件名可渲染。
/// </summary>
public static class ThumbnailSheetComposer
{
    private const double Padding = 16;

    /// <summary>格子之间的水平间距。</summary>
    private const double CellGapX = 8;

    /// <summary>格子之间的垂直间距（不含时间码）。</summary>
    private const double CellGapY = 8;

    private const double BaseCaptionHeight = 18;

    private const double BaseCaptionFontSize = 11.5;

    private const double CaptionReferenceCellWidth = 320;

    private const double HeaderTitleLineHeight = 26;

    private const double HeaderInfoLineHeight = 20;

    private const double HeaderBottomGap = 12;

    /// <summary>单张成图的像素总数上限。64MP 的 BGRA 缓冲约占 256MiB。</summary>
    public const long MaximumPixelCount = 64L * 1024L * 1024L;

    /// <summary>单边尺寸上限，与主流后端的最大纹理尺寸对齐。</summary>
    public const int MaximumDimension = 16_384;

    public static int CalculateCellWidthForOutputWidth(int columns, int outputWidth)
    {
        columns = Math.Max(1, columns);
        var fixedWidth = (int)Math.Ceiling(Padding * 2 + (columns - 1) * CellGapX);
        return Math.Max(1, (outputWidth - fixedWidth) / columns);
    }

    public static int CalculateOutputWidth(int columns, int cellWidth)
    {
        columns = Math.Max(1, columns);
        cellWidth = Math.Max(1, cellWidth);
        return (int)Math.Ceiling(Padding * 2 + columns * cellWidth + (columns - 1) * CellGapX);
    }

    /// <summary>
    /// 计算成图的整体布局尺寸。
    /// </summary>
    public static ThumbnailSheetLayout CalculateLayout(
        int columns,
        int rows,
        PixelSize cellSize,
        bool showHeader,
        bool showCaption,
        int headerLineCount = 2)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);

        var cellWidth = Math.Max(1, cellSize.Width);
        var cellHeight = Math.Max(1, cellSize.Height);
        if (showCaption)
            cellHeight += CalculateCaptionHeight(cellWidth);

        var gridWidth = columns * cellWidth + (columns - 1) * CellGapX;
        var gridHeight = rows * cellHeight + (rows - 1) * CellGapY;

        var width = (int)Math.Ceiling(gridWidth + Padding * 2);
        var headerHeight = showHeader
            ? CalculateHeaderHeight(width, headerLineCount)
            : 0;
        var height = (int)Math.Ceiling(gridHeight + headerHeight + Padding * 2);

        return new ThumbnailSheetLayout(width, height, headerHeight, cellWidth, cellHeight, columns, rows);
    }

    /// <summary>
    /// 在给定网格与显示开关下，计算能让成图落在后端上限内的最大单元格宽度。
    /// 用于超限时给用户一个可操作的建议值，而不是静默篡改其参数。
    /// 返回 0 表示即使最小宽度也无法满足（网格本身过大）。
    /// </summary>
    public static int CalculateMaximumCellWidth(
        int columns,
        int rows,
        double aspectRatio,
        bool showHeader,
        bool showCaption,
        int headerLineCount = 2)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);

        for (var width = MaximumCellWidthLimit; width >= 16; width -= 8)
        {
            var height = Math.Max(9, (int)Math.Round(width * aspectRatio));
            var layout = CalculateLayout(
                columns,
                rows,
                new PixelSize(width, height),
                showHeader,
                showCaption,
                headerLineCount);
            if (layout.Width <= MaximumDimension
                && layout.Height <= MaximumDimension
                && (long)layout.Width * layout.Height <= MaximumPixelCount)
                return width;
        }

        return 0;
    }

    private const int MaximumCellWidthLimit = 1920;

    /// <summary>
    /// 生成整张总览图。<paramref name="cells"/> 按行优先顺序排列，
    /// 缺失或未就绪的格子画占位底。<paramref name="headerLines"/> 已由调用方
    /// 按当前语言拼好（第一行文件名，后续各行为分类后的技术信息）。
    /// </summary>
    public static Bitmap Compose(
        ThumbnailSheetLayout layout,
        IReadOnlyList<ThumbnailSheetCell> cells,
        IReadOnlyList<ThumbnailSheetHeaderLine> headerLines,
        bool showCaption,
        bool showIndex)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var width = layout.Width;
        var height = layout.Height;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("Thumbnail sheet size is invalid.");
        if ((long)width * height > MaximumPixelCount)
            throw new InvalidOperationException("Thumbnail sheet is too large to compose.");

        var target = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        try
        {
            using (var context = target.CreateDrawingContext(true))
            {
                DrawBackground(context, width, height);

                if (layout.HeaderHeight > 0)
                    DrawHeader(context, layout, headerLines);

                DrawGrid(context, layout, cells, showCaption, showIndex);
            }

            return target;
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    private static void DrawBackground(DrawingContext context, int width, int height)
    {
        context.FillRectangle(SheetBrushes.Background, new Rect(0, 0, width, height));
    }

    private static void DrawHeader(
        DrawingContext context,
        ThumbnailSheetLayout layout,
        IReadOnlyList<ThumbnailSheetHeaderLine> headerLines)
    {
        var left = Padding;
        var top = Padding;

        var scale = CalculateHeaderScale(layout.Width);
        var titleSize = 15 * scale;
        var infoSize = 12.5 * scale;

        // 第一行：文件名，字号略大并使用高对比色。
        if (headerLines.Count > 0)
        {
            DrawText(
                context,
                headerLines[0].Text,
                new Point(left, top),
                titleSize,
                SheetBrushes.HeaderPrimary,
                SheetFonts.Header,
                maxWidth: layout.Width - Padding * 2,
                accentStart: headerLines[0].AccentStart,
                accentLength: headerLines[0].AccentLength);
            top += HeaderTitleLineHeight * scale;
        }

        // 后续行分别承载基本、视频、音频和其他流信息。
        for (var index = 1; index < headerLines.Count; index++)
        {
            DrawText(
                context,
                headerLines[index].Text,
                new Point(left, top),
                infoSize,
                SheetBrushes.HeaderSecondary,
                SheetFonts.Mono,
                maxWidth: layout.Width - Padding * 2,
                accentStart: headerLines[index].AccentStart,
                accentLength: headerLines[index].AccentLength);
            top += HeaderInfoLineHeight * scale;
        }
    }

    private static double CalculateHeaderHeight(int width, int headerLineCount)
    {
        var lineCount = Math.Max(1, headerLineCount);
        var scale = CalculateHeaderScale(width);
        return Math.Ceiling(
            (HeaderTitleLineHeight + (lineCount - 1) * HeaderInfoLineHeight) * scale
            + HeaderBottomGap);
    }

    private static double CalculateHeaderScale(int width) =>
        Math.Clamp(width / 1200d, 1d, 2.5d);

    internal static double CalculateCaptionScale(int cellWidth) =>
        Math.Clamp(cellWidth / CaptionReferenceCellWidth, 1d, 4d);

    internal static int CalculateCaptionHeight(int cellWidth) =>
        (int)Math.Ceiling(BaseCaptionHeight * CalculateCaptionScale(cellWidth));

    internal static double CalculateCaptionFontSize(int cellWidth) =>
        BaseCaptionFontSize * CalculateCaptionScale(cellWidth);

    private static void DrawGrid(
        DrawingContext context,
        ThumbnailSheetLayout layout,
        IReadOnlyList<ThumbnailSheetCell> cells,
        bool showCaption,
        bool showIndex)
    {
        var originX = Padding;
        var originY = Padding + layout.HeaderHeight;

        for (var row = 0; row < layout.Rows; row++)
        {
            for (var column = 0; column < layout.Columns; column++)
            {
                var index = row * layout.Columns + column;
                if (index >= cells.Count)
                    return;

                var cell = cells[index];
                var x = originX + column * (layout.CellWidth + CellGapX);
                var y = originY + row * (layout.CellHeight + CellGapY);

                var imageRect = new Rect(x, y, layout.CellWidth, layout.CellHeight);
                if (showCaption)
                    imageRect = imageRect.WithHeight(
                        imageRect.Height - CalculateCaptionHeight(layout.CellWidth));

                var thumbnail = cell.Thumbnail;
                if (thumbnail is not null)
                {
                    // 位图在抽帧时已按目标尺寸 letterbox 好，这里直接铺满。
                    context.DrawImage(thumbnail, new Rect(thumbnail.Size), imageRect);
                }
                else
                {
                    context.FillRectangle(SheetBrushes.CellPlaceholder, imageRect);
                }

                if (!showCaption)
                    continue;

                var captionTop = imageRect.Bottom;
                var captionText = BuildCaption(cell, showIndex);
                if (!string.IsNullOrEmpty(captionText))
                {
                    DrawText(
                        context,
                        captionText,
                        new Point(x, captionTop),
                        CalculateCaptionFontSize(layout.CellWidth),
                        SheetBrushes.Caption,
                        SheetFonts.Mono,
                        maxWidth: layout.CellWidth);
                }
            }
        }
    }

    private static string BuildCaption(ThumbnailSheetCell cell, bool showIndex)
    {
        var time = string.IsNullOrEmpty(cell.TimestampText)
            ? CommonUtil.FormatSeconds(cell.Timestamp.TotalSeconds, true)
            : cell.TimestampText;

        return showIndex
            ? string.Format(CultureInfo.CurrentCulture, "{0}. {1}", cell.Index + 1, time)
            : time;
    }

    private static void DrawText(
        DrawingContext context,
        string text,
        Point origin,
        double fontSize,
        IBrush brush,
        Typeface typeface,
        double maxWidth,
        int accentStart = -1,
        int accentLength = 0)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0)
            return;

        // 位图渲染不做亚像素抗锯齿，坐标必须落在整像素上，否则文字边缘发虚。
        var alignedOrigin = new Point(Math.Round(origin.X), Math.Round(origin.Y));
        var alignedFontSize = Math.Round(fontSize);

        var displayText = text;
        var formatted = new FormattedText(
            displayText,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            alignedFontSize,
            brush);

        if (formatted.Width > maxWidth)
        {
            displayText = Ellipsize(text, maxWidth, typeface, alignedFontSize, brush);
            if (displayText.Length == 0)
                return;

            formatted = new FormattedText(
                displayText,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                alignedFontSize,
                brush);
        }

        if (accentStart >= 0
            && accentLength > 0
            && accentStart + accentLength <= displayText.Length)
        {
            formatted.SetForegroundBrush(SheetBrushes.HeaderAccent, accentStart, accentLength);
        }

        context.DrawText(formatted, alignedOrigin);
    }

    /// <summary>
    /// 逐字符收缩文本直到宽度落入 maxWidth。
    /// 对于中文等宽字符为主的文本，这样比按字符数估算更可靠。
    /// </summary>
    private static string Ellipsize(
        string text,
        double maxWidth,
        Typeface typeface,
        double fontSize,
        IBrush brush)
    {
        const string ellipsis = "\u2026";

        for (var length = text.Length - 1; length > 0; length--)
        {
            var candidate = text[..length] + ellipsis;
            var measure = new FormattedText(
                candidate,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                brush);

            if (measure.Width <= maxWidth)
                return candidate;
        }

        return ellipsis;
    }
}

/// <summary>
/// 缩略图总览成图的尺寸布局结果。
/// </summary>
public sealed record ThumbnailSheetLayout(
    int Width,
    int Height,
    double HeaderHeight,
    int CellWidth,
    int CellHeight,
    int Columns,
    int Rows);

internal static class SheetBrushes
{
    public static readonly IBrush Background = new SolidColorBrush(Color.FromRgb(24, 25, 28));
    public static readonly IBrush CellPlaceholder = new SolidColorBrush(Color.FromRgb(46, 48, 53));
    public static readonly IBrush HeaderPrimary = new SolidColorBrush(Color.FromRgb(240, 241, 244));
    public static readonly IBrush HeaderSecondary = new SolidColorBrush(Color.FromRgb(168, 172, 180));
    public static readonly IBrush HeaderAccent = new SolidColorBrush(Color.FromRgb(245, 158, 66));
    public static readonly IBrush Caption = new SolidColorBrush(Color.FromRgb(178, 182, 190));
}

/// <summary>
/// 总览图使用到的字族。等宽字族用于表头第二行与时间码，
/// 解析策略与 <see cref="Controls.TsPacketHexView"/> 保持一致（含平台兜底）。
/// </summary>
internal static class SheetFonts
{
    private static readonly string[] PreferredMonospaceFonts =
    [
        "Consolas", "Menlo", "Courier New", "DejaVu Sans Mono", "Noto Sans Mono CJK SC"
    ];

    private static Typeface? _monospace;
    private static Typeface? _header;

    /// <summary>表头正文使用系统默认字族，保证中文文件名可渲染。</summary>
    public static Typeface Header => _header ??= Typeface.Default;

    public static Typeface Mono => _monospace ??= ResolveMonospaceTypeface();

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
