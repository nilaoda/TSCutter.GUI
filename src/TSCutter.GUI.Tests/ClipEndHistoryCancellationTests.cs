using System.Reflection;
using Avalonia;
using Splat;
using TSCutter.GUI.Models;
using TSCutter.GUI.Services;
using TSCutter.GUI.ViewModels;
using Xunit;

namespace TSCutter.GUI.Tests;

[CollectionDefinition("Editor history", DisableParallelization = true)]
public sealed class EditorHistoryCollection;

[Collection("Editor history")]
public sealed class ClipEndHistoryCancellationTests
{
    [Fact]
    public void UndoAndRedoCancelPendingEndScansAndRefreshMarkCommands()
    {
        // 仅注册控件所需服务，不启动窗口、原生解码器或加载用户配置。
        var app = new Application();
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

    private const BindingFlags PrivateMembers = BindingFlags.Instance | BindingFlags.NonPublic;

    private static IDisposable BindApplication(Application app)
    {
        // 参考程序集隐藏了注册接口，测试通过运行时接口建立可恢复的服务作用域。
        var locatorType = typeof(AvaloniaObject).Assembly.GetType("Avalonia.AvaloniaLocator")!;
        var scope = (IDisposable)locatorType.GetMethod("EnterScope")!.Invoke(null, null)!;
        var locator = locatorType.GetProperty("CurrentMutable")!.GetValue(null)!;
        var binding = locator.GetType().GetMethod("Bind")!.MakeGenericMethod(typeof(Application)).Invoke(locator, null)!;
        var toConstant = binding.GetType().GetMethod("ToConstant")!;
        if (toConstant.IsGenericMethodDefinition)
            toConstant = toConstant.MakeGenericMethod(typeof(Application));
        toConstant.Invoke(binding, [app]);
        return scope;
    }

    private static void Set(MainWindowViewModel vm, string name, object value) =>
        typeof(MainWindowViewModel).GetField(name, PrivateMembers)!.SetValue(vm, value);
    private static object? Get(MainWindowViewModel vm, string name) =>
        typeof(MainWindowViewModel).GetField(name, PrivateMembers)!.GetValue(vm);
}
