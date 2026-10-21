using System.Text.Json;
using Xunit;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Utils.Application;

namespace TSCutter.GUI.Tests.Editing;

public sealed class CutterProjectServiceTests
{
    [Fact]
    public void RemuxExportOptionsAndIndependentQueueSnapshotSurviveProjectRoundTrip()
    {
        using var files = new ProjectTestFiles();
        var project = CreateProject(files.CreateSource());
        project.RemuxSettings = new RemuxExportSettings
        {
            Container = "mkv", ReferenceVideoIndex = 0, TimelineOriginSeconds = 0.021, StreamIndices = [0, 1, 3]
        };
        project.Queue[0].RemuxSettings = project.RemuxSettings.Snapshot();
        project.RemuxSettings.StreamIndices.Remove(3);
        var restored = CutterProjectService.Deserialize(CutterProjectService.Serialize(project));
        Assert.Equal("mkv", restored.RemuxSettings!.Container);
        Assert.Equal(0.021, restored.RemuxSettings.TimelineOriginSeconds);
        Assert.Equal(new[] { 0, 1 }, restored.RemuxSettings.StreamIndices);
        Assert.Equal(new[] { 0, 1, 3 }, restored.Queue[0].RemuxSettings!.StreamIndices);
    }

    [Theory]
    [InlineData("mts")]
    [InlineData("m2ts")]
    [InlineData("bin")]
    public void ProjectClipsAreNotRestrictedToTsSuffix(string extension)
    {
        using var files = new ProjectTestFiles();
        var original = files.CreateSource();
        var path = Path.ChangeExtension(original.Path, extension);
        File.Move(original.Path, path);
        var project = CreateProject(CutterProjectService.Snapshot(path));
        var restored = CutterProjectService.Deserialize(CutterProjectService.Serialize(project));
        Assert.Equal(path, restored.Source!.Path);
        Assert.Equal(2, restored.Clips.Count);
    }

    [Fact]
    public async Task GrowingRecordingCanBeReopenedWithItsSavedClips()
    {
        using var files = new ProjectTestFiles();
        var source = files.CreateSource();
        var project = CreateProject(source);
        var projectPath = Path.Combine(files.DirectoryPath, "session.tscut");

        await CutterProjectService.SaveAsync(projectPath, project);
        await using (var append = new FileStream(source.Path, FileMode.Append, FileAccess.Write))
            await append.WriteAsync(new byte[32_000]);

        var restored = await CutterProjectService.LoadAsync(projectPath);
        Assert.Equal(source.Length, restored.Source!.Length);
        Assert.Equal(-1, restored.Clips[0].EndPosition);
        Assert.Equal(1, restored.SelectedClipIndex);
        Assert.Single(restored.Queue);
    }

    [Fact]
    public void ChangedSavedPrefixOrShorterRecordingIsRejected()
    {
        using var files = new ProjectTestFiles();
        var source = files.CreateSource();
        var json = CutterProjectService.Serialize(CreateProject(source));

        using (var stream = new FileStream(source.Path, FileMode.Open, FileAccess.Write))
        {
            stream.Position = 30_000;
            stream.WriteByte(255);
        }
        Assert.Throws<ProjectSourceChangedException>(() => CutterProjectService.Deserialize(json));

        using (var stream = new FileStream(source.Path, FileMode.Open, FileAccess.Write))
            stream.SetLength(source.Length - 1);
        Assert.Throws<ProjectSourceChangedException>(() => CutterProjectService.Deserialize(json));
    }

    [Fact]
    public void InvalidClipBoundaryIsRejectedBeforeRestoringState()
    {
        using var files = new ProjectTestFiles();
        var source = files.CreateSource();
        var project = CreateProject(source);
        project.Clips[0].StartPosition = source.Length + 1;
        var json = JsonSerializer.Serialize(project, AppJsonContext.Default.CutterProject);

        Assert.Throws<InvalidDataException>(() => CutterProjectService.Deserialize(json));
    }

    [Fact]
    public void LargeRecordingChecksSavedClipBoundaries()
    {
        using var files = new ProjectTestFiles();
        var path = Path.Combine(files.DirectoryPath, "long-recording.ts");
        const long boundary = 24 * 1024 * 1024 + 1234;
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            stream.SetLength(32 * 1024 * 1024);
        var source = CutterProjectService.Snapshot(path, [boundary]);
        var json = CutterProjectService.Serialize(CreateProject(source));

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            stream.Position = boundary;
            stream.WriteByte(1);
        }
        Assert.Throws<ProjectSourceChangedException>(() => CutterProjectService.Deserialize(json));
    }

    [Fact]
    public void RepeatedQueueSourcesShareOneBoundedSnapshot()
    {
        using var files = new ProjectTestFiles();
        var source = files.CreateSource();
        var project = CreateProject(source);
        project.Queue.Clear();
        for (var index = 0; index < 100; index++)
            project.Queue.Add(new ProjectQueueItem
            {
                Source = new ProjectSource { Path = source.Path },
                OutputFilePath = Path.Combine(files.DirectoryPath, $"export-{index}.ts"),
                StartTime = index,
                EndTime = index + 1,
                StartPosition = index * 100,
                EndPosition = index * 100 + 50
            });
        project.Source = new ProjectSource { Path = source.Path };

        CutterProjectService.PopulateSources(project);

        Assert.All(project.Queue, item => Assert.Same(project.Source, item.Source));
        Assert.InRange(project.Source!.AnchorOffsets.Count, 2, 32);
        CutterProjectService.Validate(project);
    }

    [Fact]
    public void OpenEndedQueueCanRepresentCurrentFileEnd()
    {
        using var files = new ProjectTestFiles();
        var project = CreateProject(files.CreateSource());
        project.Queue[0].EndPosition = -1;
        project.Queue[0].EndTime = -1;

        var restored = CutterProjectService.Deserialize(CutterProjectService.Serialize(project));

        Assert.Equal(-1, restored.Queue[0].EndTime);
        restored.Queue[0].StartPosition = -1;
        Assert.Throws<InvalidDataException>(() => CutterProjectService.Validate(restored));
    }

    [Fact]
    public void GrowingRecordingInvalidatesOnlyOpenEndedExports()
    {
        var openEnded = new ProjectClip { WasExported = true, EndPosition = -1 };
        var fixedEnd = new ProjectClip { WasExported = true, EndPosition = 200 };

        Assert.True(CutterProjectService.IsSavedExportCurrent(openEnded, 1000, 1000));
        Assert.False(CutterProjectService.IsSavedExportCurrent(openEnded, 1000, 1200));
        Assert.True(CutterProjectService.IsSavedExportCurrent(fixedEnd, 1000, 1200));
    }

    [Fact]
    public void LabelsAndQueueNamingSurviveProjectRoundTrip()
    {
        using var files = new ProjectTestFiles();
        var project = CreateProject(files.CreateSource());
        project.Clips[0].Name = "Opening";
        project.Clips[0].Notes = "First line\nSecond line";
        project.Clips[0].Color = "blue";
        project.Queue[0].Name = "Queued opening";
        project.Queue[0].Notes = "Independent snapshot";
        project.Queue[0].Color = "green";
        project.Queue[0].ClipIndex = 2;
        project.Queue[0].GeneratedFileName = "Recording_002.ts";

        var restored = CutterProjectService.Deserialize(CutterProjectService.Serialize(project));

        Assert.Equal(4, restored.Version);
        Assert.Equal(("Opening", "First line\nSecond line", "blue"),
            (restored.Clips[0].Name, restored.Clips[0].Notes, restored.Clips[0].Color));
        Assert.Equal(("Queued opening", "Independent snapshot", "green", 2, "Recording_002.ts"),
            (restored.Queue[0].Name, restored.Queue[0].Notes, restored.Queue[0].Color,
                restored.Queue[0].ClipIndex, restored.Queue[0].GeneratedFileName));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void OlderProjectsDefaultToEmptyLabels(int version)
    {
        using var files = new ProjectTestFiles();
        var project = CreateProject(files.CreateSource());
        project.Version = version;
        var json = System.Text.Json.Nodes.JsonNode.Parse(CutterProjectService.Serialize(project))!;
        foreach (var item in json["Clips"]!.AsArray().Concat(json["Queue"]!.AsArray()))
            foreach (var property in new[] { "Name", "Notes", "Color", "ClipIndex", "GeneratedFileName" })
                item!.AsObject().Remove(property);

        var restored = CutterProjectService.Deserialize(json.ToJsonString());
        Assert.All(restored.Clips, clip => Assert.Equal(("", "", ""), (clip.Name, clip.Notes, clip.Color)));
        Assert.Equal(1, restored.Queue[0].ClipIndex);
        Assert.Empty(restored.Queue[0].GeneratedFileName);
    }

    [Fact]
    public void InvalidLabelsAreRejectedBeforeRestoringState()
    {
        using var files = new ProjectTestFiles();
        var project = CreateProject(files.CreateSource());
        project.Clips[0].Name = new string('a', ClipLabels.MaximumNameLength + 1);
        Assert.Throws<InvalidDataException>(() => CutterProjectService.Validate(project));
        project.Clips[0].Name = "";
        project.Clips[0].Notes = null!;
        Assert.Throws<InvalidDataException>(() => CutterProjectService.Validate(project));
        project.Clips[0].Notes = "";
        project.Queue[0].Color = "not-a-color";
        Assert.Throws<InvalidDataException>(() => CutterProjectService.Validate(project));
        project.Queue[0].Color = "";
        project.Queue[0].ClipIndex = 0;
        Assert.Throws<InvalidDataException>(() => CutterProjectService.Validate(project));
    }

    private static CutterProject CreateProject(ProjectSource source) => new()
    {
        Source = source,
        SelectedClipIndex = 1,
        Clips =
        [
            new ProjectClip { StartTime = 0, EndTime = 60, StartPosition = 0,
                EndPosition = -1, IsSelected = true },
            new ProjectClip { StartTime = 10, EndTime = 20, StartPosition = 100,
                EndPosition = 200, IsSelected = true }
        ],
        Queue =
        [
            new ProjectQueueItem { Source = source, OutputFilePath =
                Path.Combine(Path.GetDirectoryName(source.Path)!, "export.ts"),
                StartTime = 10, EndTime = 20, StartPosition = 100, EndPosition = 200 }
        ]
    };

    private sealed class ProjectTestFiles : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(
            Path.GetTempPath(),
            "tscutter-project-test-" + Guid.NewGuid().ToString("N"));

        public ProjectTestFiles() => Directory.CreateDirectory(DirectoryPath);

        public ProjectSource CreateSource()
        {
            var path = Path.Combine(DirectoryPath, "recording.ts");
            var content = new byte[160_000];
            Random.Shared.NextBytes(content);
            File.WriteAllBytes(path, content);
            return CutterProjectService.Snapshot(path);
        }

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}
