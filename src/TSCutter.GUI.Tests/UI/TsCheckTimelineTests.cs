using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Splat;
using TSCutter.GUI.Controls;
using TSCutter.GUI.Models.TransportStream.Analysis;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.ViewModels.TransportStream.Analysis;
using TSCutter.GUI.Views.TransportStream.Analysis;
using Xunit;

namespace TSCutter.GUI.Tests.UI;

[Collection("Avalonia UI")]
public sealed class TsCheckTimelineTests
{
    [Fact]
    public async Task TimelineSelectsAvPayloadWhenAvailableAndFallsBackToTotalTs()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(new LocalizationService());

            var viewModel = new TsCheckWindowViewModel(null!) { IsTimelineView = true };
            viewModel.Initialize(CreateResult(184));
            var window = new TsCheckWindow { DataContext = viewModel };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var chart = Assert.Single(window.GetVisualDescendants().OfType<TsCheckTimeline>());
                Assert.Equal(TsCheckTimelineStreamView.AvPayloadSeries, chart.SelectedPid);
                Assert.Contains(viewModel.TimelineStreams, series => series.Pid == TsCheckTimelineStreamView.TotalTsSeries);

                viewModel.Initialize(CreateResult(0));
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(TsCheckTimelineStreamView.TotalTsSeries, chart.SelectedPid);
                Assert.Single(viewModel.TimelineStreams);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static TsCheckResult CreateResult(double avPayloadBytes)
    {
        var result = new TsCheckResult { FilePath = "sample.ts", FileSize = 376 };
        result.Timeline.Add(new TsCheckTimelineBucket
        {
            StartSeconds = 0,
            DurationSeconds = 1,
            TotalPacketCount = 2,
            AvPayloadBytes = avPayloadBytes,
            Segment = 0
        });
        return result;
    }
}
