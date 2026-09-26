using System.IO;
using SkiaSharp;
using TSCutter.GUI.Models;
using TSCutter.GUI.Services;
using TSCutter.GUI.ViewModels;
using TSCutter.GUI.Views;
using Xunit;

namespace TSCutter.GUI.Tests;

public sealed class FrameSearchTests
{
    [Fact]
    public async Task BothDialogViewModelsResolveToTheirWindowTypes()
    {
        var locator = new TestViewLocator();
        var manager = new FrameTemplateManagerWindowViewModel();
        var search = new FrameSearchWindowViewModel();
        try
        {
            Assert.Equal(typeof(FrameTemplateManagerWindow).FullName, locator.NameFor(manager));
            Assert.Equal(typeof(FrameSearchWindow).FullName, locator.NameFor(search));
        }
        finally
        {
            manager.OnClosed();
            await search.OnClosedAsync();
        }
    }

    [Fact]
    public async Task SavedFramesRoundTripAndInvalidLibraryIsNotOverwritten()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"frame-search-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "saved-frames.json");
            var store = new SavedFrameStore(path);
            var frame = new SavedFrameTemplate { Name = "Opening advertisement", Jpeg = [1, 2, 3] };
            await store.SaveAsync(new SavedFrameLibrary { Templates = [frame] });
            var loaded = await store.LoadAsync();
            Assert.Equal(frame.Id, Assert.Single(loaded.Templates).Id);
            Assert.Equal(frame.Jpeg, loaded.Templates[0].Jpeg);

            await File.WriteAllTextAsync(path, "{ damaged json");
            await Assert.ThrowsAnyAsync<Exception>(() => store.LoadAsync());
            Assert.Equal("{ damaged json", await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CompressedFrameMatchesMoreCloselyThanAnotherPicture()
    {
        using var original = CreatePicture(0, 95);
        using var compressed = CreatePicture(0, 38);
        using var unrelated = CreatePicture(1, 95);

        var reference = FrameVisualMatcher.Create(original);
        var same = FrameVisualMatcher.Similarity(reference, FrameVisualMatcher.Create(compressed));
        var different = FrameVisualMatcher.Similarity(reference, FrameVisualMatcher.Create(unrelated));

        Assert.True(same >= 0.82, $"JPEG-compressed reference scored {same:F3}");
        Assert.True(same > different + 0.1,
            $"Similar and unrelated frames scored {same:F3} and {different:F3}");
    }

    [Fact]
    public void DifferentDarkBroadcastScenesDoNotLookLikeStrongMatches()
    {
        using var reference = CreateDarkBroadcastPicture(alternate: false);
        using var differentScene = CreateDarkBroadcastPicture(alternate: true);
        using var image = SKImage.FromBitmap(reference);
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 35);
        using var compressed = SKBitmap.Decode(jpeg.ToArray());
        using var shifted = new SKBitmap(320, 180);
        using (var canvas = new SKCanvas(shifted))
        {
            canvas.Clear(SKColors.Black);
            canvas.DrawBitmap(reference, new SKRect(4, 2, 324, 182));
        }

        var target = FrameVisualMatcher.Create(reference);
        var trueMatch = FrameVisualMatcher.Similarity(target,
            FrameVisualMatcher.Create(compressed));
        var shiftedMatch = FrameVisualMatcher.Similarity(target,
            FrameVisualMatcher.Create(shifted));
        var falseMatch = FrameVisualMatcher.Similarity(target,
            FrameVisualMatcher.Create(differentScene));

        Assert.InRange(trueMatch, 0.82, 1);
        Assert.InRange(shiftedMatch, 0.82, 1);
        Assert.True(falseMatch < 0.82,
            $"Unrelated dark scenes scored {falseMatch:P0}");
    }

    [Fact]
    public void FlatFramesMatchOnlyWhenTheirColorsAreClose()
    {
        using var reference = new SKBitmap(320, 180);
        using var similar = new SKBitmap(320, 180);
        using var different = new SKBitmap(320, 180);
        reference.Erase(new SKColor(24, 30, 42));
        similar.Erase(new SKColor(25, 31, 43));
        different.Erase(new SKColor(70, 30, 42));

        var target = FrameVisualMatcher.Create(reference);
        Assert.True(FrameVisualMatcher.Similarity(target,
            FrameVisualMatcher.Create(similar)) >= 0.82);
        Assert.True(FrameVisualMatcher.Similarity(target,
            FrameVisualMatcher.Create(different)) < 0.82);
    }

    [Fact]
    public void ConsecutiveMatchesChooseTheHighestScoringFrame()
    {
        var scores = new[]
        {
            (TimeSpan.FromSeconds(10), 0.91),
            (TimeSpan.FromSeconds(12), 0.94),
            (TimeSpan.FromSeconds(14), 0.90),
            (TimeSpan.FromSeconds(40), 0.88)
        };
        var groups = FrameSearchService.GroupScores(scores, 2);
        Assert.Equal(2, groups.Count);
        Assert.Equal(TimeSpan.FromSeconds(14), groups[0].End);
        Assert.Equal(TimeSpan.FromSeconds(12), groups[0].Best);

    }

    [Fact]
    public void NearbyWindowCentersFiveKeyFramesOnTheBestMatch()
    {
        Assert.Equal([2, 3, 4, 5, 6],
            FrameSearchService.NearbyWindowIndices(9, 4));
        Assert.Equal([0, 1, 2],
            FrameSearchService.NearbyWindowIndices(3, 0));
        Assert.Equal([2, 3, 4, 5, 6],
            FrameSearchService.NearbyWindowIndices(7, 6));
    }

    [Fact]
    public void SearchProgressUsesOnlyTheRemainingVideo()
    {
        Assert.Equal(0, FrameSearchService.SearchProgress(TimeSpan.FromSeconds(30), 30, 90));
        Assert.Equal(0.5, FrameSearchService.SearchProgress(TimeSpan.FromSeconds(60), 30, 90));
        Assert.Equal(1, FrameSearchService.SearchProgress(TimeSpan.FromSeconds(90), 30, 90));
        Assert.Equal(0, FrameSearchService.SearchProgress(TimeSpan.FromSeconds(29), 30, 90));
    }

    [Fact]
    public void FirstStrongFrameIsPublishedBeforeTheSearchReachesTheEnd()
    {
        var tracker = new FrameSearchService.MatchGroupTracker(1, 0.82, 12);
        var first = Assert.Single(tracker.Observe(TimeSpan.FromSeconds(4), 0.94));
        Assert.False(first.Replace);
        Assert.Equal(TimeSpan.FromSeconds(4), first.Start);
        Assert.Empty(tracker.Observe(TimeSpan.FromSeconds(5), 0.96));
        Assert.Empty(tracker.Observe(TimeSpan.FromSeconds(6), 0.95));

        Assert.Empty(tracker.Observe(TimeSpan.FromSeconds(8), 0.2));
        var completed = Assert.Single(tracker.Observe(TimeSpan.FromSeconds(10), 0.2));
        Assert.True(completed.Replace);
        Assert.Equal(first.Start, completed.Start);
        Assert.Equal(TimeSpan.FromSeconds(6), completed.End);
        Assert.Equal(TimeSpan.FromSeconds(5), completed.Best);
        Assert.True(tracker.HasStrongMatch);
    }

    [Fact]
    public void LaterAdvertisementFrameCanReplaceTheFirstCandidate()
    {
        var tracker = new FrameSearchService.MatchGroupTracker(1, 0.82, 12);
        var first = TimeSpan.FromSeconds(1014.24);
        Assert.Single(tracker.Observe(first, 0.83));
        foreach (var offset in new[] { 0.48, 0.96, 1.44, 1.92 })
            Assert.Empty(tracker.Observe(first + TimeSpan.FromSeconds(offset), 0.5));

        var better = first + TimeSpan.FromSeconds(2.4);
        Assert.Empty(tracker.Observe(better, 0.96));
        Assert.Equal(better, tracker.ActiveBest);
        var completed = Assert.Single(tracker.Observe(better + TimeSpan.FromSeconds(3.5), 0.4));
        Assert.Equal(better, completed.Best);
        Assert.Equal(0.96, completed.Similarity);
    }

    [Fact]
    public void LaterBetterMatchesReplaceWeakerVisibleResults()
    {
        var tracker = new FrameSearchService.MatchGroupTracker(1, 0.82, 1);
        var first = Assert.Single(tracker.Observe(TimeSpan.FromSeconds(4), 0.85));
        Assert.Null(first.EvictStart);
        Assert.True(Assert.Single(tracker.Observe(TimeSpan.FromSeconds(8), 0.1)).Replace);
        Assert.Empty(tracker.Observe(TimeSpan.FromSeconds(10), 0.83));

        var better = Assert.Single(tracker.Observe(TimeSpan.FromSeconds(11), 0.93));
        Assert.False(better.Replace);
        Assert.Equal(first.Start, better.EvictStart);
        Assert.Equal(TimeSpan.FromSeconds(10), better.Start);
        Assert.Equal(TimeSpan.FromSeconds(11), better.Best);
    }

    [Fact]
    public void SingleMatchGetsACompletionUpdateForNearbyFrames()
    {
        var tracker = new FrameSearchService.MatchGroupTracker(2, 0.82, 12);
        var initial = Assert.Single(tracker.Observe(TimeSpan.FromSeconds(4), 0.93));
        var completed = tracker.Complete();

        Assert.NotNull(completed);
        Assert.True(completed.Value.Replace);
        Assert.Equal(initial.Best, completed.Value.Best);
    }

    [Fact]
    public void NonSquareSampleAspectRatioUsesDisplayGeometryForMatching()
    {
        using var coded = new SKBitmap(320, 240);
        using var displayed = new SKBitmap(320, 180);
        using var original = CreatePicture(0, 95);
        using (var canvas = new SKCanvas(coded))
            canvas.DrawBitmap(original, new SKRect(0, 0, 320, 240));
        using (var canvas = new SKCanvas(displayed))
            canvas.DrawBitmap(original, new SKRect(0, 0, 320, 180));

        var expected = FrameVisualMatcher.Create(displayed);
        var corrected = FrameVisualMatcher.Create(coded, 16d / 9);
        var uncorrected = FrameVisualMatcher.Create(coded);

        Assert.True(FrameVisualMatcher.Similarity(expected, corrected) > 0.95);
        Assert.True(FrameVisualMatcher.Similarity(expected, corrected) >
                    FrameVisualMatcher.Similarity(expected, uncorrected) + 0.05);
    }

    private static SKBitmap CreatePicture(int variant, int jpegQuality)
    {
        using var pixels = new SKBitmap(320, 180);
        using (var canvas = new SKCanvas(pixels))
        {
            canvas.Clear(variant == 0 ? SKColors.DarkBlue : SKColors.DarkRed);
            using var paint = new SKPaint { Color = variant == 0 ? SKColors.Yellow : SKColors.Cyan };
            canvas.DrawRect(variant == 0 ? 36 : 180, 30, 90, 100, paint);
        }
        using var image = SKImage.FromBitmap(pixels);
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, jpegQuality);
        return SKBitmap.Decode(jpeg.ToArray());
    }

    private static SKBitmap CreateDarkBroadcastPicture(bool alternate)
    {
        var pixels = new SKBitmap(320, 180);
        using var canvas = new SKCanvas(pixels);
        canvas.Clear(alternate ? new SKColor(34, 12, 12) : new SKColor(5, 7, 12));
        using var paint = new SKPaint();
        paint.Color = SKColors.White;
        canvas.DrawRect(9, 8, 26, 8, paint); // 相同的台标。
        paint.Color = SKColors.Goldenrod;
        canvas.DrawRect(282, 151, 22, 14, paint);
        if (alternate)
        {
            paint.Color = new SKColor(115, 35, 15);
            canvas.DrawRect(43, 31, 230, 113, paint);
            paint.Color = new SKColor(165, 70, 30);
            canvas.DrawRect(118, 43, 77, 76, paint);
        }
        else
        {
            paint.Color = new SKColor(145, 17, 18);
            canvas.DrawRect(35, 128, 250, 8, paint);
            canvas.DrawRect(100, 104, 95, 5, paint);
            paint.Color = new SKColor(37, 54, 65);
            canvas.DrawLine(165, 17, 86, 123, paint);
        }
        return pixels;
    }

    private sealed class TestViewLocator : ViewLocator
    {
        public string NameFor(object viewModel) => GetViewName(viewModel);
    }
}
