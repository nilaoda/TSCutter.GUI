using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TSCutter.GUI.Utils.Files;

internal enum MediaFileAccessError
{
    InputOutsideWorkspace,
    OutputOutsideWorkspace,
    ProjectOutsideWorkspace,
    OutputExists
}

internal sealed class MediaFileAccess
{
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
    internal bool OverwriteExports => !IsRestricted;
    internal string? DefaultOutputDirectory => workspace?.OutputDirectory;

    internal bool AllowsInput(string path) => workspace?.AllowsInput(path) ?? true;

    internal void DemandReadable(string path)
    {
        if (workspace is not null && !workspace.AllowsInput(path) && !workspace.AllowsOutput(path))
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

    internal FileMode PrepareOutput(string path, FileMode mode)
    {
        DemandOutput(path);
        return IsRestricted && mode == FileMode.Create ? FileMode.CreateNew : mode;
    }

    internal FileStream OpenOutput(string path, FileMode mode = FileMode.Create, FileAccess access = FileAccess.Write,
        FileShare share = FileShare.None, int bufferSize = 4096, FileOptions options = FileOptions.None)
    {
        mode = PrepareOutput(path, mode);
        try
        {
            return new FileStream(path, mode, access, share, bufferSize, options);
        }
        catch (IOException error) when (mode == FileMode.CreateNew && IsOutputConflict(path))
        {
            throw OutputConflict(path, error);
        }
    }

    internal bool IsOutputConflict(string path) => IsRestricted && Path.Exists(path);

    internal IOException OutputConflict(string path, IOException error) =>
        new(string.Format(message!(MediaFileAccessError.OutputExists), Path.GetFileName(path)), error);

    internal void PublishExport(string temporaryPath, string outputPath) =>
        PublishOutput(temporaryPath, outputPath, overwrite: OverwriteExports);

    internal void PublishOutput(string temporaryPath, string outputPath, bool overwrite)
    {
        // 选择输出后也可能有其他任务创建同名文件，由文件系统原子地决定是否可以发布。
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
