using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using TSCutter.GUI.ViewModels;
using TSCutter.GUI.ViewModels.Editing;

namespace TSCutter.GUI.Web;

internal sealed partial class WebUiSession
{
    private string? workspaceChangeRequest;
    private string? reportedState;
    private string? reportedPreferences;

    private void StartWorkerReporting(IClassicDesktopStyleApplicationLifetime lifetime, Window mainWindow, CancellationToken token)
    {
        if (Environment.GetEnvironmentVariable("TSCUTTER_WEB_MANAGED") != WebUiWorker.ManagedValue)
            return;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(token))
                    await Dispatcher.UIThread.InvokeAsync(() => PublishWorkerSnapshot(lifetime, mainWindow), DispatcherPriority.Background, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        });
    }

    private void PrepareWorkspaceChange(string request, IClassicDesktopStyleApplicationLifetime lifetime, Window mainWindow, WebUiDesktop desktop)
    {
        if (!Guid.TryParseExact(request, "N", out _))
            return;
        var dialogs = lifetime.Windows.Where(window => window != mainWindow && window.IsVisible).ToArray();
        var result = dialogs.Any(window => window.DataContext is OutputWindowViewModel)
            ? WebUiWorkspacePreparation.Exporting
            : dialogs.Length != 0 || (mainWindow.DataContext as MainWindowViewModel)?.IsLoadingMedia == true
                ? WebUiWorkspacePreparation.Busy : WebUiWorkspacePreparation.Ready;
        if (result == WebUiWorkspacePreparation.Ready)
        {
            // 校验和暂停输入在同一次界面调度中完成，避免确认后又启动新的导出。
            workspaceChangeRequest = request;
            desktop.ResetInput();
            PublishWorkerSnapshot(lifetime, mainWindow);
        }
        Console.WriteLine(WebUiWorkspaceProtocol.Response + request + ":" + result);
    }

    private void ResumeWorkspaceInput(string request)
    {
        if (workspaceChangeRequest == request)
            workspaceChangeRequest = null;
    }

    private void PublishWorkerSnapshot(IClassicDesktopStyleApplicationLifetime lifetime, Window mainWindow)
    {
        var editor = mainWindow.DataContext as MainWindowViewModel;
        var dialogs = lifetime.Windows.Where(window => window != mainWindow && window.IsVisible).ToArray();
        var state = new WebUiWorkerState(mainWindow.Title ?? string.Empty, Volatile.Read(ref connected) != 0,
            editor?.IsVideoInitialized == true || editor?.Clips.Count > 0 || editor?.ExportQueue.Count > 0 || dialogs.Length != 0,
            dialogs.Any(window => window.DataContext is OutputWindowViewModel));
        var json = JsonSerializer.Serialize(state, WebUiWorkerJsonContext.Default.WebUiWorkerState);
        if (json != reportedState)
        {
            reportedState = json;
            Console.WriteLine(WebUiWorker.StateMessage + json);
        }
        if (App.ConfigService is not { } config)
            return;
        var preferences = WebUiPreferences.CreateSnapshot(config.CurrentConfig, editor?.CutMode, editor?.ClipMode);
        if (preferences == reportedPreferences)
            return;
        reportedPreferences = preferences;
        // 偏好只回传给对应账号；不写桌面配置，也不把账号口令传入编辑进程。
        Console.WriteLine(WebUiWorker.PreferencesMessage + Convert.ToBase64String(Encoding.UTF8.GetBytes(preferences)));
    }
}
