using System;
using System.Globalization;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;

namespace TSCutter.GUI.Utils.Media;

/// <summary>
/// A timecode read from media metadata, anchored at the start of the app timeline.
/// Frame numbers are derived from the media frame rate; elapsed seconds stay the
/// authoritative value for editing and seeking.
/// </summary>
public readonly record struct MediaTimecode(int Hours, int Minutes, int Seconds, int Frames,
    double FrameRate, bool DropFrame = false)
{
    public int FramesPerSecond => (int)Math.Round(FrameRate);
    private int DropFrames => DropFrame ? (int)Math.Round(FrameRate * 0.066666) : 0;
    private int FramesPerMinute => FramesPerSecond * 60 - DropFrames;
    private int FramesPer10Minutes => FramesPerMinute * 10 + DropFrames;
    private char Separator => DropFrame ? ';' : ':';

    public string StartText => Format(ToFrameNumber(), Separator);

    public string Format(double elapsedSeconds)
    {
        if (FramesPerSecond < 1)
            return StartText;
        var elapsedFrames = (long)Math.Round(Math.Max(0, elapsedSeconds) * FrameRate);
        return Format(ToFrameNumber() + elapsedFrames, Separator);
    }

    public string FormatDuration(double elapsedSeconds)
    {
        if (FramesPerSecond < 1)
            return "00:00:00:00";
        var frames = (long)Math.Round(Math.Max(0, elapsedSeconds) * FrameRate);
        return Format(frames, Separator);
    }

    internal long ToFrameNumber() => ToFrameNumber(Hours, Minutes, Seconds, Frames);

    internal long ToFrameNumber(int hours, int minutes, int seconds, int frames)
    {
        if (!DropFrame)
            return ((hours * 3600L + minutes * 60L + seconds) * FramesPerSecond) + frames;
        var totalMinutes = hours * 60L + minutes;
        return FramesPer10Minutes * (totalMinutes / 10)
               + FramesPerMinute * (totalMinutes % 10)
               + FramesPerSecond * seconds + frames;
    }

    private string Format(long frameNumber, char separator)
    {
        var fps = FramesPerSecond;
        if (frameNumber < 0)
            frameNumber = 0;
        int hours, minutes, seconds, frames;
        if (DropFrame && FramesPerMinute > 0)
        {
            var tenMinuteGroups = frameNumber / FramesPer10Minutes;
            var remainder = frameNumber % FramesPer10Minutes;
            var minutesWithinTen = (int)(remainder / FramesPerMinute);
            var framesWithinMinute = (int)(remainder % FramesPerMinute);
            // Timecodes ending in :00 or :01 at a minute boundary are invalid;
            // treat them as the preceding second.
            if (minutesWithinTen > 0 && framesWithinMinute < DropFrames)
            {
                minutesWithinTen--;
                framesWithinMinute += FramesPerMinute;
            }
            var totalMinutes = tenMinuteGroups * 10 + minutesWithinTen;
            frames = framesWithinMinute;
            seconds = frames / fps;
            frames %= fps;
            hours = (int)(totalMinutes / 60) % 24;
            minutes = (int)(totalMinutes % 60);
        }
        else
        {
            frames = (int)(frameNumber % fps);
            var totalSeconds = frameNumber / fps;
            seconds = (int)(totalSeconds % 60);
            var totalMinutes = totalSeconds / 60;
            minutes = (int)(totalMinutes % 60);
            hours = (int)(totalMinutes / 60) % 24;
        }
        return string.Create(CultureInfo.InvariantCulture,
            $"{hours:00}:{minutes:00}:{seconds:00}{separator}{frames:00}");
    }
}

/// <summary>
/// Process-wide timecode display state, so every time label can follow the
/// status-bar toggle. File-name formatting always stays in elapsed time.
/// </summary>
public static class TimecodeDisplay
{
    public static MediaTimecode? Current { get; private set; }
    public static bool Enabled { get; private set; }

    public static void Configure(MediaTimecode? timecode, bool enabled)
    {
        Current = timecode;
        Enabled = enabled && timecode is not null;
    }

    public static string Format(double seconds, bool forFile = false) =>
        !forFile && Enabled && Current is { } timecode
            ? timecode.Format(seconds)
            : CommonUtil.FormatSeconds(seconds, forFile);
}

public static class TimecodeUtil
{
    /// <summary>Uses a broadcast UTC time-of-day as the timecode at the timeline start.</summary>
    public static MediaTimecode FromUtc(DateTimeOffset utc, double frameRate) =>
        new(utc.Hour, utc.Minute, utc.Second, 0, frameRate);

    /// <summary>Reads a timecode tag from the video stream, any stream, or the container.</summary>
    public static MediaTimecode? Read(FormatContext context, MediaStream video)
    {
        var fps = Rate(video.AvgFrameRate);
        if (fps <= 0)
            fps = Rate(video.RFrameRate);

        if (video.Metadata.TryGetValue("timecode", out var text)
            || FindStreamTag(context, out text)
            || context.Metadata.TryGetValue("timecode", out text))
            return TryParse(text, fps);
        return null;
    }

    public static MediaTimecode? TryParse(string? text, double frameRate)
    {
        if (string.IsNullOrWhiteSpace(text) || frameRate < 1 || frameRate > 120)
            return null;
        var parts = text.Trim().Split([':', ';']);
        if (parts.Length != 4)
            return null;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var frames))
            return null;
        var fps = (int)Math.Round(frameRate);
        if (minutes >= 60 || seconds >= 60 || frames >= fps || hours < 0)
            return null;
        return new MediaTimecode(hours, minutes, seconds, frames, frameRate, text.Contains(';'));
    }

    /// <summary>Converts an absolute timecode entered by the user into elapsed timeline seconds.</summary>
    public static bool TryParseElapsed(string text, MediaTimecode start, out double elapsed)
    {
        elapsed = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var parts = text.Trim().Split([':', ';']);
        if (parts.Length != 4
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var frames))
            return false;
        var fps = start.FramesPerSecond;
        var dropFrame = text.Contains(';');
        if (fps < 1 || minutes >= 60 || seconds >= 60 || frames >= fps || hours < 0)
            return false;
        var entered = new MediaTimecode(hours, minutes, seconds, frames, start.FrameRate, dropFrame);
        var total = entered.ToFrameNumber();
        var origin = start.ToFrameNumber();
        if (total < origin)
            return false;
        elapsed = (total - origin) / start.FrameRate;
        return true;
    }

    private static bool FindStreamTag(FormatContext context, out string text)
    {
        foreach (var stream in context.Streams)
            if (stream.Metadata.TryGetValue("timecode", out text))
                return true;
        text = "";
        return false;
    }

    private static double Rate(AVRational value) =>
        value.den == 0 ? 0 : value.num / (double)value.den;
}
