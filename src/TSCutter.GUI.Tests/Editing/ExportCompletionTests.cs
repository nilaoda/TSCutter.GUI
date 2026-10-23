using System.Reflection;
using Avalonia.Headless;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.Avalonia;
using HanumanInstitute.MvvmDialogs.FileSystem;
using Splat;
using Xunit;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Tests.UI;
using TSCutter.GUI.Utils;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.ViewModels.Editing;

namespace TSCutter.GUI.Tests.Editing;

[Collection("Avalonia UI")]
public sealed class ExportCompletionTests
{
    [RemuxExportFact]
    public async Task MergeCompletionPublishesSharedOutputSizeAndSingleExportReplacesItsMarker()
    {
        var source = Path.Combine(TSCutter.GUI.Tests.FFmpeg.NativeTestEnvironment.FixtureDirectory("remux"), "multi.mp4");
        var merged = Path.Combine(Path.GetDirectoryName(source)!, "ui-merged.mp4");
        var single = Path.Combine(Path.GetDirectoryName(source)!, "ui-single.mp4");
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var locale = new LocalizationService();
            locale.SwitchLanguage("zh-CN");
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
            var manager = DispatchProxy.Create<IDialogManager, ExportDialogManager>();
            var stub = (ExportDialogManager)(object)manager;
            stub.OutputPath = merged;
            var dialogs = new DialogService(manager, viewModelFactory: type => Activator.CreateInstance(type)!);
            var vm = new MainWindowViewModel(dialogs, new ConfigurationService(locale)) { VideoPath = source };
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                var prepared = new VideoInstance(source);
                prepared.InitVideo();
                Assert.True(await (Task<bool>)typeof(MainWindowViewModel).GetMethod("LoadVideoAsync", flags)!.Invoke(vm, [prepared])!);
                var first = new PickedClip { InFileInfo = new FileInfo(source), RequiresRemux = true,
                    StartTime = 2, EndTime = 4, EndPosition = 1, IsSelected = true };
                var second = new PickedClip { InFileInfo = new FileInfo(source), RequiresRemux = true,
                    StartTime = 6, EndTime = 8, EndPosition = 1, IsSelected = true };
                vm.Clips.Add(first);
                vm.Clips.Add(second);
                vm.SelectedClip = first;
                await vm.MergeSelectedClipsCommand.ExecuteAsync(null);
                Assert.Equal(2, stub.LastSegmentCount);
                Assert.All(vm.Clips, clip =>
                {
                    Assert.Equal(ClipExportStatus.Done, clip.ExportStatus);
                    Assert.Equal(merged, clip.OutputFilePathStr);
                    Assert.Equal(LocalizationManager.Instance.String_Clips_Merged, clip.EstimatedSizeStr);
                });
                var size = LocalizationManager.Instance.String_SizePrefix + CommonUtil.FormatFileSize(new FileInfo(merged).Length);
                Assert.Equal(size, vm.SelectedClipEstimatedSizeStr);
                var capture = typeof(MainWindowViewModel).GetMethod("CaptureProject", flags)!;
                var project = (CutterProject)capture.Invoke(vm, [false])!;
                CutterProjectService.PopulateSources(project);
                var restored = CutterProjectService.Deserialize(CutterProjectService.Serialize(project));
                Assert.Equal(restored.Clips[0].MergedOutput, restored.Clips[1].MergedOutput);
                Assert.Equal(2, restored.Clips[0].MergedOutput!.ClipCount);
                vm.SelectClip(first);
                Assert.Equal(LocalizationManager.Instance.String_Clips_Merged, vm.SelectedClipEstimatedSizeStr);
                stub.OutputPath = single;
                await (Task)typeof(MainWindowViewModel).GetMethod("SaveVideoAsync", flags)!.Invoke(vm, null)!;
                Assert.Null(first.MergedOutput);
                Assert.Equal(LocalizationManager.Instance.String_SizePrefix + CommonUtil.FormatFileSize(new FileInfo(single).Length),
                    vm.SelectedClipEstimatedSizeStr);
                typeof(MainWindowViewModel).GetMethod("RecordHistory", flags)!.Invoke(vm, null);
                typeof(MainWindowViewModel).GetMethod("InvalidateClipExports", flags)!.Invoke(vm, null);
                vm.UndoCommand.Execute(null);
                Assert.All(vm.Clips, clip => Assert.Null(clip.OutputFileInfo));
            }
            finally { vm.Close(); }
            return true;
        }, CancellationToken.None);
    }
}

public class ExportDialogManager : DispatchProxy
{
    public string OutputPath { get; set; } = "";
    public int LastSegmentCount { get; private set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "ShowFrameworkDialogAsync" => Task.FromResult<object?>(new DesktopDialogStorageFile(OutputPath)),
        "ShowDialogAsync" => RunOutputAsync((OutputWindowViewModel)args![1]!),
        _ => throw new NotSupportedException(method.ToString())
    };

    private async Task<bool?> RunOutputAsync(OutputWindowViewModel output)
    {
        var request = (RemuxExportRequest?)typeof(OutputWindowViewModel).GetField("_mediaRequest", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(output);
        LastSegmentCount = request?.Segments.Count ?? 1;
        await output.OutputCommand.ExecuteAsync(null);
        Assert.Null(output.Exception);
        Assert.True(output.DialogResult);
        return output.DialogResult;
    }
}
