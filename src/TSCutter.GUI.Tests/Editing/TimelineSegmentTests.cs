using System.Reflection;
using Avalonia.Headless;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.Avalonia;
using Splat;
using Xunit;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Tests.UI;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.Tests.Editing;

[Collection("Avalonia UI")]
public sealed class TimelineSegmentTests
{
    [RemuxExportFact]
    public async Task MatroskaChaptersBecomeSegmentsThatAddNamedClips()
    {
        var source = Path.Combine(TSCutter.GUI.Tests.FFmpeg.NativeTestEnvironment.FixtureDirectory("remux"), "multi.mkv");
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var locale = new LocalizationService();
            locale.SwitchLanguage("zh-CN");
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
            var dialogs = new DialogService(DispatchProxy.Create<IDialogManager, ExportDialogManager>());
            var vm = new MainWindowViewModel(dialogs, new ConfigurationService(locale)) { VideoPath = source };
            try
            {
                var prepared = new VideoInstance(source);
                prepared.InitVideo();
                Assert.True(await (Task<bool>)typeof(MainWindowViewModel)
                    .GetMethod("LoadVideoAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [prepared])!);
                // 章节 0–3s、3–8s，最后一章到视频结尾。
                Assert.Equal(["First chapter", "Second chapter"], vm.TimelineSegments.Select(segment => segment.Title));
                Assert.Equal(3, vm.TimelineSegments[1].StartSeconds);
                Assert.True(vm.TimelineSegments[1].EndsAtEnd);
                Assert.Equal("章节：2 个", vm.TimelineSegmentSummaryText);

                // 按章节添加片段：以章节名命名，终点按当前剪辑模式落在章节结束处，章节时间精确，不提示核对。
                await vm.AddClipFromSegmentCommand.ExecuteAsync(vm.TimelineSegments[0]);
                var clip = Assert.Single(vm.Clips);
                Assert.Equal("First chapter", clip.Name);
                Assert.Equal(0, clip.StartTime);
                Assert.InRange(clip.EndTime, 3, 4.1);
                Assert.False(clip.NeedsBoundaryReview);
            }
            finally
            {
                vm.Close();
            }
            return true;
        }, CancellationToken.None);
    }
}
