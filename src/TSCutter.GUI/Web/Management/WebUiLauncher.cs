using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Classic.Avalonia.Theme;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.Utils.Files;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.Web;

internal static partial class WebUiLauncher
{
    private static WebUiServer? server;
    private static Window? panel;
    private static WebUiLoginStore? logins;
    private static MediaWorkspace? previousWorkspace;
    private static string[] links = [];
    public static bool IsRunning => server?.IsRunning == true;
    public static event Action? RunningChanged;
    private static event Action? UsersChanged;

    private sealed record DirectoryDraft(bool UseDefaultSource, string SourcePath, bool UseDefaultOutput, string OutputPath);

    public static async Task ShowAsync(Window owner)
    {
        if (panel is not null)
        {
            panel.Activate();
            return;
        }
        if (App.ConfigService is not { } configService)
            return;
        var config = configService.CurrentConfig;
        var strings = LocalizationManager.Instance;
        var window = new WebUiSettingsWindow();
        if (owner.Screens.ScreenFromWindow(owner) is { Scaling: > 0 } screen)
        {
            var width = screen.WorkingArea.Width / screen.Scaling;
            var height = screen.WorkingArea.Height / screen.Scaling;
            window.MinWidth = Math.Min(window.MinWidth, width);
            window.MinHeight = Math.Min(window.MinHeight, height);
            window.Width = Math.Min(window.Width, width);
            window.Height = Math.Min(window.Height, height);
            window.MaxHeight = height;
        }
        window.SourcePath.Text = config.WebUiInputDirectory;
        window.DestinationPath.Text = config.WebUiOutputDirectory;
        window.ListenPort.Value = config.WebUiPort is >= 1024 and <= 65535 ? config.WebUiPort : 5360;
        window.MaxWorkers.Value = Math.Clamp(config.WebUiMaxWorkers, 1, 16);
        window.AllowLan.IsChecked = config.WebUiAllowLan;
        window.EnableIpv6.IsChecked = config.WebUiEnableIpv6;
        window.EnableIpv6.IsEnabled = config.WebUiAllowLan;
        window.AllowLan.IsCheckedChanged += (_, _) => window.EnableIpv6.IsEnabled = window.AllowLan.IsChecked == true;
        var busy = false;
        var updating = false;
        var directoryError = string.Empty;
        string? selectAfterRefresh = null;
        WebUiUserStatus[] shown = [];
        var drafts = new System.Collections.Generic.Dictionary<string, DirectoryDraft>();
        var rows = new System.Collections.Generic.Dictionary<string, WebUiAccountRow>();
        WebUiUserStatus? Selected() => window.Accounts.SelectedItem is WebUiAccountRow row ? shown.FirstOrDefault(item => item.Id == row.Id) : null;
        WebUiAccount? Account(string id) => config.WebUiAccounts.FirstOrDefault(account => account.Id == id);
        string DefaultInput() => window.SourcePath.Text?.Trim() ?? string.Empty;
        string DefaultOutput() => window.DestinationPath.Text?.Trim() ?? string.Empty;
        bool ReadNumber(NumericUpDown control, int minimum, int maximum, out int value) =>
            int.TryParse(control.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value) && value >= minimum && value <= maximum;
        DirectoryDraft Draft(WebUiAccount account) => drafts.GetValueOrDefault(account.Id) ??
            new DirectoryDraft(account.InputDirectory is null, account.InputDirectory ?? DefaultInput(),
                account.OutputDirectory is null, account.OutputDirectory ?? DefaultOutput());
        bool Changed(WebUiAccount account)
        {
            var draft = Draft(account);
            return draft.UseDefaultSource != (account.InputDirectory is null) ||
                !draft.UseDefaultSource && !SameDirectory(draft.SourcePath, account.InputDirectory) ||
                draft.UseDefaultOutput != (account.OutputDirectory is null) ||
                !draft.UseDefaultOutput && !SameDirectory(draft.OutputPath, account.OutputDirectory);
        }
        string StateLabel(WebUiUserStatus account) => !account.Enabled ? strings.String_WebUi_AccountDisabled :
            account.Exporting ? strings.String_WebUi_UserExporting : account.Connected ? strings.String_WebUi_UserConnected :
            account.Running ? strings.String_WebUi_UserRetained : account.AwaitingReconnect ? strings.String_WebUi_UserWaitingReconnect : strings.String_WebUi_UserStopped;

        void RenderDetails(bool keepInput = false, bool keepOutput = false)
        {
            var selected = Selected();
            var account = selected is null ? null : Account(selected.Id);
            window.AccountDetails.IsVisible = account is not null;
            window.NoAccountSelected.IsVisible = account is null;
            if (account is null || selected is null)
                return;
            updating = true;
            try
            {
                var draft = Draft(account);
                var changed = Changed(account);
                var input = draft.UseDefaultSource ? DefaultInput() : draft.SourcePath;
                var output = draft.UseDefaultOutput ? DefaultOutput() : draft.OutputPath;
                var saved = selected.InputDirectory ?? account.InputDirectory ?? DefaultInput();
                var restart = selected.Running && (!SameDirectory(saved, input) || !SameDirectory(selected.OutputDirectory, output));
                window.SelectedUserName.Text = account.UserName;
                window.SelectedUserState.Text = StateLabel(selected);
                var prefix = ReleaseInfo.WindowTitle + " - ";
                window.SelectedUserFile.Text = selected.Title.StartsWith(prefix, StringComparison.Ordinal) ? selected.Title[prefix.Length..] :
                    selected.Title == ReleaseInfo.WindowTitle ? string.Empty : selected.Title;
                window.SelectedUserFile.IsVisible = !string.IsNullOrEmpty(window.SelectedUserFile.Text);
                window.UseDefaultSource.IsChecked = draft.UseDefaultSource;
                window.UserSourcePath.IsReadOnly = draft.UseDefaultSource;
                if (!keepInput && window.UserSourcePath.Text != input)
                    window.UserSourcePath.Text = input;
                window.UserSourceHint.Text = draft.UseDefaultSource ? string.Format(strings.String_WebUi_InheritedSourceHint, DefaultInput()) : strings.String_WebUi_CustomSourceHint;
                window.UseDefaultOutput.IsChecked = draft.UseDefaultOutput;
                window.UserOutput.IsReadOnly = draft.UseDefaultOutput;
                if (!keepOutput && window.UserOutput.Text != output)
                    window.UserOutput.Text = output;
                window.UserOutputHint.Text = draft.UseDefaultOutput ? string.Format(strings.String_WebUi_InheritedOutputHint, DefaultOutput()) : strings.String_WebUi_CustomOutputHint;
                window.StopWorker.IsEnabled = !busy && selected.Running;
                window.ResetPassword.IsEnabled = window.ToggleAccount.IsEnabled = window.RemoveAccount.IsEnabled = !busy;
                window.UseDefaultSource.IsEnabled = window.UserSourcePath.IsEnabled = window.BrowseUserSource.IsEnabled = !busy;
                window.UseDefaultOutput.IsEnabled = window.UserOutput.IsEnabled = window.BrowseUserOutput.IsEnabled = !busy;
                window.ToggleAccount.Content = selected.Enabled ? strings.String_WebUi_DisableAccount : strings.String_WebUi_EnableAccount;
                window.DirectoryNotice.IsVisible = selected.Exporting || changed && restart;
                window.DirectoryNoticeText.Text = selected.Exporting ? strings.String_WebUi_DirectoryExportBlocked : strings.String_WebUi_DirectoryRestartHint;
                window.DirectoryDirtyState.Text = changed ? strings.String_WebUi_DirectoryChangesPending : strings.String_WebUi_NoDirectoryChanges;
                window.CancelUserDirectory.IsEnabled = !busy && changed;
                window.SaveUserDirectory.IsEnabled = !busy && changed && !selected.Exporting;
                window.DirectoryError.Text = directoryError;
                window.DirectoryError.IsVisible = directoryError.Length != 0;
                if (rows.TryGetValue(account.Id, out var row))
                    row.HasChanges = changed;
            }
            finally { updating = false; }
        }

        void RefreshUsers()
        {
            var selectedId = selectAfterRefresh ?? Selected()?.Id;
            selectAfterRefresh = null;
            updating = true;
            try
            {
                shown = (server?.GetUserStatus(config.WebUiAccounts) ?? config.WebUiAccounts.Select(account => new WebUiUserStatus(
                    account.Id, account.UserName, account.Enabled, false, false, false, string.Empty,
                    account.OutputDirectory ?? DefaultOutput(),
                    InputDirectory: account.InputDirectory ?? DefaultInput())).ToArray()).OrderBy(account => account.UserName, StringComparer.CurrentCultureIgnoreCase).ToArray();
                var next = shown.Select(item =>
                {
                    if (!rows.TryGetValue(item.Id, out var row) || row.UserName != item.UserName)
                        rows[item.Id] = row = new WebUiAccountRow(item.Id, item.UserName);
                    row.Status = StateLabel(item);
                    row.HasChanges = Account(item.Id) is { } account && Changed(account);
                    return row;
                }).ToArray();
                if (window.Accounts.ItemsSource is not System.Collections.Generic.IEnumerable<WebUiAccountRow> current || !current.SequenceEqual(next))
                    window.Accounts.ItemsSource = next;
                window.Accounts.SelectedItem = next.FirstOrDefault(item => item.Id == selectedId) ?? next.FirstOrDefault();
                foreach (var removed in rows.Keys.Where(id => !shown.Any(item => item.Id == id)).ToArray())
                {
                    rows.Remove(removed);
                    drafts.Remove(removed);
                }
            }
            finally { updating = false; }
            RenderDetails();
        }

        void Refresh()
        {
            var started = IsRunning;
            window.Configuration.IsEnabled = !started && !busy;
            window.Sharing.IsVisible = started;
            var selectedLink = window.Addresses.SelectedItem as string;
            window.Addresses.ItemsSource = links;
            var selectedIndex = selectedLink is null ? -1 : Array.IndexOf(links, selectedLink);
            window.Addresses.SelectedIndex = selectedIndex >= 0 ? selectedIndex : config.WebUiAllowLan && links.Length > 1 ? 1 : 0;
            window.StartStop.Content = started ? strings.String_WebUi_Stop : strings.String_WebUi_Start;
            window.StartStop.IsEnabled = window.CloseDialog.IsEnabled = window.AddAccount.IsEnabled = window.Accounts.IsEnabled = !busy;
            window.ServiceProgress.IsVisible = window.ServiceProgress.IsIndeterminate = busy;
            RefreshUsers();
        }

        void ShowStatus(string? message, bool error = false)
        {
            window.Status.Text = message ?? strings.String_WebUi_DirectoryAuthorityHint;
            window.Status.IsVisible = true;
            window.Status.Classes.Set("error", error);
        }

        async Task SaveAccountsAsync()
        {
            if (server is not null)
                await server.UpdateAccountsAsync(config.WebUiAccounts);
            else
                logins?.UpdateAccounts(config.WebUiAccounts);
            configService.Save();
        }

        string WorkspaceErrorText(Exception error) => error switch
        {
            MediaWorkspaceException { Error: MediaWorkspaceError.OverlappingDirectories } => strings.String_WebUi_SeparateWorkspaceFolders,
            MediaWorkspaceException { Error: MediaWorkspaceError.UnsafeDirectory } => strings.String_WebUi_UnsafeWorkspaceFolder,
            DirectoryNotFoundException => strings.String_WebUi_WorkspaceFolderUnavailable,
            UnauthorizedAccessException => strings.String_WebUi_WorkspaceFolderAccessDenied,
            _ => strings.String_WebUi_DirectoryInvalid
        };

        async Task<MediaWorkspace> CreateWorkspaceAsync(string input, string output)
        {
            try
            {
                return await Task.Run(() => new MediaWorkspace(input, output));
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
            {
                throw new IOException(WorkspaceErrorText(error), error);
            }
        }

        async Task RunAsync(Func<Task> action)
        {
            if (busy)
                return;
            busy = true;
            Refresh();
            ShowStatus(null);
            try
            {
                await action();
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                ShowStatus(error is MediaWorkspaceException ? WorkspaceErrorText(error) : error.Message, true);
            }
            finally { busy = false; Refresh(); }
        }

        window.Closing += (_, args) => args.Cancel = busy;
        window.Closed += (_, _) =>
        {
            // 未启动服务也保存连接设置；未完成或无效的数字输入不覆盖上次的有效配置。
            var input = string.IsNullOrWhiteSpace(window.SourcePath.Text) ? null : DefaultInput();
            var output = string.IsNullOrWhiteSpace(window.DestinationPath.Text) ? null : DefaultOutput();
            var port = ReadNumber(window.ListenPort, 1024, 65535, out var parsedPort) ? parsedPort : config.WebUiPort;
            var workers = ReadNumber(window.MaxWorkers, 1, 16, out var parsedWorkers) ? parsedWorkers : config.WebUiMaxWorkers;
            var allowLan = window.AllowLan.IsChecked == true;
            var enableIpv6 = window.EnableIpv6.IsChecked == true;
            if (config.WebUiInputDirectory == input && config.WebUiOutputDirectory == output && config.WebUiPort == port &&
                config.WebUiMaxWorkers == workers && config.WebUiAllowLan == allowLan && config.WebUiEnableIpv6 == enableIpv6)
                return;
            config.WebUiInputDirectory = input;
            config.WebUiOutputDirectory = output;
            config.WebUiPort = port;
            config.WebUiMaxWorkers = workers;
            config.WebUiAllowLan = allowLan;
            config.WebUiEnableIpv6 = enableIpv6;
            configService.Save();
        };
        window.CloseDialog.Click += (_, _) => window.Close();
        window.BrowseSource.Click += async (_, _) => await BrowseFolderAsync(window, strings.String_WebUi_DefaultInputFolder, window.SourcePath);
        window.BrowseDestination.Click += async (_, _) => await BrowseFolderAsync(window, strings.String_WebUi_OutputRoot, window.DestinationPath);
        // 即时更新账号目录预览，避免切换页签时仍显示尚未派发的旧路径。
        window.SourcePath.PropertyChanged += (_, args) =>
        {
            if (args.Property == TextBox.TextProperty && server is null)
                RefreshUsers();
        };
        window.DestinationPath.PropertyChanged += (_, args) =>
        {
            if (args.Property == TextBox.TextProperty && server is null)
                RefreshUsers();
        };
        window.Accounts.SelectionChanged += (_, _) =>
        {
            if (!updating)
            {
                directoryError = string.Empty;
                RenderDetails();
            }
        };
        window.SettingsTabs.SelectionChanged += (_, _) =>
        {
            window.StartStop.IsDefault = window.SettingsTabs.SelectedIndex == 0;
            window.SaveUserDirectory.IsDefault = window.SettingsTabs.SelectedIndex == 1;
        };
        window.UseDefaultSource.IsCheckedChanged += (_, _) =>
        {
            if (updating || Selected() is not { } selected || Account(selected.Id) is not { } account)
                return;
            drafts[selected.Id] = Draft(account) with
            {
                UseDefaultSource = window.UseDefaultSource.IsChecked == true
            };
            directoryError = string.Empty;
            ShowStatus(null);
            RenderDetails();
        };
        // 同步记录文本属性，避免粘贴后立即保存或切换账号时丢失尚未派发的 TextChanged。
        window.UserSourcePath.PropertyChanged += (_, args) =>
        {
            if (args.Property != TextBox.TextProperty || updating || Selected() is not { } selected || Account(selected.Id) is not { } account || window.UseDefaultSource.IsChecked == true)
                return;
            drafts[selected.Id] = Draft(account) with
            {
                SourcePath = window.UserSourcePath.Text ?? string.Empty
            };
            directoryError = string.Empty;
            ShowStatus(null);
            RenderDetails(keepInput: true);
        };
        window.BrowseUserSource.Click += async (_, _) =>
        {
            if (busy || Selected() is not { } selected || Account(selected.Id) is not { } account)
                return;
            var chosen = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = strings.String_WebUi_UserInputFolder });
            if (chosen.FirstOrDefault()?.TryGetLocalPath() is not { } path)
                return;
            drafts[selected.Id] = Draft(account) with { UseDefaultSource = false, SourcePath = path };
            directoryError = string.Empty;
            ShowStatus(null);
            RenderDetails();
        };
        window.UseDefaultOutput.IsCheckedChanged += (_, _) =>
        {
            if (updating || Selected() is not { } selected || Account(selected.Id) is not { } account)
                return;
            drafts[selected.Id] = Draft(account) with { UseDefaultOutput = window.UseDefaultOutput.IsChecked == true };
            directoryError = string.Empty;
            ShowStatus(null);
            RenderDetails();
        };
        window.UserOutput.PropertyChanged += (_, args) =>
        {
            if (args.Property != TextBox.TextProperty || updating || Selected() is not { } selected ||
                Account(selected.Id) is not { } account || window.UseDefaultOutput.IsChecked == true)
                return;
            drafts[selected.Id] = Draft(account) with { OutputPath = window.UserOutput.Text ?? string.Empty };
            directoryError = string.Empty;
            ShowStatus(null);
            RenderDetails(keepOutput: true);
        };
        window.BrowseUserOutput.Click += async (_, _) =>
        {
            if (busy || Selected() is not { } selected || Account(selected.Id) is not { } account)
                return;
            var chosen = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = strings.String_WebUi_UserOutput });
            if (chosen.FirstOrDefault()?.TryGetLocalPath() is not { } path)
                return;
            drafts[selected.Id] = Draft(account) with { UseDefaultOutput = false, OutputPath = path };
            directoryError = string.Empty;
            ShowStatus(null);
            RenderDetails();
        };
        window.CancelUserDirectory.Click += (_, _) =>
        {
            if (Selected() is not { } selected)
                return;
            drafts.Remove(selected.Id);
            directoryError = string.Empty;
            RenderDetails();
        };
        window.SaveUserDirectory.Click += async (_, _) =>
        {
            if (Selected() is not { } selected || Account(selected.Id) is not { } account)
                return;
            var draft = Draft(account);
            await RunAsync(async () =>
            {
                directoryError = string.Empty;
                var source = draft.UseDefaultSource ? DefaultInput() : draft.SourcePath.Trim();
                var output = draft.UseDefaultOutput ? DefaultOutput() : draft.OutputPath.Trim();
                if (string.IsNullOrEmpty(DefaultInput()))
                {
                    directoryError = strings.String_WebUi_DirectoryMissingDefault;
                    return;
                }
                if (string.IsNullOrEmpty(DefaultOutput()))
                {
                    directoryError = strings.String_WebUi_DirectoryMissingOutput;
                    return;
                }
                if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(output) ||
                    !Path.IsPathFullyQualified(source) || !Path.IsPathFullyQualified(output))
                {
                    directoryError = strings.String_WebUi_DirectoryInvalid;
                    return;
                }
                MediaWorkspace resolved;
                var requested = account with
                {
                    InputDirectory = draft.UseDefaultSource ? null : source,
                    OutputDirectory = draft.UseDefaultOutput ? null : output
                };
                try
                {
                    var defaults = await CreateWorkspaceAsync(DefaultInput(), DefaultOutput());
                    var accounts = config.WebUiAccounts.ToArray();
                    resolved = await Task.Run(() => WebUiServer.CreateWorkspace(defaults, requested, accounts));
                }
                catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
                {
                    directoryError = WorkspaceErrorText(error.InnerException ?? error);
                    return;
                }
                var configuredInput = draft.UseDefaultSource ? null : resolved.InputDirectory;
                var configuredOutput = draft.UseDefaultOutput ? null : resolved.OutputDirectory;
                if (selected.Running && (!SameDirectory(selected.InputDirectory, resolved.InputDirectory) ||
                    !SameDirectory(selected.OutputDirectory, resolved.OutputDirectory)) &&
                    !await ConfirmDirectoryChangeAsync(window, account.UserName, resolved.InputDirectory, resolved.OutputDirectory))
                    return;
                WebUiDirectoryChange result;
                if (server is not null)
                {
                    result = await server.UpdateDirectoriesAsync(account.Id, configuredInput, configuredOutput);
                    if (result is WebUiDirectoryChange.Exporting or WebUiDirectoryChange.Busy)
                    {
                        directoryError = result == WebUiDirectoryChange.Exporting ? strings.String_WebUi_DirectoryExportBlocked : strings.String_WebUi_DirectoryBusy;
                        return;
                    }
                }
                else
                {
                    logins?.SetDirectories(account.Id, configuredInput, configuredOutput);
                    result = WebUiDirectoryChange.Applied;
                }
                var index = config.WebUiAccounts.FindIndex(item => item.Id == account.Id);
                if (index < 0)
                    return;
                config.WebUiAccounts[index] = config.WebUiAccounts[index] with
                {
                    InputDirectory = configuredInput,
                    OutputDirectory = configuredOutput
                };
                drafts.Remove(account.Id);
                configService.Save();
                ShowStatus(string.Format(result == WebUiDirectoryChange.Restarted ? strings.String_WebUi_DirectoryUpdatedReconnect : strings.String_WebUi_DirectorySaved, account.UserName));
            });
        };
        window.AddAccount.Click += async (_, _) =>
        {
            if (busy)
                return;
            if (config.WebUiAccounts.Count >= 32)
            {
                ShowStatus(strings.String_WebUi_DuplicateAccount, true);
                return;
            }
            var entry = await AskAccountAsync(window, config.WebUiAccounts.Select(account => account.UserName).ToArray());
            if (entry is null)
                return;
            await RunAsync(async () =>
            {
                var hash = await Task.Run(() => WebUiPassword.Hash(entry.Value.Password));
                var account = new WebUiAccount { UserName = entry.Value.Name, PasswordHash = hash };
                config.WebUiAccounts.Add(account);
                selectAfterRefresh = account.Id;
                await SaveAccountsAsync();
            });
        };
        window.ResetPassword.Click += async (_, _) =>
        {
            if (Selected() is not { } selected || busy)
                return;
            var password = await AskPasswordAsync(window, selected.UserName);
            if (password is null)
                return;
            await RunAsync(async () =>
            {
                var hash = await Task.Run(() => WebUiPassword.Hash(password));
                var index = config.WebUiAccounts.FindIndex(account => account.Id == selected.Id);
                if (index >= 0)
                    config.WebUiAccounts[index] = config.WebUiAccounts[index] with
                    {
                        PasswordHash = hash
                    };
                await SaveAccountsAsync();
            });
        };
        window.ToggleAccount.Click += async (_, _) =>
        {
            if (Selected() is not { } selected)
                return;
            if (selected.Enabled && !await ConfirmWorkspaceStopAsync(window, selected.UserName))
                return;
            await RunAsync(async () =>
            {
                var index = config.WebUiAccounts.FindIndex(account => account.Id == selected.Id);
                if (index >= 0)
                    config.WebUiAccounts[index] = config.WebUiAccounts[index] with
                    {
                        Enabled = !selected.Enabled
                    };
                await SaveAccountsAsync();
            });
        };
        window.RemoveAccount.Click += async (_, _) =>
        {
            if (Selected() is not { } selected || !await ConfirmWorkspaceStopAsync(window, selected.UserName))
                return;
            await RunAsync(async () => { config.WebUiAccounts.RemoveAll(account => account.Id == selected.Id); await SaveAccountsAsync(); });
        };
        window.StopWorker.Click += async (_, _) =>
        {
            if (Selected() is not { } selected || !await ConfirmWorkspaceStopAsync(window, selected.UserName))
                return;
            await RunAsync(async () =>
            {
                if (server is not null)
                    await server.StopWorkerAsync(selected.Id);
            });
        };
        window.StartStop.Click += async (_, _) => await RunAsync(async () =>
        {
            if (server is not null)
            {
                await server.DisposeAsync();
                server = null;
                links = [];
                RunningChanged?.Invoke();
                return;
            }
            if (!config.WebUiAccounts.Any(account => account.Enabled))
                throw new ArgumentException(strings.String_WebUi_NoAccounts);
            if (!ReadNumber(window.ListenPort, 1024, 65535, out var port) || !ReadNumber(window.MaxWorkers, 1, 16, out var workers))
                throw new ArgumentException(strings.String_WebUi_InvalidLaunch);
            var workspace = await CreateWorkspaceAsync(DefaultInput(), DefaultOutput());
            config.WebUiInputDirectory = workspace.InputDirectory;
            config.WebUiOutputDirectory = workspace.OutputDirectory;
            config.WebUiPort = port;
            config.WebUiAllowLan = window.AllowLan.IsChecked == true;
            config.WebUiEnableIpv6 = window.EnableIpv6.IsChecked == true;
            config.WebUiMaxWorkers = workers;
            configService.Save();
            if (previousWorkspace is null || !MediaFileUtil.PathsEqual(previousWorkspace.InputDirectory, workspace.InputDirectory) ||
                !MediaFileUtil.PathsEqual(previousWorkspace.OutputDirectory, workspace.OutputDirectory))
                logins = new WebUiLoginStore();
            previousWorkspace = workspace;
            logins!.UpdateAccounts(config.WebUiAccounts);
            var editor = owner.DataContext as MainWindowViewModel;
            var theme = ThemeModel.AllThemes.Concat(ThemeModel.AllDarkThemes).FirstOrDefault(item => item.Variant == Avalonia.Application.Current?.ActualThemeVariant);
            var next = new WebUiServer(new WebUiServerOptions(workspace, port, config.WebUiAllowLan, config.WebUiMaxWorkers,
                WebUiPreferences.CreateSnapshot(config, editor?.CutMode, editor?.ClipMode), theme?.Name, config.Language, theme?.IsDarkTheme == true, EnableIpv6: config.WebUiEnableIpv6), logins);
            next.UsersChanged += OnUsersChanged;
            next.PreferencesChanged += OnPreferencesChanged;
            try
            {
                await next.StartAsync();
            }
            catch { await next.DisposeAsync(); throw; }
            server = next;
            links = WebUiHttp.GetAddresses(config.WebUiAllowLan, config.WebUiEnableIpv6).Select(address => new UriBuilder("http", address.ToString(), next.Port).Uri.AbsoluteUri).ToArray();
            RunningChanged?.Invoke();
        });
        window.CopyLink.Click += async (_, _) =>
        {
            if (window.Addresses.SelectedItem is string link && window.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(link);
                ShowStatus(strings.String_WebUi_LinkCopied);
            }
        };
        panel = window;
        UsersChanged += RefreshUsers;
        ShowStatus(null);
        Refresh();
        try
        {
            await window.ShowDialog(owner);
        }
        finally { UsersChanged -= RefreshUsers; panel = null; }
    }

    public static Task<bool> ConfirmOwnerCloseAsync(Window owner)
    {
        var strings = LocalizationManager.Instance;
        var exit = new Button { Content = strings.String_WebUi_ExitAndStop, MinWidth = 90 };
        var cancel = new Button { Content = strings.String_Cancel, MinWidth = 80, IsDefault = true, IsCancel = true };
        var dialog = new ClassicWindow
        {
            Title = strings.String_WebUi_Title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            CanMaximize = false,
            CanMinimize = false,
            ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = strings.String_WebUi_ExitWarning, TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { exit, cancel }
                    }
                }
            }
        };
        exit.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Opened += (_, _) => cancel.Focus();
        return dialog.ShowDialog<bool>(owner);
    }

    private static async Task BrowseFolderAsync(Window window, string label, TextBox path)
    {
        var items = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = label });
        if (items.FirstOrDefault()?.TryGetLocalPath() is { } selected)
            path.Text = selected;
    }

    private static async Task<string?> AskPasswordAsync(Window owner, string userName)
    {
        var strings = LocalizationManager.Instance;
        var password = new TextBox { PasswordChar = '●', MaxLength = 128, MinWidth = 280 };
        // macOS 远程键盘可能不提供有效扫描码，需要保留原生文本输入通道。
        InputMethod.SetIsInputMethodEnabled(password, OperatingSystem.IsMacOS());
        var apply = new Button { Content = strings.String_WebUi_ResetPassword, MinWidth = 100, IsDefault = true, IsEnabled = false };
        var cancel = new Button { Content = strings.String_Cancel, IsCancel = true, MinWidth = 80 };
        var dialog = new ClassicWindow
        {
            Title = strings.String_WebUi_ResetPassword,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            CanMaximize = false,
            CanMinimize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 12,
                Children =
            {
                new TextBlock { Text = userName, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = strings.String_WebUi_ResetPasswordHint, TextWrapping = TextWrapping.Wrap }, password,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { apply, cancel } }
            }
            }
        };
        password.PropertyChanged += (_, args) =>
        {
            if (args.Property == TextBox.TextProperty)
                apply.IsEnabled = password.Text?.Length is >= 8 and <= 128;
        };
        dialog.Opened += (_, _) => password.Focus();
        apply.Click += (_, _) =>
        {
            if (password.Text?.Length is >= 8 and <= 128)
                dialog.Close(password.Text);
        };
        cancel.Click += (_, _) => dialog.Close();
        return await dialog.ShowDialog<string?>(owner);
    }

    private static Task<bool> ConfirmWorkspaceStopAsync(Window owner, string userName)
    {
        var strings = LocalizationManager.Instance;
        var confirm = new Button { Content = strings.String_WebUi_Confirm, MinWidth = 80 };
        var cancel = new Button { Content = strings.String_Cancel, IsCancel = true, IsDefault = true, MinWidth = 80 };
        var dialog = new ClassicWindow
        {
            Title = strings.String_WebUi_Accounts,
            Width = 430,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            CanMaximize = false,
            CanMinimize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 14,
                Children =
            {
                new TextBlock { Text = string.Format(strings.String_WebUi_StopUserWarning, userName), TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { confirm, cancel } }
            }
            }
        };
        confirm.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        return dialog.ShowDialog<bool>(owner);
    }

    private static void OnUsersChanged() => Dispatcher.UIThread.Post(() => UsersChanged?.Invoke());

    private static void OnPreferencesChanged(string id, string preferences) => Dispatcher.UIThread.Post(() =>
    {
        if (App.ConfigService is not { } configService)
            return;
        var accounts = configService.CurrentConfig.WebUiAccounts;
        var index = accounts.FindIndex(account => account.Id == id);
        if (index < 0)
            return;
        accounts[index] = accounts[index] with
        {
            Preferences = preferences
        };
        configService.Save();
    });

    internal static void UpdateTheme(string theme)
    {
        var model = ThemeModel.AllThemes.Concat(ThemeModel.AllDarkThemes).FirstOrDefault(item => item.Name == theme);
        server?.UpdateTheme(theme, model?.IsDarkTheme == true);
    }

    public static void StopWithOwner() => server?.StopWithOwner();
}
