using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using TSCutter.GUI.Models;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.Services;

internal sealed class FrameSearchService
{
    private const double CandidateThreshold = 0.82;
    private const int MaximumResults = 12;
    private const int PreviewWidth = 150;
    private const int PreviewHeight = 84;
    private const int MaximumNearbyScanFrames = 200;

    public async Task SearchAsync(
        string filePath, byte[] referenceJpeg, TimeSpan startTime,
        Func<FrameSearchMatch, bool, TimeSpan?, Task> onMatch,
        Func<CancellationToken, Task> pauseAfterMatch, IProgress<double>? progress,
        CancellationToken token)
    {
        using var stream = new MemoryStream(referenceJpeg, writable: false);
        using var reference = new Bitmap(stream);
        var target = FrameVisualMatcher.Create(reference);
        using var decoder = new VideoInstance(filePath, enableHardwareDecoding: false);
        await decoder.InitVideoAsync(token).ConfigureAwait(false);
        var duration = decoder.GetVideoDurationInSeconds();
        if (!double.IsFinite(duration) || duration <= 0)
            throw new InvalidDataException("The video duration is unavailable.");
        var startSeconds = Math.Max(0, startTime.TotalSeconds);
        if (startSeconds >= duration)
        {
            progress?.Report(1);
            return;
        }
        if (startSeconds > 0)
            await decoder.SeekToTimeAsync(TimeSpan.FromSeconds(startSeconds), token)
                .ConfigureAwait(false);
        var gap = decoder.EstimatedKeyFrameIntervalSeconds;
        var interval = double.IsFinite(gap) && gap > 0 ? Math.Max(1, gap) : 1;
        var candidates = new List<(Sample Sample, FrameSearchPreviewFrame Preview)>();
        var seenFrames = new HashSet<long>();
        var tracker = new MatchGroupTracker(interval, CandidateThreshold, MaximumResults);
        FrameSearchPreviewFrame? activeBest = null;
        FrameNeighborhood? activeNeighborhood = null;
        var recentFrames = new List<FrameSearchPreviewFrame>(2);
        VideoInstance? previewDecoder = null;
        var lastReportedProgress = 0d;

        async Task PublishAsync(MatchGroupUpdate update, FrameSearchPreviewFrame scoredFrame,
            bool includeNearby, FrameNeighborhood? neighborhood = null)
        {
            token.ThrowIfCancellationRequested();
            if (previewDecoder is null && includeNearby && neighborhood is null)
            {
                previewDecoder = new VideoInstance(filePath, enableHardwareDecoding: false);
                await previewDecoder.InitVideoAsync(token).ConfigureAwait(false);
            }
            var match = await CreateMatchAsync(previewDecoder, scoredFrame, update,
                    interval, includeNearby, neighborhood, token)
                .ConfigureAwait(false);
            try
            {
                await onMatch(match, update.Replace, update.EvictStart).ConfigureAwait(false);
            }
            catch
            {
                match.Dispose();
                throw;
            }
        }

        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var decoded = await decoder.DecodeNextSearchFrameAsync(176, 99, token)
                    .ConfigureAwait(false);
                if (decoded is null) break;
                FrameSearchPreviewFrame? currentPreview = null;
                try
                {
                    // 定位可能落在目标时间之前的关键帧，不能把它计入搜索结果。
                    if (decoded.Bitmap is null || decoded.FrameTimestamp.TotalSeconds < startSeconds ||
                        !seenFrames.Add(decoded.FrameTimestamp.Ticks))
                        continue;
                    var aspect = GetDisplayAspectRatio(decoded);
                    var similarity = FrameVisualMatcher.Similarity(target,
                        FrameVisualMatcher.Create(decoded.Bitmap, aspect));
                    var sample = new Sample(decoded.FrameTimestamp, similarity);
                    var fraction = SearchProgress(sample.Time, startSeconds, duration);
                    if (fraction - lastReportedProgress >= 0.005)
                    {
                        progress?.Report(fraction);
                        lastReportedProgress = fraction;
                    }
                    FrameSearchPreviewFrame CurrentPreview() => currentPreview ??=
                        CreatePreview(decoded, 176, 99);

                    foreach (var update in tracker.Observe(sample.Time, similarity))
                    {
                        var scoredFrame = update.Best == sample.Time
                            ? CurrentPreview() : activeBest;
                        if (scoredFrame is null || scoredFrame.Time != update.Best)
                            throw new InvalidOperationException("The scored frame is unavailable.");
                        await PublishAsync(update, scoredFrame, includeNearby: update.Replace,
                                neighborhood: update.Replace ? activeNeighborhood : null)
                            .ConfigureAwait(false);
                        if (update.Replace)
                            await pauseAfterMatch(token).ConfigureAwait(false);
                    }

                    if (tracker.ActiveBest == sample.Time)
                    {
                        activeBest?.Dispose();
                        activeBest = CloneScanPreview(CurrentPreview());
                        activeNeighborhood?.Dispose();
                        activeNeighborhood = null;
                        activeNeighborhood = new FrameNeighborhood(recentFrames, activeBest);
                    }
                    else if (tracker.ActiveBest is null)
                    {
                        activeBest?.Dispose();
                        activeBest = null;
                        activeNeighborhood?.Dispose();
                        activeNeighborhood = null;
                    }
                    else
                        activeNeighborhood?.AddFollowing(CurrentPreview());

                    if (candidates.Count < 3 || similarity > candidates[^1].Sample.Similarity)
                    {
                        var preview = CloneScanPreview(CurrentPreview());
                        candidates.Add((sample, preview));
                        candidates.Sort((left, right) =>
                            right.Sample.Similarity.CompareTo(left.Sample.Similarity));
                        if (candidates.Count > 3)
                        {
                            candidates[^1].Preview.Dispose();
                            candidates.RemoveAt(candidates.Count - 1);
                        }
                    }
                    recentFrames.Add(CurrentPreview());
                    if (recentFrames.Count > 2)
                    {
                        recentFrames[0].Dispose();
                        recentFrames.RemoveAt(0);
                    }
                }
                finally
                {
                    if (currentPreview is not null &&
                        recentFrames.All(item => !ReferenceEquals(item, currentPreview)))
                        currentPreview.Dispose();
                    DisposeDecoded(decoded);
                }
            }

            if (tracker.Complete() is { } finalUpdate && activeBest is not null)
                await PublishAsync(finalUpdate, activeBest, includeNearby: true,
                        neighborhood: activeNeighborhood)
                    .ConfigureAwait(false);

            if (!tracker.HasStrongMatch)
            {
                var samples = candidates.Select(item => item.Sample).ToList();
                foreach (var group in GroupSamples(samples, interval))
                {
                    var scoredFrame = candidates.First(item => item.Sample.Time == group.Best.Time).Preview;
                    await PublishAsync(new MatchGroupUpdate(group.Start, group.End,
                            group.Best.Time, group.Best.Similarity, Replace: false),
                        scoredFrame, includeNearby: true).ConfigureAwait(false);
                }
            }
            progress?.Report(1);
        }
        finally
        {
            activeBest?.Dispose();
            activeNeighborhood?.Dispose();
            foreach (var frame in recentFrames) frame.Dispose();
            foreach (var candidate in candidates) candidate.Preview.Dispose();
            previewDecoder?.Dispose();
        }
    }

    private static async Task<FrameSearchMatch> CreateMatchAsync(
        VideoInstance? decoder, FrameSearchPreviewFrame scoredFrame,
        MatchGroupUpdate update, double interval,
        bool includeNearby, FrameNeighborhood? neighborhood, CancellationToken token)
    {
        var previews = includeNearby && neighborhood is not null
            ? neighborhood.CreatePreviews()
            : includeNearby && decoder is not null
                ? await DecodeNearbyAsync(decoder, scoredFrame, interval, token)
                    .ConfigureAwait(false)
                : new List<FrameSearchPreviewFrame> { ClonePreview(scoredFrame) };
        if (previews.Count == 1)
            previews[0].Label = includeNearby
                ? LocalizationManager.Instance.String_FrameSearch_BestFrame
                : LocalizationManager.Instance.String_FrameSearch_CandidateFrame;
        return new FrameSearchMatch(update.Start, update.End, update.Similarity,
            update.Similarity >= CandidateThreshold, previews);
    }

    internal static IReadOnlyList<(TimeSpan Start, TimeSpan End, TimeSpan Best, double Similarity)>
        GroupScores(IEnumerable<(TimeSpan Time, double Similarity)> scores, double interval)
    {
        var groups = GroupSamples(scores.Select(item => new Sample(item.Time, item.Similarity)).ToList(), interval);
        return groups.Select(group => (group.Start, group.End, group.Best.Time, group.Best.Similarity)).ToArray();
    }

    internal static double SearchProgress(TimeSpan frameTime, double startSeconds, double duration) =>
        Math.Clamp((frameTime.TotalSeconds - startSeconds) / (duration - startSeconds), 0, 1);

    internal static IReadOnlyList<int> NearbyWindowIndices(int count, int bestIndex)
    {
        if (count <= 0 || bestIndex < 0 || bestIndex >= count)
            return [];
        var start = Math.Clamp(bestIndex - 2, 0, Math.Max(0, count - 5));
        return Enumerable.Range(start, Math.Min(5, count - start)).ToArray();
    }

    private static async Task<List<FrameSearchPreviewFrame>> DecodeNearbyAsync(
        VideoInstance decoder, FrameSearchPreviewFrame scoredFrame,
        double interval, CancellationToken token)
    {
        var bestTime = scoredFrame.Time;
        var frames = new List<FrameSearchPreviewFrame>();
        try
        {
            FrameSearchPreviewFrame? first = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var distance = Math.Max(4, interval * (6 + attempt * 4));
                var start = Math.Max(0, bestTime.TotalSeconds - distance);
                first = await DecodeThumbnailAsync(decoder, TimeSpan.FromSeconds(start),
                    PreviewWidth, PreviewHeight, token).ConfigureAwait(false);
                if (first is null || first.Time < bestTime || start == 0) break;
                first.Dispose();
                first = null;
            }
            if (first is not null) frames.Add(first);

            while (frames.Count < MaximumNearbyScanFrames &&
                   frames.Count(frame => frame.Time > bestTime) < 2)
            {
                var decoded = await decoder.DecodeNextSearchFrameAsync(
                    PreviewWidth, PreviewHeight, token).ConfigureAwait(false);
                if (decoded is null) break;
                try
                {
                    if (decoded.Bitmap is not null &&
                        frames.All(frame => frame.Time != decoded.FrameTimestamp))
                        frames.Add(CreatePreview(decoded, PreviewWidth, PreviewHeight));
                }
                finally
                {
                    DisposeDecoded(decoded);
                }
            }

            var decodedBest = frames.FindIndex(frame => frame.Time == bestTime);
            if (decodedBest >= 0)
            {
                frames[decodedBest].Dispose();
                frames.RemoveAt(decodedBest);
            }
            var best = ClonePreview(scoredFrame);
            var bestIndex = frames.FindIndex(frame => frame.Time > bestTime);
            if (bestIndex < 0) bestIndex = frames.Count;
            frames.Insert(bestIndex, best);

            var selectedIndices = NearbyWindowIndices(frames.Count, bestIndex);
            var selected = new List<FrameSearchPreviewFrame>(selectedIndices.Count);
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames[index];
                if (!selectedIndices.Contains(index))
                {
                    frame.Dispose();
                    continue;
                }
                frame.Label = NeighborLabel(index - bestIndex);
                selected.Add(frame);
            }
            return selected;
        }
        catch
        {
            foreach (var frame in frames) frame.Dispose();
            throw;
        }
    }

    private static IEnumerable<SampleGroup> GroupSamples(List<Sample> samples, double interval)
    {
        samples.Sort((left, right) => left.Time.CompareTo(right.Time));
        SampleGroup? current = null;
        foreach (var sample in samples)
        {
            if (current is null || (sample.Time - current.End).TotalSeconds > interval * 1.5)
            {
                if (current is not null) yield return current;
                current = new SampleGroup(sample.Time, sample.Time, sample);
            }
            else
            {
                current.End = sample.Time;
                if (sample.Similarity > current.Best.Similarity)
                    current.Best = sample;
            }
        }
        if (current is not null) yield return current;
    }

    private static async Task<FrameSearchPreviewFrame?> DecodeThumbnailAsync(
        VideoInstance decoder, TimeSpan time, int width, int height, CancellationToken token)
    {
        var decoded = await decoder.DecodeAtTimeAsync(time, width, height, token).ConfigureAwait(false);
        try
        {
            if (decoded.Bitmap is null) return null;
            return CreatePreview(decoded, width, height);
        }
        finally
        {
            DisposeDecoded(decoded);
        }
    }

    private static double GetDisplayAspectRatio(DecodeResult decoded) =>
        decoded.RequiresSampleAspectRatioCorrection && decoded.SourcePixelSize.Height > 0
            ? decoded.SourcePixelSize.Width / (double)decoded.SourcePixelSize.Height : 0;

    private static FrameSearchPreviewFrame CreatePreview(DecodeResult decoded, int width, int height) =>
        new(decoded.FrameTimestamp, ImageUtil.CreateThumbnail(
            decoded.Bitmap!, width, height, GetDisplayAspectRatio(decoded)));

    private static FrameSearchPreviewFrame ClonePreview(FrameSearchPreviewFrame preview) =>
        new(preview.Time, ImageUtil.CreateThumbnail(preview.Bitmap, PreviewWidth, PreviewHeight));

    private static FrameSearchPreviewFrame CloneScanPreview(FrameSearchPreviewFrame preview) =>
        new(preview.Time, ImageUtil.CreateThumbnail(preview.Bitmap, 176, 99));

    private static string NeighborLabel(int offset) => offset switch
    {
        0 => LocalizationManager.Instance.String_FrameSearch_BestFrame,
        < 0 => string.Format(LocalizationManager.Instance.String_FrameSearch_BeforeOffset,
            -offset),
        _ => string.Format(LocalizationManager.Instance.String_FrameSearch_AfterOffset,
            offset)
    };

    private sealed class FrameNeighborhood : IDisposable
    {
        private readonly List<FrameSearchPreviewFrame> frames = [];
        private readonly int bestIndex;

        public FrameNeighborhood(IEnumerable<FrameSearchPreviewFrame> preceding,
            FrameSearchPreviewFrame best)
        {
            try
            {
                foreach (var frame in preceding)
                    frames.Add(CloneScanPreview(frame));
                bestIndex = frames.Count;
                frames.Add(CloneScanPreview(best));
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void AddFollowing(FrameSearchPreviewFrame frame)
        {
            if (frames.Count < bestIndex + 3 && frame.Time > frames[bestIndex].Time)
                frames.Add(CloneScanPreview(frame));
        }

        public List<FrameSearchPreviewFrame> CreatePreviews()
        {
            var result = new List<FrameSearchPreviewFrame>(frames.Count);
            try
            {
                for (var index = 0; index < frames.Count; index++)
                {
                    var preview = ClonePreview(frames[index]);
                    preview.Label = NeighborLabel(index - bestIndex);
                    result.Add(preview);
                }
                return result;
            }
            catch
            {
                foreach (var frame in result) frame.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            foreach (var frame in frames) frame.Dispose();
        }
    }

    private static void DisposeDecoded(DecodeResult decoded)
    {
        decoded.BitmapLease?.Dispose();
        if (decoded.BitmapLease is null) decoded.Bitmap?.Dispose();
        decoded.GpuFrame?.Dispose();
    }

    private readonly record struct Sample(TimeSpan Time, double Similarity);

    internal readonly record struct MatchGroupUpdate(
        TimeSpan Start, TimeSpan End, TimeSpan Best, double Similarity, bool Replace,
        TimeSpan? EvictStart = null);

    internal sealed class MatchGroupTracker(double interval, double threshold, int maximumResults)
    {
        // 广告画面可能隔数个关键帧才完成文字动画，至少观察三秒再结束匹配段。
        private readonly double groupGap = Math.Max(3, interval * 1.5);
        private SampleGroup? active;
        private readonly List<(TimeSpan Start, double Similarity)> published = [];

        public bool HasStrongMatch { get; private set; }
        public TimeSpan? ActiveBest => active?.Best.Time;

        public IReadOnlyList<MatchGroupUpdate> Observe(TimeSpan time, double similarity)
        {
            var updates = new List<MatchGroupUpdate>(2);
            if (active is not null && (time - active.End).TotalSeconds > groupGap)
            {
                if (FinishActive() is { } finished) updates.Add(finished);
                active = null;
            }

            if (similarity < threshold) return updates;
            HasStrongMatch = true;
            var sample = new Sample(time, similarity);
            if (active is not null)
            {
                active.End = time;
                if (similarity > active.Best.Similarity)
                    active.Best = sample;
                if (!active.Published && TryPublish(active) is { } promoted)
                    updates.Add(promoted);
            }
            else
            {
                active = new SampleGroup(time, time, sample);
                if (TryPublish(active) is { } initial)
                    updates.Add(initial);
            }
            return updates;
        }

        public MatchGroupUpdate? Complete()
        {
            var update = FinishActive();
            active = null;
            return update;
        }

        private MatchGroupUpdate? FinishActive()
        {
            if (active is not { Published: true } group) return null;
            var index = published.FindIndex(item => item.Start == group.Start);
            if (index >= 0)
                published[index] = (group.Start, group.Best.Similarity);
            return new MatchGroupUpdate(group.Start, group.End, group.Best.Time,
                group.Best.Similarity, Replace: true);
        }

        private MatchGroupUpdate? TryPublish(SampleGroup group)
        {
            if (maximumResults <= 0) return null;
            TimeSpan? evictStart = null;
            if (published.Count >= maximumResults)
            {
                var weakest = published.MinBy(item => item.Similarity);
                if (group.Best.Similarity <= weakest.Similarity) return null;
                evictStart = weakest.Start;
                published.Remove(weakest);
            }
            published.Add((group.Start, group.Best.Similarity));
            group.Published = true;
            return new MatchGroupUpdate(group.Start, group.End, group.Best.Time,
                group.Best.Similarity, Replace: false, EvictStart: evictStart);
        }
    }

    private sealed class SampleGroup(TimeSpan start, TimeSpan end, Sample best)
    {
        public TimeSpan Start { get; } = start;
        public TimeSpan End { get; set; } = end;
        public Sample Best { get; set; } = best;
        public bool Published { get; set; }
    }
}
