using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace TSCutter.GUI.Utils.Files;

internal enum MediaFileAccessError
{
    InputOutsideWorkspace,
    OutputOutsideWorkspace,
    ProjectOutsideWorkspace,
    OutputExists,
    OutputMatchesSource
}

internal sealed class MediaFileAccess
{
    private readonly HashSet<string> overwriteGrants = new(MediaFileUtil.PathComparer);
    private readonly MediaWorkspace? workspace;
    private readonly Func<MediaFileAccessError, string>? message;

    private MediaFileAccess()
    {
    }

    internal MediaFileAccess(MediaWorkspace workspace, Func<MediaFileAccessError, string> message)
    {
        this.workspace = workspace;
        this.message = message;
    }

    // 当前每个进程只有一套文件规则，由启动入口配置后供现有媒体服务使用。
    internal static MediaFileAccess Current { get; private set; } = new();

    internal static void ConfigureForProcess(MediaFileAccess access) => Current = access;

    internal bool IsRestricted => workspace is not null;
    internal string? DefaultOutputDirectory => workspace?.OutputDirectory;

    internal bool AllowsInput(string path) => workspace?.AllowsInput(path) ?? true;

    internal bool AllowsOutput(string path) => workspace?.AllowsOutput(path) ?? true;

    internal void DemandReadable(string path)
    {
        if (workspace is not null && !workspace.AllowsInput(path))
            throw new UnauthorizedAccessException(message!(MediaFileAccessError.InputOutsideWorkspace));
    }

    internal void DemandOutput(string path)
    {
        if (workspace is not null && !workspace.AllowsOutput(path))
            throw new UnauthorizedAccessException(message!(MediaFileAccessError.OutputOutsideWorkspace));
    }

    internal void DemandProjectPaths(IEnumerable<string> inputs, IEnumerable<string> outputs)
    {
        if (workspace is not null &&
            (inputs.Any(path => !workspace.AllowsInput(path)) || outputs.Any(path => !workspace.AllowsOutput(path))))
            throw new InvalidDataException(message!(MediaFileAccessError.ProjectOutsideWorkspace));
    }

    internal void GrantOverwrite(string path)
    {
        path = Path.GetFullPath(path);
        DemandOutput(path);
        lock (overwriteGrants)
            overwriteGrants.Add(MediaFileUtil.CanonicalPath(path));
    }

    internal void ClearOverwriteGrants()
    {
        lock (overwriteGrants)
            overwriteGrants.Clear();
    }

    internal bool TryConsumeOverwrite(string path)
    {
        lock (overwriteGrants)
            return overwriteGrants.Remove(MediaFileUtil.CanonicalPath(path));
    }

    internal bool CanOverwrite(string path)
    {
        if (!IsRestricted)
            return true;
        lock (overwriteGrants)
            return overwriteGrants.Contains(MediaFileUtil.CanonicalPath(path));
    }

    private bool TakeOverwrite(string path)
    {
        var granted = TryConsumeOverwrite(path);
        return !IsRestricted || granted;
    }

    // 媒体输出在全部前置检查通过后调用；删除目录项后新建文件，避免修改硬链接指向的数据。
    internal void BeginMediaOutput(string path, IEnumerable<string> sources)
    {
        path = Path.GetFullPath(path);
        DemandOutput(path);
        DemandDifferentSources(path, sources);
        var directory = Path.GetDirectoryName(path);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException(directory);
        if (TakeOverwrite(path))
            File.Delete(path);
        else if (Path.Exists(path))
            throw OutputConflict(path, new IOException("The output file already exists."));
    }

    private void DemandDifferentSources(string path, IEnumerable<string> sources)
    {
        if (sources.Any(source => MediaFileUtil.ConflictsWithSource(path, Path.GetFullPath(source))))
            throw new IOException(message?.Invoke(MediaFileAccessError.OutputMatchesSource) ??
                "The output path conflicts with a source file.");
    }

    internal FileMode PrepareOutput(string path, FileMode mode, IEnumerable<string>? sources = null)
    {
        DemandOutput(path);
        if (mode == FileMode.Create)
        {
            BeginMediaOutput(path, sources ?? []);
            return FileMode.CreateNew;
        }
        return mode;
    }

    internal FileStream OpenOutput(string path, FileMode mode = FileMode.Create, FileAccess access = FileAccess.Write,
        FileShare share = FileShare.None, int bufferSize = 4096, FileOptions options = FileOptions.None,
        IEnumerable<string>? sources = null)
    {
        mode = PrepareOutput(path, mode, sources);
        try
        {
            return new FileStream(path, mode, access, share, bufferSize, options);
        }
        catch (IOException error) when (mode == FileMode.CreateNew && IsOutputConflict(path))
        {
            throw OutputConflict(path, error);
        }
    }

    // 小文件先完整写入同目录临时文件，写入失败时保留目标及其内容。
    internal async Task WriteOutputAsync(string path, Func<Stream, Task> write, IEnumerable<string>? sources = null)
    {
        path = Path.GetFullPath(path);
        DemandOutput(path);
        DemandDifferentSources(path, sources ?? []);
        var overwrite = TakeOverwrite(path);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".tscutter-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = OpenOutput(temporary, FileMode.CreateNew))
                await write(stream).ConfigureAwait(false);
            MoveOutput(temporary, path, overwrite);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    internal bool IsOutputConflict(string path) => IsRestricted && Path.Exists(path);

    internal IOException OutputConflict(string path, IOException error) =>
        new(string.Format(message!(MediaFileAccessError.OutputExists), Path.GetFileName(path)), error);

    internal void PublishExport(string temporaryPath, string outputPath) =>
        PublishOutput(temporaryPath, outputPath, overwrite: !IsRestricted);

    internal void PublishOutput(string temporaryPath, string outputPath, bool overwrite)
    {
        DemandOutput(outputPath);
        var granted = TryConsumeOverwrite(outputPath);
        MoveOutput(temporaryPath, outputPath, overwrite || granted);
    }

    private void MoveOutput(string temporaryPath, string outputPath, bool overwrite)
    {
        // 创建和发布均由文件系统检查重名，禁止覆盖的写入不会替换并发生成的文件。
        DemandOutput(outputPath);
        try
        {
            File.Move(temporaryPath, outputPath, overwrite);
        }
        catch (IOException error) when (!overwrite && IsOutputConflict(outputPath))
        {
            throw OutputConflict(outputPath, error);
        }
    }
}
