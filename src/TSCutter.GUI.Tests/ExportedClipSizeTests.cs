using System.Reflection;
using Avalonia.Headless;
using Splat;
using TSCutter.GUI.Models;
using TSCutter.GUI.Services;
using TSCutter.GUI.Utils;
using TSCutter.GUI.ViewModels;
using Xunit;

namespace TSCutter.GUI.Tests;

[Collection("Avalonia UI")]
public sealed class ExportedClipSizeTests
{
    [Theory]
    [InlineData(-1L, false)]
    [InlineData(500L, true)]
    public async Task RestoringAGrowingSourceKeepsOnlyCurrentExportSizes(long endPosition, bool current)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tscutter-growth-size-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "recording.ts");
            var output = Path.Combine(directory, "previous.ts");
            File.WriteAllBytes(source, new byte[1200]);
            File.WriteAllBytes(output, new byte[100]);
            using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
            await session.Dispatch<bool>(async () =>
            {
                using var resolver = new ModernDependencyResolver();
                using var scope = resolver.WithResolver();
                var locale = new LocalizationService();
                locale.SwitchLanguage("zh-CN");
                Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
                var vm = new MainWindowViewModel(null!, new ConfigurationService(locale)) { DurationMax = 60 };
                var project = new CutterProject
                {
                    Source = new ProjectSource { Path = source, Length = 1000 }, SelectedClipIndex = 0,
                    Clips = [new ProjectClip { EndPosition = endPosition, EndTime = 30, OutputFilePath = output, WasExported = true }]
                };
                await (Task)typeof(MainWindowViewModel).GetMethod("RestoreProjectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(vm, [project])!;
                Assert.Equal(current, vm.Clips[0].OutputFileInfo is not null);
                Assert.Equal(current ? ClipExportStatus.Done : ClipExportStatus.Pending, vm.Clips[0].ExportStatus);
                Assert.True(File.Exists(output));
                vm.Close();
                return true;
            }, CancellationToken.None);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedOutputSizeReplacesTheEstimateInClipAndSelection(bool remux)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tscutter-size-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source.mp4");
            var output = Path.Combine(directory, "output.mp4");
            File.WriteAllBytes(source, new byte[4096]);
            File.WriteAllBytes(output, new byte[1024]);
            using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
            await session.Dispatch(() =>
            {
                using var resolver = new ModernDependencyResolver();
                using var scope = resolver.WithResolver();
                var locale = new LocalizationService();
                locale.SwitchLanguage("zh-CN");
                Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
                var vm = new MainWindowViewModel(null!, new ConfigurationService(locale));
                using var video = new VideoInstance(source);
                typeof(VideoInstance).GetProperty(nameof(VideoInstance.Inited))!.SetValue(video, true);
                typeof(VideoInstance).GetProperty(nameof(VideoInstance.CanRemuxClip))!.SetValue(video, remux);
                typeof(MainWindowViewModel).GetField("_videoInstance", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, video);
                using var clip = new PickedClip { InFileInfo = new FileInfo(source), RequiresRemux = remux };
                vm.Clips.Add(clip);
                vm.SelectClip(clip);
                Assert.Equal(remux, vm.IsRemuxExport);
                var before = vm.SelectedClipEstimatedSizeStr;
                var selectionChanged = new List<string?>();
                vm.PropertyChanged += (_, args) => selectionChanged.Add(args.PropertyName);
                var changed = new List<string?>();
                clip.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
                clip.OutputFileInfo = new FileInfo(output);
                var expected = LocalizationManager.Instance.String_SizePrefix + CommonUtil.FormatFileSize(1024);
                Assert.NotEqual(before, expected);
                Assert.Equal(expected, clip.EstimatedSizeStr);
                Assert.Equal(expected, clip.ExportSizeStr);
                Assert.Equal(expected, vm.SelectedClipEstimatedSizeStr);
                Assert.Contains(nameof(MainWindowViewModel.SelectedClipEstimatedSizeStr), selectionChanged);
                Assert.Contains(nameof(PickedClip.EstimatedSizeStr), changed);
                Assert.Contains(nameof(PickedClip.ExportSizeStr), changed);
                clip.OutputFileInfo = new FileInfo(Path.Combine(directory, "missing.mp4"));
                Assert.Null(clip.OutputFileSizeStr);
                Assert.Equal(before, vm.SelectedClipEstimatedSizeStr);
                clip.OutputFileInfo = new FileInfo(output);
                clip.ExportStatus = ClipExportStatus.Done;
                clip.StartTime = 1;
                Assert.Null(clip.OutputFileInfo);
                Assert.Equal(ClipExportStatus.Pending, clip.ExportStatus);
                Assert.Equal(before, vm.SelectedClipEstimatedSizeStr);
                Assert.True(File.Exists(output));
                clip.OutputFileInfo = new FileInfo(output);
                clip.EndPosition = 2048;
                Assert.Null(clip.OutputFileInfo);
                Assert.Equal(remux ? before : LocalizationManager.Instance.String_SizePrefix + CommonUtil.FormatFileSize(2048),
                    vm.SelectedClipEstimatedSizeStr);
                vm.Close();
            }, CancellationToken.None);
        }
        finally { Directory.Delete(directory, true); }
    }
}
