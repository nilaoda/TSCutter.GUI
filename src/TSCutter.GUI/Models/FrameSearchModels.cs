using System;
using System.Collections.Generic;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TSCutter.GUI.Models;

public sealed class SavedFrameLibrary
{
    public int Version { get; set; } = 1;
    public List<SavedFrameTemplate> Templates { get; set; } = [];
}

public sealed class SavedFrameTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public byte[] Jpeg { get; set; } = [];
}

public sealed class SavedFrameTile(SavedFrameTemplate template) : ObservableObject, IDisposable
{
    public SavedFrameTemplate Template { get; } = template;
    public Bitmap Thumbnail { get; } = CreateBitmap(template.Jpeg);
    public string Name => Template.Name;
    public string CreatedText => Template.CreatedUtc.ToLocalTime().ToString("g");
    public void RefreshName() => OnPropertyChanged(nameof(Name));
    public void Dispose() => Thumbnail.Dispose();

    private static Bitmap CreateBitmap(byte[] jpeg)
    {
        using var stream = new System.IO.MemoryStream(jpeg, writable: false);
        return new Bitmap(stream);
    }
}

public sealed class FrameSearchPreviewFrame(TimeSpan time, Bitmap bitmap, string label = "") : IDisposable
{
    public TimeSpan Time { get; } = time;
    public Bitmap Bitmap { get; } = bitmap;
    public string Label { get; set; } = label;
    public string TimeText => Utils.CommonUtil.FormatSeconds(Time.TotalSeconds, true);
    public void Dispose() => Bitmap.Dispose();
}

public sealed class FrameSearchMatch(
    TimeSpan start,
    TimeSpan end,
    double similarity,
    bool isLikelyMatch,
    IReadOnlyList<FrameSearchPreviewFrame> nearbyFrames) : IDisposable
{
    public TimeSpan Start { get; } = start;
    public TimeSpan End { get; } = end;
    public double Similarity { get; } = similarity;
    public bool IsLikelyMatch { get; } = isLikelyMatch;
    public IReadOnlyList<FrameSearchPreviewFrame> NearbyFrames { get; } = nearbyFrames;
    public string RangeText => $"{Utils.CommonUtil.FormatSeconds(Start.TotalSeconds, true)} – " +
                               Utils.CommonUtil.FormatSeconds(End.TotalSeconds, true);
    public string SimilarityText => $"{Similarity:P0}";
    public void Dispose()
    {
        foreach (var frame in NearbyFrames)
            frame.Dispose();
    }
}
