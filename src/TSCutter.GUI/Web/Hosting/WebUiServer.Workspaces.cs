using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.Utils.Files;
using TSCutter.GUI.Models.Application;

namespace TSCutter.GUI.Web;

internal enum WebUiWorkspacePreparation
{
    Ready, Exporting, Busy
}
internal enum WebUiDirectoryChange
{
    Unchanged, Applied, Restarted, Exporting, Busy
}

internal static class WebUiWorkspaceProtocol
{
    internal const string Prepare = "prepare-workspace:";
    internal const string Resume = "resume-workspace:";
    internal const string Response = "Web UI workspace change: ";
}

internal sealed partial class WebUiServer
{
    private readonly ConcurrentDictionary<string, byte> awaitingReconnect = [];

    internal static MediaWorkspace CreateWorkspace(MediaWorkspace defaults, WebUiAccount account) =>
        new(account.InputDirectory ?? defaults.InputDirectory, account.OutputDirectory ?? defaults.OutputDirectory);

    internal async Task<WebUiDirectoryChange> UpdateDirectoriesAsync(string id, string? inputDirectory, string? outputDirectory,
        CancellationToken token = default)
    {
        if (!string.IsNullOrWhiteSpace(inputDirectory) && !Path.IsPathFullyQualified(inputDirectory) ||
            !string.IsNullOrWhiteSpace(outputDirectory) && !Path.IsPathFullyQualified(outputDirectory))
            throw new ArgumentException("Workspace directories must be absolute paths.");
        await management.WaitAsync(token);
        WebUiWorker? worker = null;
        try
        {
            if (stopping.IsCancellationRequested)
                throw new OperationCanceledException(stopping.Token);
            var account = logins.FindAccount(id) ?? throw new InvalidOperationException("The account no longer exists.");
            var requested = account with
            {
                InputDirectory = string.IsNullOrWhiteSpace(inputDirectory) ? null : inputDirectory,
                OutputDirectory = string.IsNullOrWhiteSpace(outputDirectory) ? null : outputDirectory
            };
            var resolved = await Task.Run(() => CreateWorkspace(options.Workspace, requested), token);
            var input = requested.InputDirectory is null ? null : resolved.InputDirectory;
            var output = requested.OutputDirectory is null ? null : resolved.OutputDirectory;
            if (MediaFileUtil.PathsEqual(account.InputDirectory ?? options.Workspace.InputDirectory, resolved.InputDirectory) &&
                MediaFileUtil.PathsEqual(account.OutputDirectory ?? options.Workspace.OutputDirectory, resolved.OutputDirectory))
            {
                logins.SetDirectories(id, input, output);
                return account.InputDirectory == input && account.OutputDirectory == output ? WebUiDirectoryChange.Unchanged : WebUiDirectoryChange.Applied;
            }
            workers.TryGetValue(id, out worker);
            var restarting = worker?.IsRunning == true;
            if (restarting)
            {
                var preparation = await worker!.PrepareDirectoryChangeAsync(token);
                if (preparation == WebUiWorkspacePreparation.Exporting)
                    return WebUiDirectoryChange.Exporting;
                if (preparation == WebUiWorkspacePreparation.Busy)
                    return WebUiDirectoryChange.Busy;
            }
            token.ThrowIfCancellationRequested();
            logins.SetDirectories(id, input, output);
            if (restarting)
            {
                awaitingReconnect[id] = 0;
                await CloseForDirectoryChangeAsync(id);
            }
            if (workers.TryRemove(id, out var old))
                await old.DisposeAsync();
            return restarting ? WebUiDirectoryChange.Restarted : WebUiDirectoryChange.Applied;
        }
        finally
        {
            worker?.CancelDirectoryChange();
            management.Release();
            UsersChanged?.Invoke();
        }
    }

    private async Task CloseForDirectoryChangeAsync(string id)
    {
        if (!connections.TryGetValue(id, out var connection))
            return;
        Volatile.Write(ref connection.Closing, 1);
        connection.CloseStatus = WorkspaceUpdated;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var acquired = false;
        try
        {
            await connection.Output.WaitAsync(timeout.Token);
            acquired = true;
            await connection.Socket.CloseOutputAsync(WorkspaceUpdated, "Workspace folders changed", timeout.Token);
        }
        catch (Exception error) when (error is OperationCanceledException or System.Net.WebSockets.WebSocketException or InvalidOperationException) { }
        finally
        {
            if (acquired)
                connection.Output.Release();
        }
        try
        {
            await connection.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (TimeoutException) { Disconnect(id, WorkspaceUpdated); }
    }
}
