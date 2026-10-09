using TSCutter.GUI.Models;
using TSCutter.GUI.Services;
using TSCutter.GUI.Utils;
using TSCutter.GUI.ViewModels;
using Xunit;

namespace TSCutter.GUI.Tests;

public sealed class TsBatchCheckTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"ts-batch-{Guid.NewGuid():N}");

    public TsBatchCheckTests() => Directory.CreateDirectory(_folder);
    public void Dispose() => Directory.Delete(_folder, true);

    [Fact]
    public void FolderDropUsesNaturalOrderAndDoesNotRecurse()
    {
        var second = WriteTs("clip2.ts");
        var tenth = WriteTs("clip10.TS");
        File.WriteAllText(Path.Combine(_folder, "other.mp4"), "ignored");
        Directory.CreateDirectory(Path.Combine(_folder, "nested"));
        File.WriteAllText(Path.Combine(_folder, "nested", "hidden.ts"), "ignored");
        Assert.Equal(new[] { second, tenth }, TsFileDropHelper.ExpandPaths([_folder]));
    }

    [Fact]
    public async Task QueueDeduplicatesFilesAndContinuesAfterAReadFailure()
    {
        var first = WriteTs("first.ts");
        var second = WriteTs("second.ts");
        var vm = CreateViewModel();
        try
        {
            await vm.AddFilesAsync([first, first, second]);
            Assert.Equal(2, vm.Files.Count);
            File.Delete(first);
            await vm.StartCommand.ExecuteAsync(null);
            Assert.Equal(TsBatchCheckState.Failed, vm.Files[0].State);
            Assert.Equal(TsBatchCheckState.Completed, vm.Files[1].State);
            var result = vm.Files[1].Result;
            Assert.NotNull(result);
            Assert.Equal(100, vm.Percent);
            Assert.False(vm.CanStart);
            await vm.StartCommand.ExecuteAsync(null);
            Assert.Same(result, vm.Files[1].Result);
        }
        finally { vm.OnClosed(); }
    }

    [Fact]
    public async Task StopPreservesCompletedFilesAndResumeRestartsOnlyUnfinishedFiles()
    {
        var vm = CreateViewModel();
        try
        {
            await vm.AddFilesAsync([WriteTs("first.ts"), WriteTs("second.ts"), WriteTs("third.ts")]);
            var second = vm.Files[1];
            var stopped = false;
            second.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(second.State) && second.State == TsBatchCheckState.Scanning && !stopped)
                {
                    stopped = true;
                    vm.StopCommand.Execute(null);
                }
            };
            await vm.StartCommand.ExecuteAsync(null);
            var firstResult = vm.Files[0].Result;
            Assert.NotNull(firstResult);
            Assert.Equal(TsBatchCheckState.Cancelled, second.State);
            Assert.Equal(TsBatchCheckState.Waiting, vm.Files[2].State);
            await vm.StartCommand.ExecuteAsync(null);
            Assert.Same(firstResult, vm.Files[0].Result);
            Assert.All(vm.Files, item => Assert.Equal(TsBatchCheckState.Completed, item.State));
        }
        finally { vm.OnClosed(); }
    }

    [Fact]
    public async Task ClosingCancelsTheActiveScanAndPreventsFurtherFilesBeingAdded()
    {
        var file = WriteTs("source.ts");
        var vm = CreateViewModel();
        await vm.AddFilesAsync([file]);
        var item = vm.Files[0];
        item.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(item.State) && item.State == TsBatchCheckState.Scanning)
                vm.OnClosed();
        };
        await vm.StartCommand.ExecuteAsync(null);
        await vm.AddFilesAsync([file]);
        Assert.Empty(vm.Files);
        Assert.False(vm.IsScanning);
        Assert.Null(item.Result);
    }

    [Fact]
    public async Task ConcurrentLogWritersPreserveExistingFilesAndProduceDistinctReports()
    {
        var source = WriteTs("video.ts");
        var existing = source + ".check.log";
        File.WriteAllText(existing, "original report");
        var paths = await Task.WhenAll(
            TsBatchCheckReportWriter.SaveLogAsync(source, null, "first report"),
            TsBatchCheckReportWriter.SaveLogAsync(source, null, "second report"));
        Assert.Equal(2, paths.Distinct().Count());
        Assert.Equal("original report", File.ReadAllText(existing));
        Assert.Equal("first report", File.ReadAllText(paths[0]));
        Assert.Equal("second report", File.ReadAllText(paths[1]));
        Assert.Equal(188 * 8, new FileInfo(source).Length);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            TsBatchCheckReportWriter.SaveLogAsync(source, Path.Combine(_folder, "missing"), "report"));
    }

    [Fact]
    public void CsvEscapesQuotesNewlinesAndFormulaPrefixes()
    {
        Assert.Equal("\"a,\"\"b\"\"\nc\"", TsBatchCheckReportWriter.CsvCell("a,\"b\"\nc"));
        Assert.Equal("\"'=1+1\"", TsBatchCheckReportWriter.CsvCell("=1+1"));
    }

    private string WriteTs(string name)
    {
        var path = Path.Combine(_folder, name);
        var data = new byte[188 * 8];
        for (var index = 0; index < 8; index++)
        {
            data[index * 188] = 0x47;
            data[index * 188 + 1] = 0x1f;
            data[index * 188 + 2] = 0xff;
            data[index * 188 + 3] = 0x10;
        }
        File.WriteAllBytes(path, data);
        return path;
    }

    private static TsBatchCheckWindowViewModel CreateViewModel() => new(null!, new TestLocalization(), new TestConfiguration());

    private sealed class TestConfiguration : IConfigurationService
    {
        public AppConfig CurrentConfig { get; } = new();
        public void Load() { }
        public void Save() { }
        public void ApplyTheme(string theme) { }
        public void ApplyLanguage(string language) { }
    }

    private sealed class TestLocalization : ILocalizationService
    {
        public List<SupportedLang> SupportedLanguages { get; } = [];
        public string CurrentLanguageCode => "en-US";
        public string GetString(string key) => key;
        public void SwitchLanguage(string code) => LanguageChanged?.Invoke();
        public event Action? LanguageChanged;
    }
}
