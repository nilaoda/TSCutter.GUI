using System.Reflection;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.Avalonia;
using Avalonia.Headless;
using Splat;
using TSCutter.GUI.ViewModels;
using Xunit;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Tests.UI;
using TSCutter.GUI.Utils;
using TSCutter.GUI.Utils.Application;

namespace TSCutter.GUI.Tests.Editing;

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
    public async Task MergedTsClipsKeepTheirRangeSizesWhileTheSelectionShowsTheActualTotal(bool remux)
    {
        using var files = new TableProbeData.Files(new byte[4096]);
        File.WriteAllBytes(files.Output, new byte[512 + 1024 + 376]);
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var locale = new LocalizationService();
            locale.SwitchLanguage("zh-CN");
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
            new App().Initialize();
            var dialogs = new DialogService(DispatchProxy.Create<IDialogManager, PreparationDialogManager>(),
                viewModelFactory: type => Locator.Current.GetService(type));
            var main = new MainWindowViewModel(dialogs, new ConfigurationService(locale));
            var group = new MergedClipOutput(Guid.NewGuid().ToString("N"), 2);
            var first = new PickedClip { InFileInfo = new FileInfo(files.Source), RequiresRemux = remux,
                StartPosition = 0, EndPosition = 512, EndTime = 1, IsSelected = true,
                MergedOutput = group, OutputFileInfo = new FileInfo(files.Output) };
            var second = new PickedClip { InFileInfo = new FileInfo(files.Source), RequiresRemux = remux,
                StartPosition = 1024, EndPosition = 2048, StartTime = 2, EndTime = 3, IsSelected = true,
                MergedOutput = group, OutputFileInfo = new FileInfo(files.Output) };
            main.Clips.Add(first);
            main.Clips.Add(second);
            main.SelectedClip = first;
            try
            {
                var expectedFirst = remux ? LocalizationManager.Instance.String_Clips_Merged
                    : LocalizationManager.Instance.String_SizePrefix + CommonUtil.FormatFileSize(512);
                var expectedSecond = remux ? LocalizationManager.Instance.String_Clips_Merged
                    : LocalizationManager.Instance.String_SizePrefix + CommonUtil.FormatFileSize(1024);
                Assert.Equal(expectedFirst, first.EstimatedSizeStr);
                Assert.Equal(expectedSecond, second.EstimatedSizeStr);
                Assert.Equal(LocalizationManager.Instance.String_SizePrefix + CommonUtil.FormatFileSize(1912),
                    main.SelectedClipEstimatedSizeStr);
                main.SelectClip(first);
                Assert.Equal(expectedFirst, main.SelectedClipEstimatedSizeStr);
                first.ExportStatus = second.ExportStatus = ClipExportStatus.Done;
                File.Delete(files.Output);
                await (Task)typeof(MainWindowViewModel).GetMethod("ForgetMissingClipOutputsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(main, [new[] { files.Output }])!;
                Assert.All(new[] { first, second }, clip =>
                {
                    Assert.Null(clip.OutputFileInfo);
                    Assert.Null(clip.MergedOutput);
                    Assert.Equal(ClipExportStatus.Pending, clip.ExportStatus);
                });
                var project = (CutterProject)typeof(MainWindowViewModel)
                    .GetMethod("CaptureProject", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [false])!;
                project.Source = new ProjectSource { Path = files.Source };
                CutterProjectService.PopulateSources(project);
                Assert.Equal(2, CutterProjectService.Deserialize(CutterProjectService.Serialize(project)).Clips.Count);
            }
            finally { main.Close(); }
            return true;
        }, CancellationToken.None);
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
