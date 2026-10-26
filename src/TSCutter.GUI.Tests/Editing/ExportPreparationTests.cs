using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.Avalonia;
using Splat;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Tests.UI;
using TSCutter.GUI.ViewModels;
using TSCutter.GUI.ViewModels.Editing;
using TSCutter.GUI.Views;
using TSCutter.GUI.Views.Editing;
using Xunit;

namespace TSCutter.GUI.Tests.Editing;

[Collection("Avalonia UI")]
public sealed class ExportPreparationTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData("fast")]
    [InlineData("slow")]
    [InlineData("cancel")]
    [InlineData("error")]
    public async Task PreparationOnlyShowsProgressForSlowWorkAndPropagatesItsResult(string mode)
    {
        await RunAsync(async (main, dialogs) =>
        {
            var completion = new TaskCompletionSource<bool>();
            var stopwatch = Stopwatch.StartNew();
            dialogs.OnPreparationShown = vm =>
            {
                Assert.True(stopwatch.Elapsed >= ExportPreparationWindowViewModel.DisplayDelay - TimeSpan.FromMilliseconds(50));
                if (mode == "cancel") vm.CancelCommand.Execute(null);
                else if (mode == "error") completion.SetException(new IOException("read failed"));
                else completion.SetResult(true);
            };
            Func<IProgress<ExportPreparationProgress>, CancellationToken, Task<bool>> work = (_, token) =>
                mode == "fast" ? Task.FromResult(true) : completion.Task.WaitAsync(token);
            var pending = (Task<bool>)typeof(MainWindowViewModel).GetMethod("RunExportPreparationAsync", PrivateInstance)!
                .MakeGenericMethod(typeof(bool)).Invoke(main, [work, null, null, true])!;
            if (mode == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            else if (mode == "error") await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            else Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(mode == "fast" ? 0 : 1, dialogs.PreparationCount);
        });
    }

    [Fact]
    public async Task LargeQueueAndWarningListsOnlyRealizeTheVisibleItems()
    {
        await RunAsync(async (main, _) =>
        {
            for (var i = 0; i < 5000; i++) main.ExportQueue.Add(new ExportQueueItem { SourceFileName = $"file-{i:0000}.ts" });
            typeof(MainWindowViewModel).GetMethod("MarkProjectCheckpoint", PrivateInstance)!.Invoke(main, null);
            var window = new MainWindow { DataContext = main, Width = 1000, Height = 720 };
            var warning = new TsExportWarningView
            {
                DataContext = new TsExportWarningViewModel { FileNames = main.ExportQueue.Select(item => item.SourceFileName).ToArray() }
            };
            try
            {
                var tabs = window.FindControl<TabControl>("RightTabControl")!;
                window.Show();
                Flush(window);
                double FooterBottom(object command)
                {
                    var button = window.GetVisualDescendants().OfType<Button>()
                        .Single(control => ReferenceEquals(control.Command, command));
                    return button.TranslatePoint(new Point(0, button.Bounds.Height), window)!.Value.Y;
                }
                var clipsFooter = FooterBottom(main.SelectAllClipsCommand);
                tabs.SelectedIndex = 1;
                Flush(window);
                Assert.Equal(clipsFooter, FooterBottom(main.ClearQueueCommand), 6);
                Assert.Equal(clipsFooter, FooterBottom(main.BatchExportQueueCommand), 6);
                CheckList(window.FindControl<ItemsControl>("ExportQueueList")!, window);
                main.ExportQueue.Clear();
                typeof(MainWindowViewModel).GetMethod("MarkProjectCheckpoint", PrivateInstance)!.Invoke(main, null);
                Flush(window);
                Assert.Equal(clipsFooter, FooterBottom(main.ClearQueueCommand), 6);
                Assert.Equal(clipsFooter, FooterBottom(main.BatchExportQueueCommand), 6);
                warning.Show();
                Flush(warning);
                CheckList(warning.FindControl<ItemsControl>("FilesList")!, warning);
            }
            finally { warning.Close(); window.Close(); }
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task QueueCompletionKeepsFailuresAndNewItemsAndPublishesOneUpdate()
    {
        await RunAsync(async (main, _) =>
        {
            var submitted = Enumerable.Range(0, 3).Select(_ => new ExportQueueItem()).ToArray();
            foreach (var item in submitted) main.ExportQueue.Add(item);
            var addedLater = new ExportQueueItem();
            main.ExportQueue.Add(addedLater);
            var output = new OutputWindowViewModel();
            output.CompletedTaskIndices.AddRange([0, 2]);
            output.FailedTaskIndices.Add(1);
            var notifications = 0;
            main.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(main.ExportQueueCount)) notifications++; };
            await (Task)typeof(MainWindowViewModel).GetMethod("ApplyQueueExportResultsAsync", PrivateInstance)!
                .Invoke(main, [submitted, output])!;
            Assert.Equal([submitted[1], addedLater], main.ExportQueue);
            Assert.Equal(ClipExportStatus.Failed, submitted[1].Status);
            Assert.Equal(ClipExportStatus.Pending, addedLater.Status);
            Assert.Equal(1, notifications);
        });
    }

    private static void CheckList(ItemsControl list, Window window)
    {
        Assert.Equal(5000, list.Items.Count);
        Assert.InRange(list.GetRealizedContainers().Count(), 1, 100);
        var scroll = list.GetVisualAncestors().OfType<ScrollViewer>().First();
        scroll.Offset = new Vector(0, scroll.Extent.Height);
        Flush(window);
        Assert.InRange(list.GetRealizedContainers().Count(), 1, 100);
        Assert.NotNull(list.ContainerFromIndex(4999));
    }

    private static void Flush(Window window)
    {
        for (var i = 0; i < 3; i++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    }

    private static async Task RunAsync(Func<MainWindowViewModel, PreparationDialogManager, Task> action)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            new App().Initialize();
            var manager = DispatchProxy.Create<IDialogManager, PreparationDialogManager>();
            var stub = (PreparationDialogManager)(object)manager;
            var dialogs = new DialogService(manager, viewModelFactory: type => Locator.Current.GetService(type));
            var main = new MainWindowViewModel(dialogs, new ConfigurationService(App.LocalizationService));
            try { await action(main, stub); }
            finally { main.Close(); }
            return true;
        }, CancellationToken.None);
    }
}

public class PreparationDialogManager : DispatchProxy
{
    public int PreparationCount { get; private set; }
    public Action<ExportPreparationWindowViewModel>? OnPreparationShown { get; set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name != "ShowDialogAsync" || args![1] is not ExportPreparationWindowViewModel vm)
            throw new NotSupportedException(method.ToString());
        PreparationCount++;
        OnPreparationShown?.Invoke(vm);
        return ObserveAsync(vm);
    }
    internal static async Task<bool?> ObserveAsync(ExportPreparationWindowViewModel vm)
    {
        await vm.ObserveCompletionAsync();
        return vm.DialogResult;
    }
}
