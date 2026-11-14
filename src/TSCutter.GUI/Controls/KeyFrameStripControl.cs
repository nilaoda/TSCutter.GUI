using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Utils;
using TSCutter.GUI.ViewModels.Preview;

namespace TSCutter.GUI.Controls;

/// <summary>随容器宽度显示关键帧，保持图片比例，并标出指针所在的 GOP。</summary>
public sealed class KeyFrameStripControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<KeyFrameStripItem>?> ItemsProperty =
        AvaloniaProperty.Register<KeyFrameStripControl, IReadOnlyList<KeyFrameStripItem>?>(nameof(Items));
    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<KeyFrameStripControl, IBrush?>(nameof(TextBrush));
    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<KeyFrameStripControl, IBrush?>(nameof(AccentBrush));
    public static readonly StyledProperty<IBrush?> BorderBrushProperty =
        AvaloniaProperty.Register<KeyFrameStripControl, IBrush?>(nameof(BorderBrush));
    public static readonly StyledProperty<IReadOnlyList<KeyFrameRetentionRange>?> RangesProperty =
        AvaloniaProperty.Register<KeyFrameStripControl, IReadOnlyList<KeyFrameRetentionRange>?>(nameof(Ranges));
    public static readonly StyledProperty<IBrush?> ClipBrushProperty =
        AvaloniaProperty.Register<KeyFrameStripControl, IBrush?>(nameof(ClipBrush));
    /// <summary>删除模式下要删除的画面所用的颜色。</summary>
    public static readonly StyledProperty<IBrush?> RemovedBrushProperty =
        AvaloniaProperty.Register<KeyFrameStripControl, IBrush?>(nameof(RemovedBrush));
    /// <summary>滚轮每格移动的格数。</summary>
    public static readonly StyledProperty<int> WheelStepProperty =
        AvaloniaProperty.Register<KeyFrameStripControl, int>(nameof(WheelStep), 1);
    /// <summary>按住 Shift 滚动时每格移动的格数。</summary>
    public static readonly StyledProperty<int> ShiftWheelStepProperty =
        AvaloniaProperty.Register<KeyFrameStripControl, int>(nameof(ShiftWheelStep), 10);
    /// <summary>反转滚轮方向：默认向下滚前进。</summary>
    public static readonly StyledProperty<bool> ReverseWheelProperty =
        AvaloniaProperty.Register<KeyFrameStripControl, bool>(nameof(ReverseWheel));
    /// <summary>缩略图大小：决定条的高度、每格尺寸与可见数量。</summary>
    public static readonly StyledProperty<KeyFrameStripThumbnailSize> ThumbnailSizeProperty =
        AvaloniaProperty.Register<KeyFrameStripControl, KeyFrameStripThumbnailSize>(nameof(ThumbnailSize),
            KeyFrameStripThumbnailSize.Medium);

    /// <summary>缩略图下方的音频波形；为 null 或 <see cref="ShowWaveform"/> 为 false 时不显示波形条。</summary>
    public static readonly StyledProperty<StripWaveformViewModel?> WaveformProperty =
        AvaloniaProperty.Register<KeyFrameStripControl, StripWaveformViewModel?>(nameof(Waveform));
    public static readonly StyledProperty<bool> ShowWaveformProperty =
        AvaloniaProperty.Register<KeyFrameStripControl, bool>(nameof(ShowWaveform));
    /// <summary>波形数据的版本，读到新数据时变化以触发重绘。</summary>
    public static readonly StyledProperty<int> WaveformVersionProperty =
        AvaloniaProperty.Register<KeyFrameStripControl, int>(nameof(WaveformVersion));
    public static readonly StyledProperty<IBrush?> WaveformBrushProperty =
        AvaloniaProperty.Register<KeyFrameStripControl, IBrush?>(nameof(WaveformBrush));

    public IReadOnlyList<KeyFrameStripItem>? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public StripWaveformViewModel? Waveform { get => GetValue(WaveformProperty); set => SetValue(WaveformProperty, value); }
    public bool ShowWaveform { get => GetValue(ShowWaveformProperty); set => SetValue(ShowWaveformProperty, value); }
    public int WaveformVersion { get => GetValue(WaveformVersionProperty); set => SetValue(WaveformVersionProperty, value); }
    public IBrush? WaveformBrush { get => GetValue(WaveformBrushProperty); set => SetValue(WaveformBrushProperty, value); }
    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? BorderBrush { get => GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }
    public IReadOnlyList<KeyFrameRetentionRange>? Ranges { get => GetValue(RangesProperty); set => SetValue(RangesProperty, value); }
    public IBrush? ClipBrush { get => GetValue(ClipBrushProperty); set => SetValue(ClipBrushProperty, value); }
    public IBrush? RemovedBrush { get => GetValue(RemovedBrushProperty); set => SetValue(RemovedBrushProperty, value); }
    public int WheelStep { get => GetValue(WheelStepProperty); set => SetValue(WheelStepProperty, value); }
    public int ShiftWheelStep { get => GetValue(ShiftWheelStepProperty); set => SetValue(ShiftWheelStepProperty, value); }
    public bool ReverseWheel { get => GetValue(ReverseWheelProperty); set => SetValue(ReverseWheelProperty, value); }
    public KeyFrameStripThumbnailSize ThumbnailSize { get => GetValue(ThumbnailSizeProperty); set => SetValue(ThumbnailSizeProperty, value); }
    internal KeyFrameStripLayout Layout { get; private set; } = KeyFrameStripLayout.Medium;
    public event EventHandler<KeyFrameStripItem>? SeekRequested;
    /// <summary>滚轮滚动的格数：正数前进，负数后退；已按每格步长与方向设置换算。</summary>
    public event EventHandler<int>? WheelStepRequested;
    /// <summary>间隔不足该值的滚轮事件忽略（触控板、平滑滚动等连续事件流），与图片预览的滚轮缩放相同。</summary>
    internal const ulong WheelIntervalMs = 16;
    private ulong? lastWheelStep;
    private static readonly IBrush TypeBadgeBrush = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)).ToImmutable();

    // 波形条：缩略图区域不变，加在下方；上留 2px 间距、条高 18px，与节目带一致。
    private const double WaveformGap = 2;
    private const double WaveformLaneHeight = 18;
    internal const double WaveformHeight = WaveformGap + WaveformLaneHeight;
    // 满高参考值的下限（约 -26 dB）：整段很安静时按这个参考画，不放大。
    private const float MinimumWaveformReference = 0.05f;
    // 波形显示的分贝范围：满高以下这么多分贝内的声音按比例画出高低。
    private const double WaveformRangeDb = 40;
    // 曲线每隔这么多像素取一个点，再按左右各 WaveformSmoothing 个点平均：只表现响度起伏，不画逐帧细节。
    private const int WaveformPointSpacing = 3;
    private const int WaveformSmoothing = 2;

    static KeyFrameStripControl() => AffectsRender<KeyFrameStripControl>(ItemsProperty,
        TextBrushProperty, AccentBrushProperty, BorderBrushProperty, RangesProperty, ClipBrushProperty, RemovedBrushProperty,
        WaveformVersionProperty, WaveformBrushProperty);

    public KeyFrameStripControl()
    {
        Height = Layout.Height;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    private bool HasWaveform => ShowWaveform && Waveform is not null;

    internal int VisibleCapacity => Math.Clamp((int)((Bounds.Width - 8) / Layout.Pitch),
        1, KeyFrameStripViewModel.MaximumVisibleCount);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ThumbnailSizeProperty)
            // 可见数量立即按新尺寸计算（宽度不变）；各档高度不同，改变高度即触发重新布局与绘制。
            Layout = KeyFrameStripLayout.For(ThumbnailSize);
        else if (change.Property != ShowWaveformProperty && change.Property != WaveformProperty)
            return;
        Height = Layout.Height + (HasWaveform ? WaveformHeight : 0);
    }

    internal IReadOnlyList<(KeyFrameStripItem Item, Rect Bounds)> GetTiles()
    {
        if (Items is not { Count: > 0 } frames || Bounds.Width < 24)
            return [];
        var count = Math.Min(frames.Count, VisibleCapacity);
        var current = frames.Select((item, index) => (item, index)).FirstOrDefault(pair => pair.item.IsCurrent).index;
        var start = Math.Clamp(current - count / 2, 0, frames.Count - count);
        var width = Math.Min(Layout.MaxTileWidth, (Bounds.Width - 8) / count);
        var left = (Bounds.Width - count * width) / 2;
        var height = Layout.TileHeight;
        return Enumerable.Range(start, count).Select((index, offset) =>
            (frames[index], new Rect(left + offset * width + 2, 3, width - 4, height))).ToArray();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var tiles = GetTiles();
        foreach (var (item, bounds) in tiles)
        {
            var imageArea = new Rect(bounds.X + 3, bounds.Y + 3, bounds.Width - 6, bounds.Height - 24);
            var badgeArea = imageArea;
            if (item.Bitmap is { } bitmap)
            {
                var size = bitmap.Size;
                var scale = Math.Min(imageArea.Width / size.Width, imageArea.Height / size.Height);
                var destination = new Rect(imageArea.Center.X - size.Width * scale / 2,
                    imageArea.Center.Y - size.Height * scale / 2, size.Width * scale, size.Height * scale);
                context.DrawImage(bitmap, new Rect(size), destination);
                badgeArea = destination;
            }
            if (!item.IsPlaceholder && item.KeyFrameType is { Length: > 0 } frameType)
            {
                var label = new FormattedText(frameType, CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Inter"), 9, Brushes.White);
                var badge = new Rect(badgeArea.Right - label.Width - 8, badgeArea.Top,
                    label.Width + 8, label.Height + 2);
                if (badge.Left >= badgeArea.Left && badge.Bottom <= badgeArea.Bottom)
                {
                    context.DrawRectangle(TypeBadgeBrush, null, badge, 2, 2);
                    context.DrawText(label, new Point(badge.X + 4, badge.Y + 1));
                }
            }
            var retentionBrush = GetRetentionBrush(item, out var retained);
            if (retained && retentionBrush is not null)
            {
                using var opacity = context.PushOpacity(0.16);
                context.FillRectangle(retentionBrush,
                    new Rect(bounds.X - 2, imageArea.Bottom + 2, bounds.Width + 4,
                        Layout.Height - imageArea.Bottom - 2));
            }
            using (context.PushOpacity(item.IsCurrent ? 1 : 0.28))
                context.DrawRectangle(null, new Pen(item.IsCurrent ? AccentBrush ?? Brushes.DodgerBlue
                    : BorderBrush ?? Brushes.Gray, item.IsCurrent ? 2 : 1), bounds);
            var text = new FormattedText(item.IsPlaceholder ? "…" : CommonUtil.FormatSeconds(item.Timestamp.TotalSeconds),
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Inter"), 11,
                TextBrush ?? Brushes.Gray);
            context.DrawText(text, new Point(bounds.Center.X - text.Width / 2, bounds.Bottom - 19));
        }
        foreach (var (bounds, isStart, brush) in GetBoundaryMarkers(tiles))
        {
            var pen = new Pen(brush, 2);
            var x = isStart ? bounds.Left : bounds.Right;
            context.DrawLine(pen, new Point(x, bounds.Top), new Point(x, bounds.Bottom));
            context.DrawLine(pen, bounds.TopLeft, bounds.TopRight);
            context.DrawLine(pen, bounds.BottomLeft, bounds.BottomRight);
        }
        if (HasWaveform)
            RenderWaveform(context, tiles);
    }

    /// <summary>每张缩略图下方的时间段，与波形读取的范围一致。占位格没有时间段。</summary>
    private List<(Rect Lane, double Start, double End)> GetWaveformSpans(
        IReadOnlyList<(KeyFrameStripItem Item, Rect Bounds)> tiles)
    {
        if (tiles.Count == 0 || Items is not { } frames)
            return [];
        var first = 0;
        while (first < frames.Count && !ReferenceEquals(frames[first], tiles[0].Item))
            first++;
        var top = Layout.Height + WaveformGap;
        var spans = new List<(Rect Lane, double Start, double End)>(tiles.Count);
        for (var offset = 0; offset < tiles.Count; offset++)
        {
            var index = first + offset;
            var bounds = tiles[offset].Bounds;
            if (index >= frames.Count || StripWaveformViewModel.GetSpan(frames, index) is not { } span)
                continue;
            // 波形占满整格（含两侧各 2px 的格间缝隙），相邻两段首尾相接，整条连续显示。
            spans.Add((new Rect(bounds.X - 2, top, bounds.Width + 4, WaveformLaneHeight), span.Start, span.End));
        }
        return spans;
    }

    private void RenderWaveform(DrawingContext context, IReadOnlyList<(KeyFrameStripItem Item, Rect Bounds)> tiles)
    {
        var waveform = Waveform!;
        var spans = GetWaveformSpans(tiles);
        if (spans.Count == 0)
            return;
        // 每隔几像素取一段时间内的峰值；还没读到的时段为 NaN，曲线在那里断开。
        var points = new List<(double X, float Peak)>();
        var loudest = MinimumWaveformReference;
        foreach (var (lane, start, end) in spans)
        {
            var count = Math.Max(1, (int)Math.Round(lane.Width / WaveformPointSpacing));
            var width = lane.Width / count;
            var step = (end - start) / count;
            for (var index = 0; index < count; index++)
            {
                var time = start + index * step;
                var peak = waveform.TryGetPeak(time, time + step, out var value) ? value : float.NaN;
                if (!float.IsNaN(peak))
                    loudest = Math.Max(loudest, peak);
                points.Add((lane.Left + (index + 0.5) * width, peak));
            }
        }
        // 以可见范围内的最大峰值为满高、按分贝换算：广播音量普遍偏低，按绝对值画几乎是一条线；
        // 按线性比例画时，响亮的字句旁边的小声段（轻声、环境声）又会显得像断了一样。
        // 参考值有下限，整段都很安静时不会被放大到满高；比最响处低 WaveformRangeDb 以上的声音落在底线。
        var levels = new double[points.Count];
        for (var index = 0; index < points.Count; index++)
        {
            var peak = points[index].Peak;
            levels[index] = float.IsNaN(peak) ? double.NaN
                : peak <= 0 ? 0 : Math.Clamp(1 + 20 * Math.Log10(peak / loudest) / WaveformRangeDb, 0, 1);
        }
        var bottom = spans[0].Lane.Bottom - 0.5;
        var height = WaveformLaneHeight - 1;
        var area = new StreamGeometry();
        var line = new StreamGeometry();
        using (var areaPath = area.Open())
        using (var linePath = line.Open())
        {
            var runStart = -1;
            for (var index = 0; index <= levels.Length; index++)
            {
                if (index < levels.Length && !double.IsNaN(levels[index]))
                {
                    if (runStart < 0)
                        runStart = index;
                    continue;
                }
                if (runStart >= 0)
                    AddWaveformRun(areaPath, linePath, points, levels, runStart, index, bottom, height);
                runStart = -1;
            }
        }
        var brush = WaveformBrush ?? TextBrush ?? Brushes.Gray;
        using (context.PushOpacity(0.3))
            context.DrawGeometry(brush, null, area);
        context.DrawGeometry(null, new Pen(brush, 1), line);
    }

    // 连续读到的一段：按左右邻点平均后连成折线，下方填充到底线。
    private static void AddWaveformRun(StreamGeometryContext areaPath, StreamGeometryContext linePath,
        List<(double X, float Peak)> points, double[] levels, int start, int end, double bottom, double height)
    {
        if (end - start < 2)
            return;
        areaPath.BeginFigure(new Point(points[start].X, bottom), true);
        for (var index = start; index < end; index++)
        {
            var from = Math.Max(start, index - WaveformSmoothing);
            var to = Math.Min(end - 1, index + WaveformSmoothing);
            var sum = 0.0;
            for (var neighbor = from; neighbor <= to; neighbor++)
                sum += levels[neighbor];
            var point = new Point(points[index].X, bottom - sum / (to - from + 1) * height);
            areaPath.LineTo(point);
            if (index == start)
                linePath.BeginFigure(point, false);
            else
                linePath.LineTo(point);
        }
        areaPath.LineTo(new Point(points[end - 1].X, bottom));
        areaPath.EndFigure(true);
        linePath.EndFigure(false);
    }

    internal IReadOnlyList<(Rect Bounds, bool IsStart, IBrush Brush)> GetBoundaryMarkers(
        IReadOnlyList<(KeyFrameStripItem Item, Rect Bounds)> tiles)
    {
        var markers = new List<(Rect Bounds, bool IsStart, IBrush Brush)>();
        foreach (var range in (Ranges ?? []).OrderBy(range => range.IsActive))
        {
            if (range.End < range.Start)
                continue;
            var brush = RangeBrush(range);
            foreach (var (item, bounds) in tiles)
            {
                if (MatchesBoundary(item, range.StartPts, range.Start))
                    markers.Add((new Rect(bounds.Left + 4, bounds.Bottom - 18, 4, 15), true, brush));
                if (!MatchesBoundary(item, range.EndPts, range.End))
                    continue;
                // 删除范围的终点是恢复播放的画面，本身保留：收口画在它左侧与前一帧之间 4px 的缝隙里。
                var x = range.Removed ? bounds.Left - 4 : bounds.Right - 8;
                markers.Add((new Rect(x, bounds.Bottom - 18, 4, 15), false, brush));
            }
        }
        return markers;
    }

    private static bool MatchesBoundary(KeyFrameStripItem item, long? pts, double time) =>
        !item.IsPlaceholder && (pts is { } exact ? item.Pts == exact : item.Timestamp.TotalSeconds == time);

    internal IBrush? GetRetentionBrush(KeyFrameStripItem item) => GetRetentionBrush(item, out _);

    private IBrush? GetRetentionBrush(KeyFrameStripItem item, out bool retained)
    {
        retained = false;
        if (item.IsPlaceholder)
            return null;
        KeyFrameRetentionRange? matching = null;
        foreach (var range in Ranges ?? [])
        {
            if (range.Contains(item.Pts, item.Timestamp.TotalSeconds) &&
                (matching is null || range.IsActive || !matching.Value.IsActive))
                matching = range;
        }
        if (matching is not { } clip)
            return BorderBrush ?? Brushes.Gray;
        retained = true;
        return RangeBrush(clip);
    }

    // 删除范围统一用删除颜色，不随片段标注颜色变化，避免与保留片段混淆。
    private IBrush RangeBrush(KeyFrameRetentionRange range) => range.Removed ? RemovedBrush ?? Brushes.Red
        : string.IsNullOrEmpty(range.Color) ? ClipBrush ?? Brushes.Green : ClipLabels.Brush(range.Color);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEffectivelyEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        foreach (var (item, bounds) in GetTiles())
        {
            if (item.IsPlaceholder || !bounds.Contains(e.GetPosition(this)))
                continue;
            SeekRequested?.Invoke(this, item);
            e.Handled = true;
            break;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        e.Handled |= ApplyWheel(e.Delta, e.KeyModifiers, e.Timestamp);
    }

    /// <summary>
    /// 处理一次滚轮滚动：与图片预览的滚轮缩放相同，每个事件按方向移动一次，不按增量大小换算——
    /// macOS 普通滚轮每格的增量随滚动加速变化且常小于 1（Avalonia 换算为 scrollingDeltaY / 5），
    /// 按增量累计会出现有时滚两格才移动一次。每次移动 <see cref="WheelStep"/> 格，按住 Shift 时移动
    /// <see cref="ShiftWheelStep"/> 格。距上一次移动不足 <see cref="WheelIntervalMs"/> 毫秒的事件忽略。
    /// 解码期间主窗口的遮罩拦截输入，落在导航条上的滚轮由窗口转交到这里；预览画面与时间轴设为滚轮定位时
    /// 也转交到这里，共用步长与间隔。返回是否处理了这次滚动。
    /// </summary>
    internal bool ApplyWheel(Vector delta, KeyModifiers modifiers, ulong timestamp)
    {
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        // macOS 按住 Shift 滚动时系统把纵向滚动换成横向，增量落在 X 上。
        var amount = delta.Y != 0 ? delta.Y : shift ? delta.X : 0;
        if (!IsEffectivelyEnabled || amount == 0)
            return false;
        if (lastWheelStep is { } last && timestamp >= last && timestamp - last < WheelIntervalMs)
            return true;
        lastWheelStep = timestamp;
        // 向上滚 Delta.Y 为正（后退），向下滚为负（前进）。
        var direction = amount < 0 ? 1 : -1;
        if (ReverseWheel)
            direction = -direction;
        var step = Math.Max(1, shift ? ShiftWheelStep : WheelStep);
        WheelStepRequested?.Invoke(this, direction * step);
        return true;
    }
}
