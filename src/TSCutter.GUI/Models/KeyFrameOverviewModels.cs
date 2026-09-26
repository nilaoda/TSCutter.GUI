using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;

namespace TSCutter.GUI.Models;

public enum KeyFrameOverviewSampling
{
    Automatic,
    EveryKeyFrame,
    Every2,
    Every5,
    Every10
}

public readonly record struct KeyFrameIndexEntry(
    int Index,
    long Pts,
    TimeSpan Timestamp,
    long FilePosition);

public sealed class KeyFrameOverviewTile(KeyFrameIndexEntry entry) : INotifyPropertyChanged, IDisposable
{
    private Bitmap? thumbnail;
    private bool isLoading;
    private string? errorText;

    public int Index { get; } = entry.Index;
    public long Pts { get; } = entry.Pts;
    public TimeSpan Timestamp { get; } = entry.Timestamp;
    public long FilePosition { get; } = entry.FilePosition;
    public string TimestampText => Utils.CommonUtil.FormatSeconds(Timestamp.TotalSeconds, true);

    public Bitmap? Thumbnail
    {
        get => thumbnail;
        private set => SetField(ref thumbnail, value);
    }

    public bool IsLoading
    {
        get => isLoading;
        private set => SetField(ref isLoading, value);
    }

    public string? ErrorText
    {
        get => errorText;
        private set => SetField(ref errorText, value);
    }

    internal void SetLoading(bool value)
    {
        IsLoading = value;
        if (value)
            ErrorText = null;
    }

    internal void SetThumbnail(Bitmap? value, string? error = null)
    {
        if (!ReferenceEquals(thumbnail, value))
            thumbnail?.Dispose();
        Thumbnail = value;
        ErrorText = error;
        IsLoading = false;
    }

    public void Dispose()
    {
        SetThumbnail(null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
