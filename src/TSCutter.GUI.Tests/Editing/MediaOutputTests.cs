using System.Runtime.InteropServices;
using System.Reflection;
using Avalonia.Headless;
using Splat;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Tests.UI;
using TSCutter.GUI.ViewModels.Editing;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Utils;
using TSCutter.GUI.Utils.Files;
using Xunit;

namespace TSCutter.GUI.Tests.Editing;

[Collection("Avalonia UI")]
public sealed class MediaOutputTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("tscutter-media-output-");
    private readonly string source;
    private readonly string target;
    private readonly MediaFileAccess restricted;

    public MediaOutputTests()
    {
        var input = root.CreateSubdirectory("media").FullName;
        var output = root.CreateSubdirectory("output").FullName;
        source = Path.Combine(input, "source.ts");
        target = Path.Combine(output, "output.ts");
        File.WriteAllBytes(source, [1, 2, 3, 4]);
        File.WriteAllText(target, "old");
        restricted = new MediaFileAccess(new MediaWorkspace(input, output), error => error.ToString());
    }

    public void Dispose() => root.Delete(recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyValidatesBeforeReplacingAndCreatesNewFile(bool remote)
    {
        var previous = MediaFileAccess.Current;
        if (remote)
        {
            MediaFileAccess.ConfigureForProcess(restricted);
            restricted.GrantOverwrite(target);
        }
        try
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                CommonUtil.CopyFileAsync(new FileInfo(source), target, start: -1));
            Assert.Equal("old", File.ReadAllText(target));
            await CommonUtil.CopyFileAsync(new FileInfo(source), target);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(target));
            if (remote)
                restricted.GrantOverwrite(source);
            await Assert.ThrowsAsync<IOException>(() => CommonUtil.CopyFileAsync(new FileInfo(source), source));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(source));
            if (remote)
                await Assert.ThrowsAsync<IOException>(() => CommonUtil.CopyFileAsync(new FileInfo(source), target));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(target));
        }
        finally { MediaFileAccess.ConfigureForProcess(previous); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task QueueReturnsApprovalOnlyWhenWritingHasNotStarted(bool writingStarted, bool cancel)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var locale = new LocalizationService();
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
            locale.SwitchLanguage("en-US");
            var previous = MediaFileAccess.Current;
            MediaFileAccess.ConfigureForProcess(restricted);
            try
            {
                var approval = new OverwriteApproval(target);
                var task = new BatchTask(source, target, 0, 4) { OverwriteApproval = approval };
                var failed = await Run(async _ =>
                {
                    if (writingStarted)
                    {
                        using var output = restricted.OpenOutput(target, sources: [source]);
                        output.WriteByte(9);
                        throw new IOException("Interrupted write");
                    }
                    if (cancel)
                        throw new OperationCanceledException();
                    await CommonUtil.CopyFileAsync(new FileInfo(source), target, start: -1);
                });
                Assert.Empty(failed.CompletedTaskIndices);
                Assert.Equal(!writingStarted, approval.Allows(target));
                Assert.False(restricted.CanOverwrite(target));
                if (writingStarted)
                {
                    Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(target));
                    Assert.Throws<IOException>(() => restricted.OpenOutput(target));
                }
                else
                {
                    Assert.Equal("old", File.ReadAllText(target));
                    var retried = await Run(_ => CommonUtil.CopyFileAsync(new FileInfo(source), target));
                    Assert.Null(retried.Exception);
                    Assert.Equal([0], retried.CompletedTaskIndices);
                    Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(target));
                    Assert.False(approval.Allows(target));
                    Assert.False(restricted.CanOverwrite(target));
                }
                return true;

                async Task<OutputWindowViewModel> Run(Func<BatchTask, Task> write)
                {
                    var output = new OutputWindowViewModel { RunBatchTaskAsync = write };
                    output.SetBatchTasks([task]);
                    await (Task)typeof(OutputWindowViewModel).GetMethod("RunBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(output, null)!;
                    return output;
                }
            }
            finally { MediaFileAccess.ConfigureForProcess(previous); }
        }, CancellationToken.None);
    }

    [Fact]
    public void ClearingOverwriteGrantsRevokesEveryRegisteredPath()
    {
        restricted.GrantOverwrite(source);
        restricted.GrantOverwrite(target);
        restricted.ClearOverwriteGrants();
        Assert.False(restricted.CanOverwrite(source));
        Assert.False(restricted.CanOverwrite(target));
        Assert.Throws<IOException>(() => restricted.OpenOutput(target));
        Assert.Equal("old", File.ReadAllText(target));
    }

    [Fact]
    public void TemporaryMediaOutputReleasesOldFileBeforeWritingAndRejectsRacingOutput()
    {
        restricted.GrantOverwrite(target);
        Assert.Throws<IOException>(() => restricted.BeginMediaOutput(target, [target]));
        Assert.Equal("old", File.ReadAllText(target));
        restricted.BeginMediaOutput(target, [source]);
        Assert.False(File.Exists(target));
        var temporary = target + ".tmp";
        File.WriteAllText(temporary, "new");
        restricted.PublishOutput(temporary, target, overwrite: false);
        Assert.Equal("new", File.ReadAllText(target));
        Assert.Throws<IOException>(() => restricted.BeginMediaOutput(target, [source]));

        restricted.GrantOverwrite(target);
        restricted.BeginMediaOutput(target, [source]);
        File.WriteAllText(temporary, "next");
        File.WriteAllText(target, "another account");
        Assert.Throws<IOException>(() => restricted.PublishOutput(temporary, target, overwrite: false));
        Assert.Equal("another account", File.ReadAllText(target));
    }

    [Fact]
    public async Task FailedSmallFileWritePreservesOriginalAndConsumesConfirmation()
    {
        restricted.GrantOverwrite(source);
        await Assert.ThrowsAsync<IOException>(() => restricted.WriteOutputAsync(source,
            stream => stream.WriteAsync(new byte[] { 9 }).AsTask(), sources: [source]));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(source));
        restricted.GrantOverwrite(target);
        await Assert.ThrowsAsync<IOException>(() => restricted.WriteOutputAsync(target, async stream =>
        {
            await stream.WriteAsync(new byte[] { 1, 2 });
            throw new IOException("Interrupted write");
        }));
        Assert.Equal("old", File.ReadAllText(target));
        Assert.False(restricted.CanOverwrite(target));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(target)!));
        await Assert.ThrowsAsync<IOException>(() => restricted.WriteOutputAsync(target,
            stream => stream.WriteAsync(new byte[] { 3, 4 }).AsTask()));
        Assert.Equal("old", File.ReadAllText(target));
        restricted.GrantOverwrite(target);
        await restricted.WriteOutputAsync(target, stream => stream.WriteAsync(new byte[] { 3, 4 }).AsTask());
        Assert.Equal(new byte[] { 3, 4 }, File.ReadAllBytes(target));
    }

    [Fact]
    public void SpaceCheckCreditsOnlyConfirmedReplacementWithoutConsumingIt()
    {
        var previous = MediaFileAccess.Current;
        MediaFileAccess.ConfigureForProcess(restricted);
        try
        {
            var task = new ExportSizeTask(target, source,
                [new ExportSizeSegment(0, 4, 0, 1)],
                ExportSizeAccuracy.Exact);
            DiskSpaceInfo? Probe(string _) => new("A", "A:",
                ExportSpaceCheck.ReserveBytes + 1);
            Assert.Single(ExportSpaceCheck.FindShortages([task], null, Probe, _ => "A"));
            restricted.GrantOverwrite(target);
            Assert.Empty(ExportSpaceCheck.FindShortages([task], null, Probe, _ => "A"));
            Assert.True(restricted.CanOverwrite(target));
            Assert.True(restricted.TryConsumeOverwrite(target));
            var approval = new OverwriteApproval(target);
            restricted.ClearOverwriteGrants();
            Assert.Empty(ExportSpaceCheck.FindShortages([task with { OverwriteApproval = approval }], null, Probe, _ => "A"));
            Assert.True(approval.Allows(target));
            Assert.False(restricted.CanOverwrite(target));
        }
        finally { MediaFileAccess.ConfigureForProcess(previous); }
    }

    [HardLinkFact]
    public void OverwritingHardLinkLeavesLinkedSourceUnchanged()
    {
        File.Delete(target);
        Assert.True(CreateLink(target, source));
        restricted.GrantOverwrite(target);
        using (var writer = new StreamWriter(restricted.OpenOutput(target, sources: [source])))
            writer.Write("new");
        Assert.Equal("new", File.ReadAllText(target));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(source));
    }

    public sealed class HardLinkFactAttribute : FactAttribute
    {
        public HardLinkFactAttribute()
        {
            var directory = Directory.CreateTempSubdirectory("tscutter-hardlink-probe-");
            try
            {
                var original = Path.Combine(directory.FullName, "original");
                File.WriteAllText(original, "probe");
                if (!CreateLink(Path.Combine(directory.FullName, "link"), original))
                    Skip = "The temporary filesystem does not support hard links.";
            }
            finally { directory.Delete(recursive: true); }
        }
    }

    private static bool CreateLink(string path, string existing) => OperatingSystem.IsWindows()
        ? CreateHardLink(path, existing, IntPtr.Zero) : Link(existing, path) == 0;

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string existing, string path);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string path, string existing, IntPtr reserved);
}
