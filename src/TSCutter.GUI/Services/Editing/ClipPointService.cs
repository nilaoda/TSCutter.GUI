using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.Utils.Editing;
using TSCutter.GUI.Utils.Files;
using TSCutter.GUI.Utils.Media;

namespace TSCutter.GUI.Services.Editing;

internal static class ClipPointService
{
    internal const string Extension = "tscutpoints";

    internal static void Validate(ClipPointFile file)
    {
        if (file.Format != "TSCutter.ClipPoints" || file.Version != 1 || file.Source is null ||
            file.Clips is not { Count: > 0 and <= CutterProjectService.MaximumItems } ||
            !double.IsFinite(file.Duration) || file.Duration <= 0 || file.Duration > TimeSpan.MaxValue.TotalSeconds / 4 ||
            file.TimeBaseNumerator <= 0 || file.TimeBaseDenominator <= 0 ||
            !Enum.IsDefined(file.CutMode) || !Enum.IsDefined(file.ClipMode) ||
            file.PreviewSelection is not { StreamIndex: >= 0 } || file.FirstFramePts == long.MinValue)
            throw new InvalidDataException("Invalid clip point file.");
        CutterProjectService.ValidateSourceShape(file.Source, checkSourceFiles: false);
        foreach (var clip in file.Clips)
        {
            CutterProjectService.ValidateClip(clip, file.Source.Length, file.TimeBasedSource, checkSourceFiles: false);
            if (clip.EndFramePosition < -1 || clip.EndFramePosition > file.Source.Length ||
                clip.StartTime > TimeSpan.MaxValue.TotalSeconds / 4 || clip.EndTime > TimeSpan.MaxValue.TotalSeconds / 4 ||
                clip.StartPts == long.MinValue || clip.EndPts == long.MinValue ||
                clip.FrameAccurate && file.CutMode != ClipCutMode.FrameAccurate)
                throw new InvalidDataException("Invalid clip boundaries.");
        }
    }

    internal static async Task<ClipPointFile> LoadAsync(string path, CancellationToken token = default)
    {
        MediaFileAccess.Current.DemandReadable(path);
        if (new FileInfo(path).Length > CutterProjectService.MaximumProjectBytes)
            throw new InvalidDataException("The clip point file is too large.");
        var json = await File.ReadAllTextAsync(path, token).ConfigureAwait(false);
        var file = JsonSerializer.Deserialize(json, AppJsonContext.Default.ClipPointFile)
            ?? throw new InvalidDataException("The clip point file is empty.");
        Validate(file);
        return file;
    }

    internal static async Task SaveAsync(string path, ClipPointFile file, CancellationToken token = default)
    {
        var json = await Task.Run(() =>
        {
            Validate(file);
            return JsonSerializer.Serialize(file, AppJsonContext.Default.ClipPointFile);
        }, token).ConfigureAwait(false);
        if (Encoding.UTF8.GetByteCount(json) > CutterProjectService.MaximumProjectBytes)
            throw new InvalidDataException("The clip point file is too large.");
        var fullPath = Path.GetFullPath(path);
        var access = MediaFileAccess.Current;
        access.DemandOutput(fullPath);
        if (!string.Equals(Path.GetExtension(fullPath), "." + Extension, StringComparison.OrdinalIgnoreCase) ||
            MediaFileUtil.PathsEqual(fullPath, file.Source.Path))
            throw new InvalidDataException("Invalid clip point output path.");
        var temporary = Path.Combine(Path.GetDirectoryName(fullPath)!, $".clip-points-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, json, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            access.PublishExport(temporary, fullPath);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    internal static bool Matches(ClipPointFile file, string targetPath, PreviewStreamSelection? selection,
        long firstPts, int numerator, int denominator)
    {
        Validate(file);
        MediaFileAccess.Current.DemandReadable(targetPath);
        if (file.PreviewSelection != selection || file.FirstFramePts != firstPts ||
            file.TimeBaseNumerator != numerator || file.TimeBaseDenominator != denominator ||
            new FileInfo(targetPath).Length != file.Source.Length)
            return false;
        // 只读取用户当前打开的素材，导入文件中的来源路径仅用于显示和校验元数据。
        var current = CutterProjectService.Snapshot(targetPath, file.Source.AnchorOffsets);
        return string.Equals(current.PrefixFingerprint, file.Source.PrefixFingerprint, StringComparison.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<ClipPointRange> Ranges(ClipPointFile file, double duration, double offset) =>
        file.Clips.Select(clip =>
        {
            var start = clip.StartTime + offset;
            var end = clip.EndPosition < 0 ? duration : clip.EndTime + offset;
            return new ClipPointRange(clip, start, end, double.IsFinite(start) && double.IsFinite(end) &&
                start >= 0 && start < duration && end >= start && end <= duration + 0.001);
        }).ToArray();

    internal static ProjectClip Copy(ProjectClip clip) => new()
    {
        Name = clip.Name, Notes = clip.Notes, Color = clip.Color,
        StartTime = clip.StartTime, EndTime = clip.EndTime,
        StartPts = clip.StartPts, EndPts = clip.EndPts,
        StartPosition = clip.StartPosition, EndPosition = clip.EndPosition,
        EndFramePosition = clip.EndFramePosition,
        StartSignature = clip.StartSignature, EndSignature = clip.EndSignature,
        FrameAccurate = clip.FrameAccurate, NeedsBoundaryReview = clip.NeedsBoundaryReview,
        StartThumbnailJpeg = clip.StartThumbnailJpeg, EndThumbnailJpeg = clip.EndThumbnailJpeg,
        IsSelected = true
    };

    internal static async Task<IReadOnlyList<ProjectClip>> RelocateAsync(string targetPath,
        PreviewStreamSelection? selection, IReadOnlyList<ClipPointRange> ranges, ClipCutMode mode,
        IProgress<int>? progress, CancellationToken token)
    {
        MediaFileAccess.Current.DemandReadable(targetPath);
        using var video = new VideoInstance(targetPath, false, selection);
        await video.InitVideoAsync(token).ConfigureAwait(false);
        if (!video.CanClip || mode == ClipCutMode.FrameAccurate && video.FrameAccurateSupport != FrameAccurateSupport.Supported)
            throw new InvalidDataException("The target does not support this cut mode.");
        var clips = new List<ProjectClip>();
        var valid = ranges.Where(range => range.InRange).ToArray();
        for (var index = 0; index < valid.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var range = valid[index];
            progress?.Report(index);
            var clip = new ProjectClip { Name = range.Clip.Name, Notes = range.Clip.Notes, Color = range.Clip.Color,
                FrameAccurate = mode == ClipCutMode.FrameAccurate, IsSelected = true, NeedsBoundaryReview = true };
            await ApplyAsync(await LocateAsync(range.Start).ConfigureAwait(false), start: true).ConfigureAwait(false);
            if (range.Clip.EndPosition < 0)
                ToEndOfFile();
            else
            {
                await ApplyAsync(await LocateAsync(range.End).ConfigureAwait(false), start: false).ConfigureAwait(false);
                // 关键帧剪辑中短于关键帧间隔的片段，起止会落在同一个关键帧上：终点延到下一个关键帧，
                // 后面没有关键帧时延到文件末尾，保证片段仍包含原来标记的画面。
                if (mode == ClipCutMode.KeyFrame && clip.EndPts <= clip.StartPts)
                {
                    var next = await video.DecodeNextFrameAsync(1, token).ConfigureAwait(false);
                    if (next.FramePts > clip.StartPts)
                        await ApplyAsync(next, start: false).ConfigureAwait(false);
                    else
                    {
                        Release(next);
                        ToEndOfFile();
                    }
                }
            }
            if (clip.EndPosition >= 0 && (clip.EndPts < clip.StartPts || clip.EndTime < clip.StartTime))
                throw new InvalidDataException("The mapped clip has no usable range.");
            clips.Add(clip);
            progress?.Report(index + 1);

            void ToEndOfFile()
            {
                clip.EndPosition = -1;
                clip.EndFramePosition = -1;
                clip.EndPts = 0;
                clip.EndTime = video.GetVideoDurationInSeconds();
                clip.EndThumbnailJpeg = null;
            }

            async Task<DecodeResult> LocateAsync(double seconds)
            {
                DecodeResult? frame;
                if (mode == ClipCutMode.FrameAccurate)
                {
                    var window = await video.DecodeFrameWindowAsync(video.SecondsToPts(seconds), 0, 0, 320, 180, token).ConfigureAwait(false);
                    frame = window.Frames.FirstOrDefault();
                }
                else
                    frame = await video.DecodeKeyFrameAtOrBeforePtsAsync(video.SecondsToPts(seconds), token).ConfigureAwait(false);
                return frame ?? throw new InvalidDataException("Cannot locate the requested boundary.");
            }

            async Task ApplyAsync(DecodeResult frame, bool start)
            {
                try
                {
                    if (frame.FramePts == long.MinValue || !video.CanRemuxClip && frame.FramePosition < 0)
                        throw new InvalidDataException("The located frame has no usable boundary.");
                    byte[]? thumbnail = null;
                    if (frame.Bitmap is { } bitmap)
                    {
                        using var image = ImageUtil.CreateThumbnail(bitmap, 240, 135);
                        using var stream = new MemoryStream();
                        ImageUtil.SaveAsJpeg(image, stream, 80);
                        thumbnail = stream.ToArray();
                    }
                    if (start)
                    {
                        clip.StartTime = frame.FrameTimestamp.TotalSeconds;
                        clip.StartPts = frame.FramePts;
                        clip.StartPosition = Math.Max(0, frame.FramePosition);
                        clip.StartThumbnailJpeg = thumbnail;
                    }
                    else
                    {
                        clip.EndTime = frame.FrameTimestamp.TotalSeconds;
                        clip.EndPts = frame.FramePts;
                        clip.EndFramePosition = frame.FramePosition;
                        clip.EndPosition = video.CanRemuxClip || clip.FrameAccurate ? Math.Max(1, frame.FramePosition) :
                            await TsClipBoundaryResolver.ResolveEndAsync(targetPath, frame.FramePosition, frame.FramePts90k,
                                frame.FrameEndSignature, token).ConfigureAwait(false);
                        clip.EndThumbnailJpeg = thumbnail;
                    }
                }
                finally
                {
                    Release(frame);
                }
            }
        }
        return clips;
    }

    private static void Release(DecodeResult frame)
    {
        frame.BitmapLease?.Dispose();
        if (frame.BitmapLease is null)
            frame.Bitmap?.Dispose();
        frame.GpuFrame?.Dispose();
    }
}
