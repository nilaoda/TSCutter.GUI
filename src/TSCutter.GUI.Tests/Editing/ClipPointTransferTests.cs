using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Classic.Avalonia.Theme;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.Avalonia;
using HanumanInstitute.MvvmDialogs.FileSystem;
using HanumanInstitute.MvvmDialogs.FrameworkDialogs;
using Splat;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Tests.UI;
using TSCutter.GUI.Utils.Files;
using TSCutter.GUI.ViewModels;
using TSCutter.GUI.ViewModels.Editing;
using TSCutter.GUI.Views.Editing;
using Xunit;

namespace TSCutter.GUI.Tests.Editing;

[Collection("Avalonia UI")]
public sealed class ClipPointTransferTests
{
    [Fact]
    public async Task RoundTripPreservesOriginalBoundariesAndRecognizesRenamedMedia()
    {
        using var files = new Files();
        var file = files.Points();
        file.ClipMode = ClipListMode.Remove;
        file.Clips[0].StartPts = (1L << 33) - 90_000;
        file.Clips[0].EndFramePosition = 188;
        await ClipPointService.SaveAsync(files.Output, file);
        var loaded = await ClipPointService.LoadAsync(files.Output);
        Assert.Equal(file.ClipMode, loaded.ClipMode);
        Assert.Equal(file.Clips[0].StartPts, loaded.Clips[0].StartPts);
        Assert.Equal(file.Clips[0].EndPts, loaded.Clips[0].EndPts);
        Assert.Equal(file.Clips[0].StartPosition, loaded.Clips[0].StartPosition);
        Assert.Equal(file.Clips[0].EndPosition, loaded.Clips[0].EndPosition);
        Assert.Equal(188, loaded.Clips[0].EndFramePosition);
        var renamed = Path.Combine(files.Root.FullName, "renamed.ts");
        File.Copy(files.Source, renamed);
        File.Delete(files.Source);
        Assert.True(Matches(loaded, renamed));
        Assert.False(ClipPointService.Matches(loaded, renamed, new PreviewStreamSelection(1, 257, 1), 90_000, 1, 90_000));
        var bytes = File.ReadAllBytes(renamed);
        bytes[1024] ^= 1;
        File.WriteAllBytes(renamed, bytes);
        Assert.False(Matches(loaded, renamed));
    }

    [Fact]
    public async Task ValidationAndCancelledWritesKeepTheExistingFile()
    {
        using var files = new Files();
        var file = files.Points();
        await ClipPointService.SaveAsync(files.Output, file);
        var original = File.ReadAllBytes(files.Output);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClipPointService.SaveAsync(files.Output, file, new CancellationToken(true)));
        Assert.Equal(original, File.ReadAllBytes(files.Output));
        file.Version = 2;
        Assert.Throws<InvalidDataException>(() => ClipPointService.Validate(file));
        file.Version = 1;
        file.Clips[0].StartTime = double.NaN;
        Assert.Throws<InvalidDataException>(() => ClipPointService.Validate(file));
        file.Clips[0].StartTime = file.Clips[0].EndTime;
        Assert.True(Assert.Single(ClipPointService.Ranges(file, 20, 0)).InRange);
        file.Clips[0].EndFramePosition = file.Source.Length + 1;
        Assert.Throws<InvalidDataException>(() => ClipPointService.Validate(file));
        Assert.Empty(Directory.GetFiles(files.Root.FullName, "*.tmp"));
    }

    [Fact]
    public async Task RestrictedAccessUsesOnlyTheCurrentMediaAndUpdatesChosenOutputs()
    {
        using var files = new Files();
        var file = files.Points();
        var media = files.Root.CreateSubdirectory("media");
        var output = files.Root.CreateSubdirectory("output");
        var target = Path.Combine(media.FullName, "target.ts");
        File.Copy(files.Source, target);
        var policy = MediaFileAccess.Current;
        MediaFileAccess.ConfigureForProcess(new MediaFileAccess(new MediaWorkspace(media.FullName, output.FullName), error => error.ToString()));
        try
        {
            Assert.True(Matches(file, target));
            var path = Path.Combine(output.FullName, "points.tscutpoints");
            await ClipPointService.SaveAsync(path, file);
            Assert.NotNull(await ClipPointService.LoadAsync(path));
            file.Clips[0].Name = "Updated clip";
            await ClipPointService.SaveAsync(path, file);
            Assert.Equal("Updated clip", Assert.Single((await ClipPointService.LoadAsync(path)).Clips).Name);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ClipPointService.SaveAsync(Path.Combine(media.FullName, "points.tscutpoints"), file));
            Assert.Throws<UnauthorizedAccessException>(() => Matches(file, files.Source));
        }
        finally { MediaFileAccess.ConfigureForProcess(policy); }
    }

    [Fact]
    public async Task ImportPreviewShowsOffsetsBoundsAndModeConflictsInAllLanguages()
    {
        using var files = new Files();
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(() =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var locale = new LocalizationService();
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
            foreach (var language in new[] { "en-US", "zh-CN", "zh-TW" })
            {
                locale.SwitchLanguage(language);
                using var vm = new ClipPointImportViewModel();
                vm.Initialize(files.Points(), 3.99, 3.5, matches: true, ClipCutMode.KeyFrame, ClipListMode.Keep, 1);
                Assert.True(vm.Exact);
                vm.AlignCurrentCommand.Execute(null);
                Assert.Equal(1.5m, vm.OffsetSeconds);
                Assert.False(vm.Exact);
                Assert.False(vm.ConfirmCommand.CanExecute(null));
                Assert.Equal(locale.GetString("String.ClipPoints.OutOfRange"), vm.Entries[0].Status);
                vm.OffsetSeconds = -3;
                Assert.StartsWith("−", vm.Entries[0].Start);
                Assert.False(vm.ConfirmCommand.CanExecute(null));
                vm.OffsetSeconds = 0;
                Assert.True(vm.ConfirmCommand.CanExecute(null));
                var view = new ClipPointImportView { DataContext = vm, Width = 620 };
                try
                {
                    foreach (var theme in new[] { ClassicTheme.Standard, TSCutter.GUI.Themes.CustomTheme.Windows11Dark })
                    {
                        Avalonia.Application.Current!.RequestedThemeVariant = theme;
                        view.Show();
                        Dispatcher.UIThread.RunJobs();
                        view.UpdateLayout();
                        Assert.True(view.Bounds.Height >= 500);
                        Assert.NotNull(view.FindControl<NumericUpDown>("Offset"));
                    }
                }
                finally { view.Close(); }
                vm.Initialize(files.Points(), 20, 0, false, ClipCutMode.KeyFrame, ClipListMode.Remove, 1);
                Assert.True(vm.Replace);
                vm.Replace = false;
                Assert.False(vm.ConfirmCommand.CanExecute(null));
                Assert.Equal(locale.GetString("String.ClipPoints.ModeConflict"), vm.ConflictText);
                vm.Replace = true;
                Assert.True(vm.ConfirmCommand.CanExecute(null));

                // 替换的默认值随偏移重新判断：精确恢复改用文件中的智能剪辑模式时冲突，按时间转移时可以追加。
                var smart = files.Points();
                smart.CutMode = ClipCutMode.FrameAccurate;
                vm.Initialize(smart, 20, 0, matches: true, ClipCutMode.KeyFrame, ClipListMode.Keep, 1);
                Assert.True(vm.Exact);
                Assert.True(vm.Replace);
                vm.OffsetSeconds = 1;
                Assert.False(vm.Replace);
                Assert.True(vm.ConfirmCommand.CanExecute(null));
                vm.OffsetSeconds = 0;
                Assert.True(vm.Replace);
                // 用户自己改过之后不再自动调整。
                vm.Replace = false;
                vm.OffsetSeconds = 1;
                vm.OffsetSeconds = 0;
                Assert.False(vm.Replace);

                // 保存的智能剪辑模式不可用：只能按时间转移，并说明原因。
                vm.Initialize(smart, 20, 0, matches: false, ClipCutMode.KeyFrame, ClipListMode.Keep, 0, modeUnavailable: true);
                Assert.False(vm.Exact);
                Assert.Equal(ClipCutMode.KeyFrame, vm.ImportCutMode);
                Assert.StartsWith(locale.GetString("String.ClipPoints.ModeUnavailable"), vm.MethodHint);
                Assert.True(vm.ConfirmCommand.CanExecute(null));
            }
            return Task.FromResult(true);
        }, CancellationToken.None);
    }

    [NativeMediaTheory("frameaccurate")]
    [InlineData("h264.ts", ClipCutMode.KeyFrame)]
    [InlineData("h264.mp4", ClipCutMode.FrameAccurate)]
    public async Task TimeTransferResolvesTheTargetBoundaries(string name, ClipCutMode mode)
    {
        using var files = new Files();
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            var file = files.Points();
            var path = Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), name);
            var ranges = ClipPointService.Ranges(file, 10, 0.2);
            var mapped = Assert.Single(await ClipPointService.RelocateAsync(path, null, ranges, mode, null, CancellationToken.None));
            Assert.Equal("Opening", mapped.Name);
            Assert.True(mapped.NeedsBoundaryReview);
            Assert.True(mapped.EndPts > mapped.StartPts);
            Assert.True(mapped.EndTime >= mapped.StartTime);
            Assert.NotEqual(file.Clips[0].StartPts, mapped.StartPts);
            Assert.Equal(270_000, file.Clips[0].StartPts);
            if (mode == ClipCutMode.FrameAccurate)
            {
                Assert.InRange(mapped.StartTime, 2.15, 2.2 + 0.001);
                Assert.InRange(mapped.EndTime, 4.15, 4.2 + 0.001);
                Assert.True(mapped.FrameAccurate);
            }
            else
                Assert.True(mapped.EndPosition > mapped.StartPosition);
            return true;
        }, CancellationToken.None);
    }

    /// <summary>关键帧剪辑中短于关键帧间隔的片段：起止落在同一关键帧时，终点延到下一个关键帧，不产生零长度片段。</summary>
    [NativeMediaFact("frameaccurate")]
    public async Task KeyFrameTransferDoesNotCollapseShortClips()
    {
        using var files = new Files();
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            var file = files.Points();
            file.Clips[0].EndTime = file.Clips[0].StartTime + 0.04;
            var path = Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), "h264.ts");
            var ranges = ClipPointService.Ranges(file, 10, 0.2);
            var mapped = Assert.Single(await ClipPointService.RelocateAsync(path, null, ranges, ClipCutMode.KeyFrame, null, CancellationToken.None));
            Assert.True(mapped.EndPosition == -1 || mapped.EndPts > mapped.StartPts && mapped.EndPosition > mapped.StartPosition);
            Assert.True(mapped.EndTime > mapped.StartTime);
            Assert.True(mapped.EndTime >= ranges[0].End);
            return true;
        }, CancellationToken.None);
    }

    [NativeMediaFact("frameaccurate")]
    public async Task HostCommandsRestoreExactBoundariesAndUndoTheImportedModes()
    {
        using var files = new Files();
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var locale = new LocalizationService();
            locale.SwitchLanguage("en-US");
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
            var config = new RemoveModeWorkflowTests.MemoryConfiguration();
            Locator.CurrentMutable.RegisterConstant<IConfigurationService>(config);
            var manager = DispatchProxy.Create<IDialogManager, TransferDialogs>();
            var dialogs = (TransferDialogs)(object)manager;
            dialogs.Path = files.Output;
            var vm = new MainWindowViewModel(new DialogService(manager, viewModelFactory: type => Activator.CreateInstance(type)!), config);
            var source = Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), "h264.ts");
            var frames = FrameAccurateExportTests.DecodeFrames(source);
            try
            {
                vm.VideoPath = source;
                Assert.True(await LoadVideo(vm));
                vm.IsFrameAccurateMode = true;
                vm.IsRemoveMode = true;
                await vm.SeekToKeyFrameAndDrawFrameAsync(frames[47]);
                await vm.MarkClipStartShortcutCommand.ExecuteAsync(null);
                await vm.SeekToKeyFrameAndDrawFrameAsync(frames[91]);
                await vm.MarkClipEndShortcutCommand.ExecuteAsync(null);
                var original = Assert.Single(vm.Clips);
                var expected = (original.StartPts, original.EndPts, original.StartPosition, original.EndPosition, original.EndFramePosition);
                await vm.ExportClipPointsCommand.ExecuteAsync(null);
                Assert.True(File.Exists(files.Output));
                Assert.True(await LoadVideo(vm));
                vm.IsFrameAccurateMode = false;
                vm.IsRemoveMode = false;
                // 两个片段，只有后加的被选中：替换导入后撤销，未选中的片段也要带着缩略图恢复。
                await vm.AddClipCommand.ExecuteAsync(null);
                await vm.SeekToKeyFrameAndDrawFrameAsync(frames[47]);
                await vm.AddClipCommand.ExecuteAsync(null);
                var before = vm.Clips.Select(clip => clip.ClipID).ToArray();
                Assert.Equal(2, before.Length);
                Assert.All(vm.Clips, clip => Assert.NotNull(clip.StartThumbnail));
                await vm.ImportClipPointsCommand.ExecuteAsync(null);
                var imported = Assert.Single(vm.Clips);
                Assert.Equal(expected, (imported.StartPts, imported.EndPts, imported.StartPosition, imported.EndPosition, imported.EndFramePosition));
                Assert.Equal(ClipCutMode.FrameAccurate, vm.CutMode);
                Assert.True(vm.IsRemoveMode);
                Assert.False(imported.NeedsBoundaryReview);
                Assert.NotNull(imported.StartThumbnail);
                vm.UndoCommand.Execute(null);
                Assert.Equal(before, vm.Clips.Select(clip => clip.ClipID));
                Assert.All(vm.Clips, clip => Assert.NotNull(clip.StartThumbnail));
                Assert.Equal(ClipCutMode.KeyFrame, vm.CutMode);
                Assert.False(vm.IsRemoveMode);
                vm.RedoCommand.Execute(null);
                Assert.Equal(expected.StartPts, Assert.Single(vm.Clips).StartPts);
                Assert.NotNull(Assert.Single(vm.Clips).StartThumbnail);
                Assert.True(vm.IsRemoveMode);
                vm.UndoCommand.Execute(null);
                Assert.All(vm.Clips, clip => Assert.NotNull(clip.StartThumbnail));
                vm.RedoCommand.Execute(null);

                // 普通编辑的撤销不改变工具栏上的模式开关。
                vm.IsRemoveMode = false;
                await vm.AddClipCommand.ExecuteAsync(null);
                vm.UndoCommand.Execute(null);
                Assert.False(vm.IsRemoveMode);
                Assert.Equal(ClipCutMode.FrameAccurate, vm.CutMode);
                Assert.Single(vm.Clips);
            }
            finally
            {
                vm.Close();
                await vm.KeyFrameStrip.IdleTask;
                await (Task)typeof(MainWindowViewModel).GetField("previewDisposalTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
            }
            return true;
        }, CancellationToken.None);
    }

    private static Task<bool> LoadVideo(MainWindowViewModel vm) => (Task<bool>)typeof(MainWindowViewModel)
        .GetMethod("LoadVideoAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [null])!;

    private static bool Matches(ClipPointFile file, string path) =>
        ClipPointService.Matches(file, path, file.PreviewSelection, file.FirstFramePts, file.TimeBaseNumerator, file.TimeBaseDenominator);

    private sealed class Files : IDisposable
    {
        internal DirectoryInfo Root { get; } = Directory.CreateTempSubdirectory("tscutter-points-");
        internal string Source => System.IO.Path.Combine(Root.FullName, "source.ts");
        internal string Output => System.IO.Path.Combine(Root.FullName, "cuts.tscutpoints");
        internal Files() => File.WriteAllBytes(Source, Enumerable.Range(0, 2048).Select(index => (byte)index).ToArray());
        internal ClipPointFile Points() => new()
        {
            Source = CutterProjectService.Snapshot(Source), PreviewSelection = new PreviewStreamSelection(0, 256, 1),
            Duration = 20, FirstFramePts = 90_000, TimeBaseNumerator = 1, TimeBaseDenominator = 90_000,
            Clips = [new ProjectClip { Name = "Opening", StartTime = 2, EndTime = 4,
                StartPts = 270_000, EndPts = 450_000, StartPosition = 0, EndPosition = 376 }]
        };
        public void Dispose() => Root.Delete(recursive: true);
    }

    public class TransferDialogs : DispatchProxy
    {
        public string Path { get; set; } = "";
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "ShowFrameworkDialogAsync")
                return Task.FromResult<object?>(args!.Any(value => value is OpenFileDialogSettings)
                    ? new[] { new DesktopDialogStorageFile(Path) } : new DesktopDialogStorageFile(Path));
            if (method.Name == "ShowDialogAsync" && args![1] is ClipPointImportViewModel import)
            {
                import.Replace = true;
                import.ConfirmCommand.Execute(null);
                Assert.True(import.DialogResult);
                return Task.FromResult<bool?>(true);
            }
            if (method.Name == "ShowDialogAsync" && args![1] is ExportPreparationWindowViewModel progress)
                return Complete(progress);
            throw new InvalidOperationException($"Unexpected dialog: {method.Name}");
        }

        private static async Task<bool?> Complete(ExportPreparationWindowViewModel vm)
        {
            await vm.ObserveCompletionAsync();
            return vm.DialogResult;
        }
    }
}
