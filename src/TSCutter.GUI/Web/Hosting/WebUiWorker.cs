using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.Utils.Files;

namespace TSCutter.GUI.Web;

internal sealed record WebUiWorkerState(string Title, bool Connected, bool HasWorkspace, bool Exporting);
internal sealed record WebUiUserStatus(string Id, string UserName, bool Enabled, bool Running, bool Connected,
    bool Exporting, string Title, string OutputDirectory, int? ProcessId = null, string? InputDirectory = null, bool AwaitingReconnect = false);

[JsonSerializable(typeof(WebUiWorkerState))]
internal sealed partial class WebUiWorkerJsonContext : JsonSerializerContext;

internal sealed class WebUiWorker : IAsyncDisposable
{
    internal const string StateMessage = "Web UI worker state: ";
    internal const string PreferencesMessage = "Web UI preferences: ";
    internal const string ManagedValue = "account";
    private readonly Process process;
    private readonly object inputGate = new();
    private readonly TaskCompletionSource<Uri> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action changed;
    private readonly Action<string> preferencesChanged;
    private string? error;
    private int started;
    private int stopping;
    private int exited;
    private string? directoryRequest;
    private TaskCompletionSource<WebUiWorkspacePreparation>? preparingDirectory;
    internal MediaWorkspace Workspace
    {
        get;
    }
    internal int ProcessId
    {
        get; private set;
    }
    internal string AccessKey { get; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    internal WebUiWorkerState State { get; private set; } = new(string.Empty, false, true, false);
    internal DateTimeOffset LastDisconnected { get; set; } = DateTimeOffset.UtcNow;
    internal bool IsRunning => Volatile.Read(ref started) != 0 && Volatile.Read(ref exited) == 0;
    internal bool IsStarting => Volatile.Read(ref started) != 0 && !ready.Task.IsCompleted;
    internal Task<Uri> Ready => ready.Task;

    internal WebUiWorker(MediaWorkspace workspace, string? preferences, string? theme, Action changed,
        Action<string> preferencesChanged, string? executable = null, string? assembly = null)
    {
        Workspace = workspace;
        this.changed = changed;
        this.preferencesChanged = preferencesChanged;
        executable ??= Environment.ProcessPath ?? throw new InvalidOperationException("The application executable is unavailable.");
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(assembly ?? typeof(WebUiWorker).Assembly.Location);
        info.ArgumentList.Add("--web-ui");
        info.ArgumentList.Add("0");
        info.ArgumentList.Add(workspace.InputDirectory);
        info.ArgumentList.Add(workspace.OutputDirectory);
        info.Environment["TSCUTTER_WEB_ACCESS_KEY"] = AccessKey;
        info.Environment["TSCUTTER_WEB_MANAGED"] = ManagedValue;
        info.Environment.Remove(WebUiPairing.StateVariable);
        info.Environment["TSCUTTER_WEB_THEME"] = theme;
        info.Environment[WebUiPreferences.EnvironmentVariable] = preferences;
        process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, data) => ReadOutput(data.Data);
        process.ErrorDataReceived += (_, data) =>
        {
            if (!string.IsNullOrWhiteSpace(data.Data))
                error = data.Data;
        };
        process.Exited += (_, _) =>
        {
            Volatile.Write(ref exited, 1);
            var failure = new IOException(error ?? "The editor process exited.");
            ready.TrySetException(failure);
            lock (inputGate)
                preparingDirectory?.TrySetException(failure);
            changed();
        };
    }

    internal void Start()
    {
        if (!process.Start())
            throw new IOException("Could not start the editor process.");
        ProcessId = process.Id;
        Volatile.Write(ref started, 1);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    private void ReadOutput(string? line)
    {
        if (line is null)
            return;
        if (line.StartsWith(WebUiWorkspaceProtocol.Response, StringComparison.Ordinal))
        {
            var parts = line[WebUiWorkspaceProtocol.Response.Length..].Split(':');
            lock (inputGate)
                if (parts.Length == 2 && parts[0] == directoryRequest &&
                    Enum.TryParse<WebUiWorkspacePreparation>(parts[1], out var result) && Enum.IsDefined(result))
                    preparingDirectory?.TrySetResult(result);
        }
        else if (line.StartsWith("Web UI: ", StringComparison.Ordinal) && Uri.TryCreate(line[8..], UriKind.Absolute, out var endpoint))
        {
            // 子进程只监听随机的本机端口，外部用户始终经过统一入口。
            if (endpoint.Host == "127.0.0.1" && endpoint.Scheme == "http")
                ready.TrySetResult(new Uri(endpoint.GetLeftPart(UriPartial.Authority)));
        }
        else if (line.StartsWith(StateMessage, StringComparison.Ordinal))
        {
            try
            {
                var next = JsonSerializer.Deserialize(line[StateMessage.Length..], WebUiWorkerJsonContext.Default.WebUiWorkerState);
                if (next is not null && next != State)
                {
                    State = next;
                    changed();
                }
            }
            catch (JsonException) { }
        }
        else if (line.StartsWith(PreferencesMessage, StringComparison.Ordinal))
        {
            try
            {
                preferencesChanged(Encoding.UTF8.GetString(Convert.FromBase64String(line[PreferencesMessage.Length..])));
            }
            catch (Exception exception) when (exception is FormatException or JsonException) { }
        }
    }

    internal async Task<WebUiWorkspacePreparation> PrepareDirectoryChangeAsync(CancellationToken token)
    {
        var completion = new TaskCompletionSource<WebUiWorkspacePreparation>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (inputGate)
        {
            if (!IsRunning)
                return WebUiWorkspacePreparation.Ready;
            if (directoryRequest is not null)
                throw new InvalidOperationException("A workspace change is already pending.");
            directoryRequest = Guid.NewGuid().ToString("N");
            preparingDirectory = completion;
            if (!SendCommand(WebUiWorkspaceProtocol.Prepare + directoryRequest))
            {
                directoryRequest = null;
                preparingDirectory = null;
                throw new IOException("Could not contact the editor process.");
            }
        }
        try
        {
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            if (result != WebUiWorkspacePreparation.Ready)
                CancelDirectoryChange();
            return result;
        }
        catch { CancelDirectoryChange(); throw; }
        finally
        {
            lock (inputGate)
                if (ReferenceEquals(preparingDirectory, completion))
                    preparingDirectory = null;
        }
    }

    internal void CancelDirectoryChange()
    {
        lock (inputGate)
        {
            if (directoryRequest is not { } request)
                return;
            directoryRequest = null;
            SendCommand(WebUiWorkspaceProtocol.Resume + request);
        }
    }

    internal bool SendCommand(string command)
    {
        lock (inputGate)
        {
            if (Volatile.Read(ref stopping) != 0 || !IsRunning)
                return false;
            try
            {
                process.StandardInput.WriteLine(command);
                return true;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException) { return false; }
        }
    }

    internal void CloseInput()
    {
        lock (inputGate)
        {
            if (Interlocked.Exchange(ref stopping, 1) != 0 || !IsRunning)
                return;
            try
            {
                process.StandardInput.WriteLine("snapshot");
                process.StandardInput.Close();
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        CloseInput();
        try
        {
            if (Volatile.Read(ref started) != 0)
            {
                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException)
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }
        finally { Volatile.Write(ref exited, 1); process.Dispose(); }
    }
}
