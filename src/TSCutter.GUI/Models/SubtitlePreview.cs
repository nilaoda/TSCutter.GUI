using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.Models;

public sealed class PreviewSubtitleTrackOption(PreviewStreamSelection? selection, string label) : ObservableObject
{
    public PreviewStreamSelection? Selection { get; } = selection;
    public string DisplayText => Selection is null ? LocalizationManager.Instance.String_SubtitlePreview_Off : label;
    public override string ToString() => DisplayText;
    public void RefreshLocalizedText() => OnPropertyChanged(nameof(DisplayText));
}

public sealed record SubtitleOverlayPart(Bitmap Bitmap, int X, int Y);

public sealed record SubtitleBackdrop(Rect Bounds, double CornerRadius);

public sealed class SubtitleOverlay(PixelSize canvasSize, IReadOnlyList<SubtitleOverlayPart> parts,
    SubtitleBackdrop? backdrop = null) : IDisposable
{
    public PixelSize CanvasSize { get; } = canvasSize;
    public SubtitleBackdrop? Backdrop { get; } = backdrop;
    public IReadOnlyList<SubtitleOverlayPart> Parts { get; } = parts;
    public void Dispose()
    {
        foreach (var part in Parts) part.Bitmap.Dispose();
    }
}
