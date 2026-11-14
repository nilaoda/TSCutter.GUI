using Avalonia.Headless;
using Splat;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Services.Media;
using TSCutter.GUI.Tests.UI;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.ViewModels.Editing;
using Xunit;

namespace TSCutter.GUI.Tests.Editing;

/// <summary>
/// 导出设置中的静音检测：只有音轨参与检测；检测后显示峰值或“静音”，静音的音轨取消勾选（不能更改的轨道保持不变），
/// 用户可以再勾上；打不开解码器的音轨注明无法检测；杜比 E 按声道组列出，全部声道组静音才取消勾选。关闭对话框会取消检测。
/// </summary>
[Collection("Avalonia UI")]
public sealed class SilenceDetectionDialogTests
{
    [Fact]
    public Task MarksAndUnchecksSilentTracks() => Run(async strings =>
    {
        var dialog = Dialog();
        Assert.False(dialog.CanDetectSilence);
        IReadOnlyCollection<int>? requested = null;
        dialog.EnableSilenceDetection((indices, _, _) =>
        {
            requested = indices;
            return Task.FromResult<IReadOnlyList<AudioTrackLevel>>(
            [
                new(1, true, 0.12), new(2, true, 0), new(3, true, 0.0003), new(4, false, 0), new(5, true, 0),
                new(6, true, 0.5, [new(1, "5.1", 0.5), new(2, "2.0", 0)]),
                new(7, true, 0.0002, [new(1, "5.1", 0.0002), new(2, "2.0", 0)])
            ]);
        });
        Assert.True(dialog.CanDetectSilence);
        await dialog.DetectSilenceCommand.ExecuteAsync(null);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], requested);
        Assert.False(dialog.IsDetectingSilence);
        var tracks = dialog.Tracks;
        // 第 5 条音轨不能更改：标为静音但保持勾选；无法检测的第 4 条与部分声道组静音的杜比 E 也保持勾选。
        Assert.Equal([true, true, false, false, true, true, true, false], tracks.Select(track => track.IsSelected));
        Assert.Equal([false, false, true, true, false, true, false, true], tracks.Select(track => track.IsSilent));
        Assert.Equal("", tracks[0].LevelText);
        Assert.Equal(string.Format(strings.String_SilenceDetect_Peak, (-18.4).ToString("F1")), tracks[1].LevelText);
        Assert.Equal(strings.String_SilenceDetect_SilentEmpty, tracks[2].LevelText);
        Assert.Equal(string.Format(strings.String_SilenceDetect_Silent, (-70.5).ToString("F1")), tracks[3].LevelText);
        Assert.Equal(strings.String_SilenceDetect_Undecodable, tracks[4].LevelText);
        var group = (int number, string layout, string value) =>
            string.Format(strings.String_SilenceDetect_DolbyEGroup, number, layout, value);
        Assert.Equal(string.Format(strings.String_SilenceDetect_DolbyE, string.Join(" · ",
            group(1, "5.1", string.Format(strings.String_SilenceDetect_GroupPeak, (-6.0).ToString("F1"))),
            group(2, "2.0", strings.String_SilenceDetect_SilentShort))), tracks[6].LevelText);
        Assert.Equal(string.Format(strings.String_SilenceDetect_DolbyE, string.Join(" · ",
            group(1, "5.1", strings.String_SilenceDetect_SilentShort),
            group(2, "2.0", strings.String_SilenceDetect_SilentShort))), tracks[7].LevelText);
        Assert.Equal(string.Format(strings.String_SilenceDetect_Found, 4, 3), dialog.SilenceStatus);
        tracks[2].IsSelected = true;
        Assert.Equal([0, 1, 2, 4, 5, 6], dialog.Apply(new RemuxExportSettings { Container = "mkv" }).StreamIndices);
    });

    [Fact]
    public Task ClosingTheDialogCancelsDetection() => Run(async _ =>
    {
        var dialog = Dialog();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.EnableSilenceDetection(async (_, _, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return [];
        });
        var running = dialog.DetectSilenceCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(dialog.IsDetectingSilence);
        Assert.False(dialog.DetectSilenceCommand.CanExecute(null));
        dialog.CancelCommand.Execute(null);
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(dialog.IsDetectingSilence);
        Assert.Equal("", dialog.SilenceStatus);
        Assert.All(dialog.Tracks, track => Assert.True(track.IsSelected));
    });

    /// <summary>在无头界面会话中以中文界面文字运行。</summary>
    private static async Task Run(Func<LocalizationManager, Task> body)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var locale = new LocalizationService();
            locale.SwitchLanguage("zh-CN");
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
            await body(LocalizationManager.Instance);
            return true;
        }, CancellationToken.None);
    }

    private static RemuxExportOptionsViewModel Dialog()
    {
        var dialog = new RemuxExportOptionsViewModel();
        dialog.Initialize(new RemuxExportSettings { Container = "mkv" },
        [
            new() { Index = 0, IsSelected = true, CanChange = false, Description = "video" },
            new() { Index = 1, IsSelected = true, AudioStreamIndex = 1, Description = "loud" },
            new() { Index = 2, IsSelected = true, AudioStreamIndex = 2, Description = "empty" },
            new() { Index = 3, IsSelected = true, AudioStreamIndex = 3, Description = "noise" },
            new() { Index = 4, IsSelected = true, AudioStreamIndex = 4, Description = "unsupported" },
            new() { Index = 5, IsSelected = true, AudioStreamIndex = 5, CanChange = false, Description = "locked" },
            new() { Index = 6, IsSelected = true, AudioStreamIndex = 6, Description = "Dolby E, one group silent" },
            new() { Index = 7, IsSelected = true, AudioStreamIndex = 7, Description = "Dolby E, all groups silent" }
        ], false);
        return dialog;
    }
}
