using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Splat;
using TSCutter.GUI.Models.Media;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Utils.Media.DolbyE;
using TSCutter.GUI.ViewModels.Media;
using TSCutter.GUI.Views.Media;
using Xunit;

namespace TSCutter.GUI.Tests.UI;

[Collection("Avalonia UI")]
public sealed class DolbyEOutputWindowTests
{
    [Fact]
    public async Task WindowShowsSummaryOutputsAndHintsInEveryLanguage()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var languages = new LocalizationService();
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(languages);
            var info = new DolbyETrackInfo(3, 0x24, "eng", new DolbyEFrameHeader(20, false, 0, 3), 0);
            foreach (var (language, summary, flacHint) in new[]
                     {
                         ("zh-CN", "20 位 · 25 fps · 配置 0：5.1 + 2.0", "FLAC 不能放进 TS"),
                         ("en-US", "20-bit · 25 fps · configuration 0: 5.1 + 2.0", "FLAC cannot be stored in TS"),
                         ("zh-TW", "20 位元 · 25 fps · 配置 0：5.1 + 2.0", "FLAC 無法放進 TS")
                     })
            {
                languages.SwitchLanguage(language);
                var viewModel = new DolbyEOutputWindowViewModel();
                viewModel.Initialize([info], new Dictionary<int, DolbyETrackSettings>(), 0x24);
                var track = viewModel.SelectedTrack!;
                // 新文件默认不处理；切到转码时使用预先生成的默认输出行。
                Assert.False(track.Transcode);
                Assert.False(track.ToSettings().HasWork);
                track.Transcode = true;
                // 默认按声道组各一行 AC3：5.1 为 448 kbps，立体声 192 kbps。
                Assert.Equal(2, track.Rows.Count);
                Assert.Equal([448, 192], track.Rows.Select(row => row.SelectedBitrate!.Value));
                track.AddRowCommand.Execute(null);
                var added = track.Rows[^1];
                added.SelectedCodec = added.Codecs.Single(choice => choice.Value == DolbyECodec.Flac);
                Assert.True(added.IsLossless);
                Assert.True(track.ShowFlacHint);

                var window = new DolbyEOutputWindow { DataContext = viewModel };
                try
                {
                    window.Show();
                    Flush(window);
                    var texts = window.GetVisualDescendants().OfType<TextBlock>()
                        .Where(text => text.IsEffectivelyVisible).Select(text => text.Text ?? "").ToArray();
                    Assert.Contains(summary, texts);
                    Assert.Contains(texts, text => text.StartsWith(flacHint, StringComparison.Ordinal));
                    Assert.Equal(3, window.GetVisualDescendants().OfType<ComboBox>()
                        .Count(box => box.IsEffectivelyVisible && box.ItemsSource is IReadOnlyList<DolbyEChoice<DolbyECodec>>));

                    // 改回不处理：输出设置保留，只是不生效。
                    track.IsKeepAsIs = true;
                    Assert.False(track.ToSettings().HasWork);
                    Assert.Equal(3, track.ToSettings().Outputs.Count);
                }
                finally
                {
                    window.Close();
                }
            }
        }, CancellationToken.None);
    }

    private static void Flush(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
