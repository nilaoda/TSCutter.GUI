using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Splat;
using TSCutter.GUI.Models.Media;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Services.Media;
using TSCutter.GUI.Utils.Media;
using TSCutter.GUI.ViewModels.Media;
using TSCutter.GUI.Views.Media;
using Xunit;

namespace TSCutter.GUI.Tests.UI;

[Collection("Avalonia UI")]
public sealed class ChannelMergeWindowTests
{
    private static readonly ChannelMergeProgramInfo Program = new(1, "Service01",
    [
        new ChannelMergeTrackInfo(0x101, 1, "PID 0x0101 · MP2", "", 48000),
        new ChannelMergeTrackInfo(0x102, 2, "PID 0x0102 · MP2", "", 48000),
        new ChannelMergeTrackInfo(0x103, 3, "PID 0x0103 · MP2", "", 48000)
    ]);

    [Fact]
    public async Task WindowShowsMappingAnalysisAndOutputsInEveryLanguage()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(async () =>
        {
            var languages = new LocalizationService();
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(languages);
            foreach (var (language, title, swapped, suggestion) in new[]
                     {
                         ("zh-CN", "节目 1 · Service01", "C ← 右 · LFE ← 左", "建议的映射与当前不同"),
                         ("en-US", "Program 1 · Service01", "C ← right · LFE ← left", "The suggested mapping differs from the current one"),
                         ("zh-TW", "節目 1 · Service01", "C ← 右 · LFE ← 左", "建議的對應與目前不同")
                     })
            {
                languages.SwitchLanguage(language);
                var viewModel = new ChannelMergeWindowViewModel();
                // 分析结果：0x102 的左声道只有低频（LFE 在左）。
                var levels = new[]
                {
                    Track(0x101, 0.07, 0.07, false, false),
                    Track(0x102, 0.0005, 0.02, true, false),
                    Track(0x103, 0.02, 0.02, false, false)
                };
                viewModel.Initialize(Program, ChannelMergeDefaults.Create(Program), (_, selected, _, _) =>
                    Task.FromResult(ChannelMergeAnalyzer.Conclude(levels, selected)));
                // 新文件默认不合并；三个槽位按 PID 顺序。
                Assert.False(viewModel.Merge);
                Assert.False(viewModel.Result.HasWork);
                Assert.Equal(new[] { 0x101, 0x102, 0x103 }, viewModel.Slots.Select(slot => slot.SelectedTrack.Value));
                viewModel.Merge = true;
                Assert.True(viewModel.Result.HasWork);
                // 默认一行 AC3 5.1，448 kbps；合并没有来源列。
                var row = Assert.Single(viewModel.Outputs.Rows);
                Assert.Equal(448, row.SelectedBitrate!.Value);
                Assert.False(row.ShowSource);

                await viewModel.AnalyzeCommand.ExecuteAsync(null);
                Assert.True(viewModel.ShowVerdictSuggest);
                viewModel.ApplySuggestionCommand.Execute(null);
                Assert.True(viewModel.Slots[1].Swap);
                Assert.True(viewModel.ShowVerdictGood);

                var window = new ChannelMergeWindow { DataContext = viewModel };
                try
                {
                    window.Show();
                    Flush(window);
                    var texts = window.GetVisualDescendants().OfType<TextBlock>()
                        .Where(text => text.IsEffectivelyVisible).Select(text => text.Text ?? "").ToArray();
                    Assert.Contains(title, texts);
                    Assert.Contains(swapped, texts);
                    Assert.DoesNotContain(suggestion, texts);

                    // 两个槽位选同一条音轨：提示并且不能确定。
                    viewModel.Slots[2].SelectedTrack = viewModel.Slots[2].Tracks[0];
                    Assert.True(viewModel.HasDuplicateSlots);
                    Assert.False(viewModel.CanConfirm);
                    Assert.False(viewModel.Result.HasWork);
                }
                finally
                {
                    window.Close();
                }
            }
        }, CancellationToken.None);
    }

    private static ChannelMergeTrackLevel Track(int pid, double left, double right, bool leftLow, bool rightLow) =>
        new(pid, true, new ChannelMergeChannelLevel(left * 4, left, leftLow ? 0.001 : 0.5, 4),
            new ChannelMergeChannelLevel(right * 4, right, rightLow ? 0.001 : 0.5, 4), 0.2, null);

    private static void Flush(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
