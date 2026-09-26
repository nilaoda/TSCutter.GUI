using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.Models;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.Services;

internal static class CutterProjectService
{
    private const int MaximumProjectBytes = 16 * 1024 * 1024;
    private const int MaximumItems = 10_000;
    private const int MaximumCapturedAnchorsPerSource = 32;

    public static void PopulateSources(CutterProject project)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var groups = project.Queue.GroupBy(item => Path.GetFullPath(item.Source.Path), comparer)
            .ToDictionary(group => group.Key, group => group.ToList(), comparer);
        if (project.Source is not null && !groups.ContainsKey(Path.GetFullPath(project.Source.Path)))
            groups.Add(Path.GetFullPath(project.Source.Path), []);

        foreach (var (path, items) in groups)
        {
            var boundaries = items.SelectMany(item => new[] { item.StartPosition, item.EndPosition });
            if (project.Source is not null && comparer.Equals(path, Path.GetFullPath(project.Source.Path)))
                boundaries = boundaries.Concat(project.Clips.SelectMany(clip =>
                    new[] { clip.StartPosition, clip.EndPosition }));
            var anchors = boundaries.Where(offset => offset >= 0).Distinct().Order().ToArray();
            if (anchors.Length > MaximumCapturedAnchorsPerSource)
                anchors = Enumerable.Range(0, MaximumCapturedAnchorsPerSource)
                    .Select(index => anchors[(int)((long)index * (anchors.Length - 1) /
                                                   (MaximumCapturedAnchorsPerSource - 1))]).ToArray();
            var source = Snapshot(path, anchors);
            if (project.Source is not null && comparer.Equals(path, Path.GetFullPath(project.Source.Path)))
                project.Source = source;
            foreach (var item in items)
                item.Source = source;
        }
    }

    public static bool IsSavedExportCurrent(ProjectClip clip, long savedSourceLength,
        long currentSourceLength) =>
        clip.WasExported && (clip.EndPosition >= 0 || currentSourceLength <= savedSourceLength);

    public static ProjectSource Snapshot(string path, IEnumerable<long>? anchors = null)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
            throw new FileNotFoundException("Source file is missing.", path);
        var selectedAnchors = (anchors ?? [])
            .Where(offset => offset >= 0 && offset <= file.Length)
            .Distinct()
            .Order()
            .Take(2048)
            .ToList();
        return new ProjectSource
        {
            Path = file.FullName,
            Length = file.Length,
            AnchorOffsets = selectedAnchors,
            PrefixFingerprint = ComputeFingerprint(file.FullName, file.Length, selectedAnchors)
        };
    }

    public static string Serialize(CutterProject project) =>
        JsonSerializer.Serialize(project, AppJsonContext.Default.CutterProject);

    public static CutterProject Deserialize(string json)
    {
        var project = JsonSerializer.Deserialize(json, AppJsonContext.Default.CutterProject)
            ?? throw new InvalidDataException("The project file is empty.");
        Validate(project);
        return project;
    }

    public static async Task<CutterProject> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (new FileInfo(path).Length > MaximumProjectBytes)
            throw new InvalidDataException("The project file is too large.");
        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => Deserialize(json), cancellationToken).ConfigureAwait(false);
    }

    public static async Task SaveAsync(string path, CutterProject project,
        CancellationToken cancellationToken = default)
    {
        var json = await Task.Run(() =>
        {
            Validate(project);
            return Serialize(project);
        }, cancellationToken).ConfigureAwait(false);
        if (Encoding.UTF8.GetByteCount(json) > MaximumProjectBytes)
            throw new InvalidDataException("The project file is too large.");
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(fullPath), ".tscut", StringComparison.OrdinalIgnoreCase) ||
            project.Source is not null && PathsEqual(fullPath, project.Source.Path) ||
            project.Queue.Any(item => PathsEqual(fullPath, item.Source.Path) ||
                                      PathsEqual(fullPath, item.OutputFilePath)))
            throw new InvalidDataException("The project path conflicts with a media file or export target.");
        var directory = Path.GetDirectoryName(fullPath)!;
        var temporaryPath = Path.Combine(directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public static void Validate(CutterProject project)
    {
        if (project.Version != 1)
            throw new InvalidDataException($"Unsupported project version: {project.Version}.");
        if (project.Clips is null || project.Queue is null ||
            project.Clips.Count > MaximumItems || project.Queue.Count > MaximumItems)
            throw new InvalidDataException("The project contains an invalid item list.");
        if (project.Clips.Count > 0 && project.Source is null)
            throw new InvalidDataException("The project has clips without a source file.");
        if (project.Source is null && project.Queue.Count == 0)
            throw new InvalidDataException("The project is empty.");
        if (project.SelectedClipIndex < -1 || project.SelectedClipIndex >= project.Clips.Count)
            throw new InvalidDataException("The selected clip index is invalid.");
        if (!IsFiniteNonnegative(project.CurrentTime) ||
            !IsFiniteNonnegative(project.TimelineZoomLevel) || project.TimelineZoomLevel > 1 ||
            !IsFiniteNonnegative(project.TimelineViewStart))
            throw new InvalidDataException("The saved timeline state is invalid.");

        var validatedSources = new HashSet<(string Path, long Length, string Fingerprint, string Anchors)>();
        if (project.Source is not null)
        {
            ValidateSourceOnce(project.Source, validatedSources);
            if (!string.Equals(System.IO.Path.GetExtension(project.Source.Path), ".ts",
                    StringComparison.OrdinalIgnoreCase) && project.Clips.Count > 0)
                throw new InvalidDataException("Clips require a TS source file.");
        }

        foreach (var clip in project.Clips)
        {
            if (clip is null || !ValidRange(clip.StartTime, clip.EndTime,
                    clip.StartPosition, clip.EndPosition, project.Source!.Length))
                throw new InvalidDataException("A saved clip has invalid boundaries.");
            if (clip.StartThumbnailJpeg?.Length > 512 * 1024 ||
                clip.EndThumbnailJpeg?.Length > 512 * 1024)
                throw new InvalidDataException("A saved clip thumbnail is too large.");
            if (clip.OutputFilePath is not null && !Path.IsPathFullyQualified(clip.OutputFilePath))
                throw new InvalidDataException("A saved output path is invalid.");
        }

        var paths = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var item in project.Queue)
        {
            if (item is null || item.Source is null)
                throw new InvalidDataException("A queued export is invalid.");
            ValidateSourceOnce(item.Source, validatedSources);
            if (!(item.EndPosition == -1 && item.EndTime == -1
                    ? IsFiniteNonnegative(item.StartTime) && item.StartPosition >= 0 &&
                      item.StartPosition <= item.Source.Length
                    : ValidRange(item.StartTime, item.EndTime,
                        item.StartPosition, item.EndPosition, item.Source.Length)) ||
                !Path.IsPathFullyQualified(item.OutputFilePath) ||
                PathsEqual(item.OutputFilePath, item.Source.Path) ||
                project.Source is not null && PathsEqual(item.OutputFilePath, project.Source.Path) ||
                !paths.Add(Path.GetFullPath(item.OutputFilePath)))
                throw new InvalidDataException("A queued export has invalid boundaries or output path.");
        }
        foreach (var item in project.Queue)
            if (paths.Contains(Path.GetFullPath(item.Source.Path)))
                throw new InvalidDataException("An export target conflicts with a source file.");
    }

    private static void ValidateSourceOnce(ProjectSource source,
        HashSet<(string Path, long Length, string Fingerprint, string Anchors)> validatedSources)
    {
        ValidateSourceShape(source);
        var key = (source.Path, source.Length, source.PrefixFingerprint,
            string.Join(',', source.AnchorOffsets));
        if (validatedSources.Add(key))
            ValidateSourceContents(source);
    }

    private static void ValidateSourceShape(ProjectSource source)
    {
        if (string.IsNullOrWhiteSpace(source.Path) || !Path.IsPathFullyQualified(source.Path) ||
            source.Length < 0 || source.AnchorOffsets is null ||
            source.AnchorOffsets.Count > 2048 ||
            source.AnchorOffsets.Any(offset => offset < 0 || offset > source.Length) ||
            string.IsNullOrWhiteSpace(source.PrefixFingerprint))
            throw new InvalidDataException("A source file entry is invalid.");
    }

    private static void ValidateSourceContents(ProjectSource source)
    {
        var file = new FileInfo(source.Path);
        if (!file.Exists)
            throw new FileNotFoundException("A project source file is missing.", source.Path);
        // A recording may still be growing. Its saved prefix must remain intact.
        if (file.Length < source.Length ||
            !string.Equals(ComputeFingerprint(source.Path, source.Length, source.AnchorOffsets),
                source.PrefixFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new ProjectSourceChangedException(source.Path);
    }

    private static string ComputeFingerprint(string path, long prefixLength,
        IReadOnlyList<long> anchors)
    {
        const int chunkSize = 8192;
        const int sampleCount = 32;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, chunkSize, FileOptions.RandomAccess);
        if (stream.Length < prefixLength)
            throw new InvalidDataException($"A project source file has shrunk: {path}");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> number = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(number, prefixLength);
        hash.AppendData(number);
        var buffer = new byte[chunkSize];
        var availableChunks = Math.Max(1,
            prefixLength == 0 ? 0 : 1 + (prefixLength - 1) / chunkSize);
        var chunkCount = prefixLength <= 16 * 1024 * 1024
            ? availableChunks : Math.Min(sampleCount, availableChunks);
        for (var index = 0; index < chunkCount; index++)
        {
            var offset = prefixLength <= 16 * 1024 * 1024
                ? (long)index * chunkSize : chunkCount == 1 ? 0 :
                (prefixLength - Math.Min(chunkSize, prefixLength)) * index / (chunkCount - 1);
            var count = (int)Math.Min(chunkSize, prefixLength - offset);
            BinaryPrimitives.WriteInt64LittleEndian(number, offset);
            hash.AppendData(number);
            stream.Position = offset;
            stream.ReadExactly(buffer.AsSpan(0, count));
            hash.AppendData(buffer.AsSpan(0, count));
        }
        foreach (var anchor in anchors)
        {
            var count = (int)Math.Min(chunkSize, prefixLength);
            var offset = Math.Clamp(anchor - count / 2, 0, prefixLength - count);
            BinaryPrimitives.WriteInt64LittleEndian(number, offset);
            hash.AppendData(number);
            stream.Position = offset;
            stream.ReadExactly(buffer.AsSpan(0, count));
            hash.AppendData(buffer.AsSpan(0, count));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static bool ValidRange(double startTime, double endTime,
        long start, long end, long length) =>
        IsFiniteNonnegative(startTime) && double.IsFinite(endTime) && endTime >= startTime &&
        start >= 0 && start <= length && (end == -1 || end > start && end <= length);

    private static bool IsFiniteNonnegative(double value) => double.IsFinite(value) && value >= 0;

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

internal sealed class ProjectSourceChangedException(string sourcePath)
    : IOException($"A project source file has changed: {sourcePath}")
{
    public string SourcePath { get; } = sourcePath;
}
