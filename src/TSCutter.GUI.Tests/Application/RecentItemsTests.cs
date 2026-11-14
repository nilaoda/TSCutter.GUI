using System.Reflection;
using System.Text.Json;
using Avalonia.Headless;
using Splat;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Models.Discs;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Services.Discs;
using TSCutter.GUI.Tests.Editing;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Tests.UI;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.ViewModels;
using TSCutter.GUI.ViewModels.Discs;
using Xunit;

namespace TSCutter.GUI.Tests.Application;

/// <summary>
/// 最近打开列表：同一路径只保留一项并移到最前、最多 10 项、配置读写，
/// 光盘重新打开时预选上次的标题，以及打开视频后记录、关闭记录时不记录。
/// </summary>
[Collection("Avalonia UI")]
public sealed class RecentItemsTests
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "tscutter-recent");

    private static RecentItem Video(string name, int minutesAgo = 0) => new()
    {
        Kind = RecentItemKind.Video,
        Path = Path.Combine(Folder, name),
        LastOpenedUtc = DateTime.UtcNow.AddMinutes(-minutesAgo)
    };

    [Fact]
    public void ReopeningMovesTheItemToTheFrontAndTheListKeepsTenItems()
    {
        var list = new List<RecentItem>();
        for (var index = 0; index < 12; index++)
            list = RecentItemList.Record(list, Video($"clip{index}.ts"));
        Assert.Equal(RecentItemList.MaxCount, list.Count);
        Assert.Equal("clip11.ts", Path.GetFileName(list[0].Path));
        Assert.DoesNotContain(list, item => Path.GetFileName(item.Path) is "clip0.ts" or "clip1.ts");

        // Windows 与 macOS 上只差大小写的路径视为同一个文件。
        var reopened = Video("CLIP5.TS");
        var expectSame = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        list = RecentItemList.Record(list, expectSame ? reopened : Video("clip5.ts"));
        Assert.Equal(RecentItemList.MaxCount, list.Count);
        Assert.Single(list, item => RecentItemList.SamePath(item.Path, Path.Combine(Folder, "clip5.ts")));
        Assert.True(RecentItemList.SamePath(list[0].Path, Path.Combine(Folder, "clip5.ts")));

        list = RecentItemList.Remove(list, Path.Combine(Folder, "clip5.ts") + Path.DirectorySeparatorChar);
        Assert.DoesNotContain(list, item => RecentItemList.SamePath(item.Path, Path.Combine(Folder, "clip5.ts")));
    }

    [Fact]
    public void SavedListIsCleanedWhenReadAndKeepsDiscAndProjectDetails()
    {
        var json = JsonSerializer.Serialize(new AppConfig
        {
            RecentItems =
            [
                Video("old.ts", minutesAgo: 30),
                new RecentItem { Kind = RecentItemKind.Video, Path = "  ", LastOpenedUtc = DateTime.UtcNow },
                Video("old.ts", minutesAgo: 60),
                new RecentItem
                {
                    Kind = RecentItemKind.Disc, Path = Path.Combine(Folder, "DISC"), DiscKind = DiscKind.BluRay,
                    DiscInputPath = Path.Combine(Folder, "DISC"), DiscTitle = 800, LastOpenedUtc = DateTime.UtcNow
                },
                new RecentItem
                {
                    Kind = RecentItemKind.Project, Path = Path.Combine(Folder, "match.tscut"), ClipCount = 12,
                    LastOpenedUtc = DateTime.UtcNow.AddMinutes(-5)
                }
            ]
        }, AppJsonContext.Default.AppConfig);

        var restored = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppConfig)!;
        Assert.True(restored.RememberRecentItems);
        // 沿用保存的顺序；重复的路径只保留靠前的一项。
        Assert.Equal([RecentItemKind.Video, RecentItemKind.Disc, RecentItemKind.Project],
            restored.RecentItems.Select(item => item.Kind));
        Assert.True(restored.RecentItems[0].LastOpenedUtc > DateTime.UtcNow.AddMinutes(-45));
        Assert.Equal((DiscKind?)DiscKind.BluRay, restored.RecentItems[1].DiscKind);
        Assert.Equal(800, restored.RecentItems[1].DiscTitle);
        Assert.Equal(12, restored.RecentItems[2].ClipCount);
        Assert.Empty(JsonSerializer.Deserialize("{}", AppJsonContext.Default.AppConfig)!.RecentItems);
    }

    [Fact]
    public async Task ReopenedDiscPreselectsThePreviousTitle()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(new LocalizationService());
            var root = Path.Combine(Folder, "DISC");
            DiscTitleInfo Title(int number, bool recommended) =>
                new(number, TimeSpan.FromMinutes(number), 0, 1, 1, null, [], [], recommended);
            var scan = new DiscScanResult(DiscKind.BluRay, "DISC", [Title(1, true), Title(800, false)],
                [Input(1), Input(800)], 0);

            var dialog = new DiscTitleSelectionViewModel();
            dialog.Initialize(root, scan, (root, 800));
            Assert.Equal(800, dialog.SelectedTitle!.Number);
            // 上次的标题已不在列表中时仍选推荐标题。
            dialog.Initialize(root, scan, (root, 5));
            Assert.Equal(1, dialog.SelectedTitle!.Number);
            dialog.Initialize(root, scan);
            Assert.Equal(1, dialog.SelectedTitle!.Number);
            return true;

            DiscInput Input(int number) => new(DiscKind.BluRay, root, number, 1, TimeSpan.FromMinutes(number), []);
        }, CancellationToken.None);
    }

    [NativeMediaFact("frameaccurate")]
    public async Task OpeningAVideoRecordsItUnlessRecordingIsOff()
    {
        var directory = NativeTestEnvironment.FixtureDirectory("frameaccurate");
        var ts = Path.Combine(directory, "h264.ts");
        var mp4 = Path.Combine(directory, "h264.mp4");
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await headless.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(new LocalizationService());
            var configuration = new RemoveModeWorkflowTests.MemoryConfiguration();
            var vm = RemoveModeWorkflowTests.CreateViewModel(configuration);
            var open = typeof(MainWindowViewModel).GetMethod("OpenVideoPathAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            try
            {
                // 没有打开文件时在预览区显示打开提示。
                await vm.RefreshRecentItemsAsync();
                Assert.True(vm.ShowRecentStartPage);
                Assert.Empty(vm.RecentItems);

                await (Task)open.Invoke(vm, [ts])!;
                Assert.True(vm.IsVideoInitialized);
                var recorded = Assert.Single(configuration.CurrentConfig.RecentItems);
                Assert.Equal(RecentItemKind.Video, recorded.Kind);
                Assert.True(RecentItemList.SamePath(ts, recorded.Path));

                await vm.RefreshRecentItemsAsync();
                var row = Assert.Single(vm.RecentItems);
                Assert.False(row.IsMissing);
                Assert.NotEmpty(row.Detail);
                // 菜单项带数字访问键，文件名里的下划线已转义。
                Assert.StartsWith("_1 ", row.MenuHeader);
                Assert.Contains(Path.GetFileName(ts).Replace("_", "__"), row.MenuHeader);
                // 已打开文件时不显示打开提示。
                Assert.False(vm.ShowRecentStartPage);

                configuration.CurrentConfig.RememberRecentItems = false;
                await (Task)open.Invoke(vm, [mp4])!;
                Assert.True(vm.IsVideoInitialized);
                Assert.Single(configuration.CurrentConfig.RecentItems);
            }
            finally
            {
                vm.Close();
            }
            return true;
        }, CancellationToken.None);
    }
}
