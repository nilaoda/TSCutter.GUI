using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using TSCutter.GUI.Utils.Application;

namespace TSCutter.GUI.Models.Preview;

public sealed class PreviewSubtitleTrackOption(PreviewStreamSelection? selection, string label) : ObservableObject
{
    public PreviewStreamSelection? Selection { get; } = selection;
    public string DisplayText => Selection is null ? LocalizationManager.Instance.String_SubtitlePreview_Off : label;
    public override string ToString() => DisplayText;
    public void RefreshLocalizedText() => OnPropertyChanged(nameof(DisplayText));
}

public sealed record SubtitleOverlayPart(Bitmap Bitmap, int X, int Y);

public sealed record SubtitleBackdrop(Rect Bounds, double CornerRadius);

// 播放时未变化的字幕可共享位图；界面和缓存各持一份引用，避免逐帧重建或提前释放。
public sealed class SubtitleOverlay : IDisposable
{
    private sealed class SharedParts(IReadOnlyList<SubtitleOverlayPart> parts)
    {
        public readonly IReadOnlyList<SubtitleOverlayPart> Parts = parts;
        public int References = 1;
    }
    private readonly SharedParts shared;
    private int disposed;
    public PixelSize CanvasSize { get; }
    public SubtitleBackdrop? Backdrop { get; }
    public IReadOnlyList<SubtitleOverlayPart> Parts => shared.Parts;

    public SubtitleOverlay(PixelSize canvasSize, IReadOnlyList<SubtitleOverlayPart> parts, SubtitleBackdrop? backdrop = null)
    {
        CanvasSize = canvasSize;
        Backdrop = backdrop;
        shared = new(parts);
    }

    private SubtitleOverlay(SubtitleOverlay source)
    {
        CanvasSize = source.CanvasSize;
        Backdrop = source.Backdrop;
        shared = source.shared;
        System.Threading.Interlocked.Increment(ref shared.References);
    }

    internal SubtitleOverlay Retain()
    {
        ObjectDisposedException.ThrowIf(System.Threading.Volatile.Read(ref disposed) != 0, this);
        return new(this);
    }

    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        if (System.Threading.Interlocked.Decrement(ref shared.References) == 0)
            foreach (var part in shared.Parts)
                part.Bitmap.Dispose();
    }
}
