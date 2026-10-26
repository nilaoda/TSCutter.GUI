using System.Reflection;
using Avalonia;
using Splat;
using TSCutter.GUI.ViewModels;
using Xunit;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Utils;
using TSCutter.GUI.Utils.Editing;

namespace TSCutter.GUI.Tests.Editing;

[CollectionDefinition("Editor history", DisableParallelization = true)]
public sealed class EditorHistoryCollection;

[Collection("Editor history")]
public sealed class ClipEndHistoryCancellationTests
{
    [Fact]
    public void SeekingKeepsMarkCommandsAvailableAndMarksTheDisplayedFrame()
    {
        using var resolver = new ModernDependencyResolver();
        using var resolverScope = resolver.WithResolver();
        var app = new Avalonia.Application();
        using var scope = BindApplication(app);
        app.RegisterServices();
        var locale = new LocalizationService();
        Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
        var path = Path.Combine(Path.GetTempPath(), $"clip-seek-{Guid.NewGuid():N}.ts");
        try
        {
            File.WriteAllBytes(path, new byte[1000]);
            var vm = new MainWindowViewModel(null!, new ConfigurationService(locale))
            {
                VideoPath = path, DurationMax = 60
            };
            using var video = new VideoInstance(path);
            typeof(VideoInstance).GetProperty(nameof(VideoInstance.Inited))!.SetValue(video, true);
            typeof(VideoInstance).GetProperty(nameof(VideoInstance.CanBinaryClip))!.SetValue(video, true);
            Set(vm, "_videoInstance", video);
            void PublishFrame(double seconds, long position, long pts) =>
                typeof(MainWindowViewModel).GetMethod("ApplyDecodeResult", PrivateMembers)!.Invoke(vm,
                    [new DecodeResult { FrameTimestamp = TimeSpan.FromSeconds(seconds), FramePosition = position, FramePts = pts }]);
            PublishFrame(1, 188, 90_000);
            vm.CurrentTime = 15; // seek 目标已移动，但新画面尚未发布。
            vm.DecodingOpCount = 1;
            Assert.True(vm.AddClipCommand.CanExecute(null));
            Assert.False(vm.MarkClipStartCommand.CanExecute(null));
            Assert.False(vm.MarkClipEndCommand.CanExecute(null));
            vm.AddClipCommand.Execute(null);
            var clip = Assert.Single(vm.Clips);
            Assert.Equal((1d, 188L, 90_000L), (clip.StartTime, clip.StartPosition, clip.StartPts));
            var marks = new System.Windows.Input.ICommand[]
            {
                vm.AddClipCommand, vm.MarkClipStartCommand, vm.MarkClipEndCommand,
                vm.MarkClipStartShortcutCommand, vm.MarkClipEndShortcutCommand
            };
            // 连续 seek 和重叠的预览请求都不应改变按钮的启用状态。
            foreach (var count in new[] { 0, 1, 2, 1, 0, 1 })
            {
                vm.DecodingOpCount = count;
                Assert.All(marks, command => Assert.True(command.CanExecute(null)));
                Assert.True(vm.RemoveClipCommand.CanExecute(null));
            }
            PublishFrame(2, 376, 180_000);
            vm.CurrentTime = 20;
            vm.MarkClipStartCommand.Execute(null);
            Assert.Equal((2d, 376L, 180_000L), (clip.StartTime, clip.StartPosition, clip.StartPts));

            Set(vm, "_isLoadingVideo", true);
            Assert.All(marks, command => Assert.False(command.CanExecute(null)));
            Set(vm, "_isLoadingVideo", false);
            Set(vm, "_isResolvingClipEnd", true);
            Assert.All(marks, command => Assert.False(command.CanExecute(null)));
            Set(vm, "_isResolvingClipEnd", false);
            PublishFrame(3, -1, 270_000);
            Assert.All(marks, command => Assert.False(command.CanExecute(null)));
            PublishFrame(3, 564, long.MinValue);
            Assert.All(marks, command => Assert.False(command.CanExecute(null)));
            PublishFrame(3, 564, 270_000);
            Assert.All(marks, command => Assert.True(command.CanExecute(null)));
            typeof(VideoInstance).GetProperty(nameof(VideoInstance.CanBinaryClip))!.SetValue(video, false);
            Assert.All(marks, command => Assert.False(command.CanExecute(null)));
            vm.Close();
            Assert.All(marks, command => Assert.False(command.CanExecute(null)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UndoAndRedoCancelPendingEndScansAndRefreshMarkCommands()
    {
        // 仅注册控件所需服务，不启动窗口、原生解码器或加载用户配置。
        var app = new Avalonia.Application();
        using var scope = BindApplication(app);
        app.RegisterServices();
        var previousLocale = Locator.Current.GetService<ILocalizationService>();
        var locale = new LocalizationService();
        Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
        var path = Path.Combine(Path.GetTempPath(), $"clip-history-{Guid.NewGuid():N}.ts");
        try
        {
            File.WriteAllBytes(path, new byte[1000]);
            var vm = new MainWindowViewModel(null!, new ConfigurationService(locale));
            using var video = new VideoInstance(path);
            typeof(VideoInstance).GetProperty(nameof(VideoInstance.Inited))!.SetValue(video, true);
            typeof(VideoInstance).GetProperty(nameof(VideoInstance.CanBinaryClip))!.SetValue(video, true);
            Set(vm, "_videoInstance", video);
            Set(vm, "_displayedFramePosition", 188L);
            Set(vm, "_displayedFramePts", 90_000L);
            var clip = new PickedClip
            {
                InFileInfo = new FileInfo(path), StartTime = 0, EndTime = 1,
                StartPosition = 0, EndPosition = 188, IsSelected = true
            };
            vm.Clips.Add(clip);
            vm.SelectedClip = clip;
            typeof(MainWindowViewModel).GetMethod("RecordHistory", PrivateMembers)!.Invoke(vm, null);
            clip.EndTime = 2;
            clip.EndPosition = 376;
            var notifications = 0;
            vm.MarkClipStartCommand.CanExecuteChanged += (_, _) => notifications++;

            using var firstScan = new CancellationTokenSource();
            Set(vm, "_clipEndCancellation", firstScan);
            Set(vm, "_isResolvingClipEnd", true);
            Assert.False(vm.MarkClipStartCommand.CanExecute(null));
            vm.UndoCommand.Execute(null);
            Assert.True(firstScan.IsCancellationRequested);
            Assert.Null(Get(vm, "_clipEndCancellation"));
            Assert.Equal(false, Get(vm, "_isResolvingClipEnd"));
            Assert.Same(clip, vm.Clips[0]);
            Assert.Equal(1, clip.EndTime);
            Assert.Equal(188, clip.EndPosition);
            Assert.True(vm.MarkClipStartCommand.CanExecute(null));
            Assert.True(notifications > 0);

            using var secondScan = new CancellationTokenSource();
            Set(vm, "_clipEndCancellation", secondScan);
            Set(vm, "_isResolvingClipEnd", true);
            vm.RedoCommand.Execute(null);
            Assert.True(secondScan.IsCancellationRequested);
            Assert.Null(Get(vm, "_clipEndCancellation"));
            Assert.Equal(false, Get(vm, "_isResolvingClipEnd"));
            Assert.Same(clip, vm.Clips[0]);
            Assert.Equal(2, clip.EndTime);
            Assert.Equal(376, clip.EndPosition);
            Assert.True(vm.MarkClipStartCommand.CanExecute(null));
            vm.Close();
        }
        finally
        {
            File.Delete(path);
            Locator.CurrentMutable.UnregisterAll<ILocalizationService>();
            if (previousLocale is not null)
                Locator.CurrentMutable.RegisterConstant(previousLocale);
        }
    }

    [Fact]
    public void ClipLabelsParticipateInUndoRedoAndProjectDirtyState()
    {
        using var resolver = new ModernDependencyResolver();
        using var resolverScope = resolver.WithResolver();
        var app = new Avalonia.Application();
        using var scope = BindApplication(app);
        app.RegisterServices();
        var locale = new LocalizationService();
        Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
        var path = Path.Combine(Path.GetTempPath(), $"clip-labels-{Guid.NewGuid():N}.ts");
        try
        {
            File.WriteAllBytes(path, new byte[1000]);
            var vm = new MainWindowViewModel(null!, new ConfigurationService(locale)) { VideoPath = path, DurationMax = 60 };
            using var video = new VideoInstance(path);
            typeof(VideoInstance).GetProperty(nameof(VideoInstance.Inited))!.SetValue(video, true);
            typeof(VideoInstance).GetProperty(nameof(VideoInstance.CanBinaryClip))!.SetValue(video, true);
            Set(vm, "_videoInstance", video);
            var clip = new PickedClip
            {
                InFileInfo = new FileInfo(path), StartTime = 0, EndTime = 1,
                StartPosition = 0, EndPosition = 188, IsSelected = true
            };
            vm.Clips.Add(clip);
            vm.SelectedClip = clip;
            Set(vm, "_projectPath", path + ".tscut");
            typeof(MainWindowViewModel).GetMethod("MarkProjectCheckpoint", PrivateMembers)!.Invoke(vm, null);
            Assert.False(vm.HasUnsavedProjectChanges);

            vm.UpdateClipDetails(clip, " Opening ", " Keep this shot ", "purple");
            Assert.Equal(("Opening", "Keep this shot", "purple"), (clip.Name, clip.Notes, clip.Color));
            Assert.Equal("purple", Assert.Single(vm.SelectedClipRanges).Color);
            Assert.True(vm.HasUnsavedProjectChanges);
            vm.UndoCommand.Execute(null);
            Assert.Equal(("", "", ""), (clip.Name, clip.Notes, clip.Color));
            Assert.False(vm.HasUnsavedProjectChanges);
            vm.RedoCommand.Execute(null);
            Assert.Equal(("Opening", "Keep this shot", "purple"), (clip.Name, clip.Notes, clip.Color));
            Assert.True(vm.HasUnsavedProjectChanges);
            vm.Close();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ClipOutputNamesUseListPositionsAndDisplayedEndTimes()
    {
        using var resolver = new ModernDependencyResolver();
        using var resolverScope = resolver.WithResolver();
        var app = new Avalonia.Application();
        using var scope = BindApplication(app);
        app.RegisterServices();
        var locale = new LocalizationService();
        Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
        var config = new ConfigurationService(locale);
        config.CurrentConfig.ClipOutputTemplate = "{index}_{name}_{end}";
        var vm = new MainWindowViewModel(null!, config);
        var first = new PickedClip { InFileInfo = new FileInfo("Recording.ts"), EndTime = 60 };
        var second = new PickedClip
        {
            InFileInfo = first.InFileInfo, Name = "Opening", EndTime = 20.5, EndPosition = 188
        };
        vm.Clips.Add(first);
        vm.Clips.Add(second);
        string OutputName(PickedClip clip) => (string)typeof(MainWindowViewModel)
            .GetMethod("CreateClipOutputName", PrivateMembers)!.Invoke(vm, [clip])!;
        Assert.Equal("001_clip001_00.01.00.000.ts", OutputName(first));
        Assert.Equal("002_Opening_00.00.20.500.ts", OutputName(second));
        vm.Clips.Remove(first);
        Assert.Equal("001_Opening_00.00.20.500.ts", OutputName(second));
        config.CurrentConfig.ClipOutputTemplate = ClipOutputNaming.DefaultTemplate;
        vm.Clips.Insert(0, first);
        Assert.Equal($"Recording_({CommonUtil.FormatSeconds(first.StartTime, true)}-" +
                     $"{CommonUtil.FormatSeconds(first.EndTime, true)}).ts", OutputName(first));
        vm.Clips.Remove(first);
        first.Dispose();
        vm.Close();
    }

    private const BindingFlags PrivateMembers = BindingFlags.Instance | BindingFlags.NonPublic;

    private static IDisposable BindApplication(Avalonia.Application app)
    {
        // 参考程序集隐藏了注册接口，测试通过运行时接口建立可恢复的服务作用域。
        var locatorType = typeof(AvaloniaObject).Assembly.GetType("Avalonia.AvaloniaLocator")!;
        var scope = (IDisposable)locatorType.GetMethod("EnterScope")!.Invoke(null, null)!;
        var locator = locatorType.GetProperty("CurrentMutable")!.GetValue(null)!;
        var binding = locator.GetType().GetMethod("Bind")!.MakeGenericMethod(typeof(Avalonia.Application)).Invoke(locator, null)!;
        var toConstant = binding.GetType().GetMethod("ToConstant")!;
        if (toConstant.IsGenericMethodDefinition)
            toConstant = toConstant.MakeGenericMethod(typeof(Avalonia.Application));
        toConstant.Invoke(binding, [app]);
        return scope;
    }

    private static void Set(MainWindowViewModel vm, string name, object value) =>
        typeof(MainWindowViewModel).GetField(name, PrivateMembers)!.SetValue(vm, value);
    private static object? Get(MainWindowViewModel vm, string name) =>
        typeof(MainWindowViewModel).GetField(name, PrivateMembers)!.GetValue(vm);
}
