using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Utils.Application;
using Xunit;

namespace TSCutter.GUI.Tests.Editing;

/// <summary>恢复文件的读写、多实例区分、遗留文件的认领与清理。</summary>
public sealed class ProjectRecoveryStoreTests : IDisposable
{
    private static readonly AppInstanceIdentity First = new(4100, 638_000_000_000_000_000);
    private static readonly AppInstanceIdentity Second = new(4200, 638_000_000_100_000_000);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tscutter-recovery-test-" + Guid.NewGuid().ToString("N"));
    private readonly HashSet<AppInstanceIdentity> _running = [];

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private ProjectRecoveryStore Store(AppInstanceIdentity owner) => new(_directory, owner, _running.Contains);

    private static byte[] Content(int clipCount, string? projectPath = null, DateTime? saved = null) =>
        ProjectRecoveryStore.Serialize(new ProjectRecoveryFile
        {
            SavedUtc = saved ?? DateTime.UtcNow,
            ProjectPath = projectPath,
            Project = new CutterProject
            {
                Source = new ProjectSource { Path = "/media/recording.ts", Length = 1, PrefixFingerprint = "00" },
                Clips = Enumerable.Range(0, clipCount).Select(index => new ProjectClip { StartTime = index }).ToList()
            }
        });

    [Fact]
    public void WritesOwnFileAtomicallyAndReplacesIt()
    {
        var store = Store(First);
        store.Write(Content(2));
        store.Write(Content(3, "/projects/show.tscut"));

        Assert.Equal([Path.GetFileName(store.OwnFilePath)], Directory.GetFiles(_directory).Select(Path.GetFileName));
        Assert.Equal(First.FileKey + ProjectRecoveryStore.Extension, Path.GetFileName(store.OwnFilePath));
        var file = ProjectRecoveryStore.Read(store.OwnFilePath);
        Assert.Equal(3, file.Project.Clips.Count);
        Assert.Equal("/projects/show.tscut", file.ProjectPath);
        Assert.Equal(ProjectRecoveryFile.CurrentFormatVersion, file.FormatVersion);

        store.DeleteOwn();
        Assert.Empty(Directory.GetFiles(_directory));
        // 文件或目录不存在时删除不报错。
        store.DeleteOwn();
        Store(Second).DeleteOwn();
        Directory.Delete(_directory);
        store.DeleteOwn();
    }

    [Fact]
    public void OversizedWriteKeepsThePreviousRecoveryFile()
    {
        var store = Store(First);
        store.Write(Content(3));

        Assert.Throws<InvalidDataException>(() => store.Write(new byte[(32 << 20) + 1]));
        Assert.Equal(3, ProjectRecoveryStore.Read(store.OwnFilePath).Project.Clips.Count);
    }

    [Fact]
    public void FilesAboveThisVersionsReadLimitAreKept()
    {
        Directory.CreateDirectory(_directory);
        var path = Store(First).OwnFilePath;
        using (var file = File.Create(path))
            file.SetLength((32L << 20) + 1);

        Assert.Empty(Store(Second).FindOrphans());
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void InstancesKeepSeparateFilesAndOnlyStoppedOnesAreOffered()
    {
        var first = Store(First);
        var second = Store(Second);
        first.Write(Content(1, "/projects/first.tscut", DateTime.UtcNow.AddHours(-2)));
        second.Write(Content(2, saved: DateTime.UtcNow.AddHours(-1)));

        // 两个实例都在运行：谁也不提示对方的文件，也不提示自己的。
        _running.UnionWith([First, Second]);
        Assert.Empty(first.FindOrphans());
        Assert.Empty(second.FindOrphans());

        // 第一个实例异常退出：第二个实例启动检查时才看到它的文件，提示所需的摘要齐全。
        _running.Remove(First);
        var entry = Assert.Single(second.FindOrphans());
        Assert.Equal((First, 1, 0, "/media/recording.ts", "/projects/first.tscut"),
            (entry.Owner, entry.ClipCount, entry.QueueCount, entry.SourcePath, entry.ProjectPath));
        Assert.Empty(first.FindOrphans());

        // 两个实例都异常退出：最近保存的排在前面。
        _running.Clear();
        Assert.Equal([Second, First], Store(new AppInstanceIdentity(4300, 638_000_000_200_000_000))
            .FindOrphans().Select(item => item.Owner));
    }

    [Fact]
    public void UnreadableFilesAndStaleTemporaryFilesAreCleanedUp()
    {
        Directory.CreateDirectory(_directory);
        var corrupt = Path.Combine(_directory, First.FileKey + ProjectRecoveryStore.Extension);
        File.WriteAllText(corrupt, "{ not json");
        var future = Path.Combine(_directory, Second.FileKey + ProjectRecoveryStore.Extension);
        // 更高版本可以更改 Project 的结构，旧版仍须保留这个文件。
        File.WriteAllText(future,
            $$"""{"FormatVersion":{{ProjectRecoveryFile.CurrentFormatVersion + 1}},"Project":"future format"}""");
        var staleTemporary = Path.Combine(_directory, $"{First.FileKey}.{Guid.NewGuid():N}.tmp");
        var runningOwner = new AppInstanceIdentity(4400, 638_000_000_300_000_000);
        var liveTemporary = Path.Combine(_directory, $"{runningOwner.FileKey}.{Guid.NewGuid():N}.tmp");
        var unrelated = Path.Combine(_directory, "notes.txt");
        File.WriteAllText(staleTemporary, "");
        File.WriteAllText(liveTemporary, "");
        File.WriteAllText(unrelated, "");
        _running.Add(runningOwner);

        Assert.Empty(Store(new AppInstanceIdentity(4500, 638_000_000_400_000_000)).FindOrphans());
        Assert.False(File.Exists(corrupt));
        Assert.False(File.Exists(staleTemporary));
        // 更新版本程序写的文件、仍在运行的实例正在写的临时文件和无关文件都不动。
        Assert.True(File.Exists(future));
        Assert.True(File.Exists(liveTemporary));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void ClaimMovesTheFileToThisInstanceAndReleasePutsItBack()
    {
        Store(First).Write(Content(4));
        var current = Store(Second);
        var otherInstance = Store(new AppInstanceIdentity(4600, 638_000_000_500_000_000));
        var entry = Assert.Single(current.FindOrphans());
        Assert.Single(otherInstance.FindOrphans());

        Assert.True(current.Claim(entry));
        Assert.True(File.Exists(current.OwnFilePath));
        Assert.False(File.Exists(entry.Path));
        // 另一个实例同时认领同一份文件时失败，不会两边都恢复。
        Assert.False(otherInstance.Claim(entry));
        Assert.Equal(4, ProjectRecoveryStore.Read(current.OwnFilePath).Project.Clips.Count);

        current.Release(entry);
        Assert.False(File.Exists(current.OwnFilePath));
        Assert.Equal(First, Assert.Single(current.FindOrphans()).Owner);

        // 本实例已有恢复文件时不认领，避免覆盖当前未保存的内容。
        current.Write(Content(7));
        Assert.False(current.Claim(entry));
        Assert.True(File.Exists(entry.Path));
        Assert.Equal(7, ProjectRecoveryStore.Read(current.OwnFilePath).Project.Clips.Count);

        ProjectRecoveryStore.Discard(entry);
        Assert.Empty(current.FindOrphans());
    }

    [Fact]
    public void InstanceIdentityMatchesProcessAndStartTime()
    {
        var current = AppInstanceIdentity.Current;
        Assert.Equal(Environment.ProcessId, current.ProcessId);
        Assert.True(AppInstanceIdentity.IsRunning(current));
        Assert.True(AppInstanceIdentity.IsRunning(current with { StartTimeUtcTicks = current.StartTimeUtcTicks + 1 }));
        // 进程号被复用：启动时间不同，不是同一个实例。
        Assert.False(AppInstanceIdentity.IsRunning(current with { StartTimeUtcTicks = current.StartTimeUtcTicks - TimeSpan.TicksPerHour }));

        Assert.True(AppInstanceIdentity.TryParseFileKey(current.FileKey, out var parsed));
        Assert.Equal(current, parsed);
        foreach (var invalid in new[] { "", "12", "-5", "abc-1", "1-abc", "0-5", "1-0", "1--5", "+1-5" })
            Assert.False(AppInstanceIdentity.TryParseFileKey(invalid, out _), invalid);
    }
}
