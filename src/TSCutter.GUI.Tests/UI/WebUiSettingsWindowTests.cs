using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Classic.Avalonia.Theme;
using Splat;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.Web;
using Xunit;

namespace TSCutter.GUI.Tests.UI;

[Collection("Avalonia UI")]
public sealed class WebUiSettingsWindowTests
{
    [Fact]
    public async Task ClosingWithoutStartingSavesConnectionSettingsAndUpdatesAccountPaths()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var languages = new LocalizationService();
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(languages);
            languages.SwitchLanguage("en-US");
            var account = new WebUiAccount { UserName = "alice", PasswordHash = "unused" };
            var configuration = new SavedConfiguration(new AppConfig { WebUiAccounts = [account] });
            Locator.CurrentMutable.RegisterConstant<IConfigurationService>(configuration);
            var folders = Directory.CreateTempSubdirectory("tscutter-web-settings-");
            var source = folders.CreateSubdirectory("素材").FullName;
            var output = folders.CreateSubdirectory("输出").FullName;
            var owner = new ClassicWindow();
            owner.Show();
            try
            {
                await Open(window =>
                {
                    window.SourcePath.Text = "  " + source + "  ";
                    window.DestinationPath.Text = output;
                    window.ListenPort.Text = "12345";
                    window.MaxWorkers.Text = "7";
                    Assert.False(window.EnableIpv6.IsChecked);
                    Assert.True(window.AllowLan.IsChecked);
                    Assert.True(window.EnableIpv6.IsEnabled);
                    window.EnableIpv6.IsChecked = true;
                    window.AllowLan.IsChecked = false;
                    Assert.False(window.EnableIpv6.IsEnabled);
                    window.SettingsTabs.SelectedIndex = 1;
                    Assert.Equal(source, window.UserSourcePath.Text);
                    Assert.Equal(output, window.UserOutput.Text);
                    Assert.True(window.UseDefaultOutput.IsChecked);
                    Assert.True(window.UserOutput.IsReadOnly);
                    window.UseDefaultOutput.IsChecked = false;
                    window.UserOutput.Text = Path.Combine(output, "custom");
                    Assert.False(window.UserOutput.IsReadOnly);
                    Assert.True(window.SaveUserDirectory.IsEnabled);
                    window.CancelUserDirectory.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.True(window.UseDefaultOutput.IsChecked);
                    Assert.Equal(output, window.UserOutput.Text);
                });
                Assert.False(WebUiLauncher.IsRunning);
                Assert.Single(configuration.CurrentConfig.WebUiAccounts);
                Assert.Equal(12345, configuration.CurrentConfig.WebUiPort);
                Assert.Equal(7, configuration.CurrentConfig.WebUiMaxWorkers);
                Assert.False(configuration.CurrentConfig.WebUiAllowLan);
                Assert.True(configuration.CurrentConfig.WebUiEnableIpv6);
                Assert.Equal(source, configuration.CurrentConfig.WebUiInputDirectory);
                Assert.Equal(output, configuration.CurrentConfig.WebUiOutputDirectory);
                Assert.Equal(1, configuration.Saves);

                // 用保存后的配置重新创建服务，覆盖应用重启后的回填，不写入用户的真实配置文件。
                configuration = configuration.Reload();
                Locator.CurrentMutable.RegisterConstant<IConfigurationService>(configuration);
                await Open(window =>
                {
                    Assert.Equal(source, window.SourcePath.Text);
                    Assert.Equal(output, window.DestinationPath.Text);
                    Assert.Equal(12345, window.ListenPort.Value);
                    Assert.Equal(7, window.MaxWorkers.Value);
                    Assert.False(window.AllowLan.IsChecked);
                    Assert.True(window.EnableIpv6.IsChecked);
                    Assert.False(window.EnableIpv6.IsEnabled);
                });
                Assert.Equal(0, configuration.Saves);
                var customOutput = folders.CreateSubdirectory("独立输出").FullName;
                await Open(window =>
                {
                    window.SettingsTabs.SelectedIndex = 1;
                    window.UseDefaultOutput.IsChecked = false;
                    window.UserOutput.Text = customOutput;
                    window.SaveUserDirectory.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }, async window =>
                {
                    await WaitForSave(window);
                    Assert.Equal(customOutput, Assert.Single(configuration.Reload().CurrentConfig.WebUiAccounts).OutputDirectory);
                    window.UseDefaultOutput.IsChecked = true;
                    window.SaveUserDirectory.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await WaitForSave(window);
                    Assert.Null(Assert.Single(configuration.Reload().CurrentConfig.WebUiAccounts).OutputDirectory);
                });
                Assert.Equal(2, configuration.Saves);
                await Open(window =>
                {
                    window.SourcePath.Text = "  ";
                    window.DestinationPath.Text = string.Empty;
                    window.ListenPort.Text = "invalid";
                    window.MaxWorkers.Text = string.Empty;
                    window.SettingsTabs.SelectedIndex = 1;
                    Assert.Equal(string.Empty, window.UserOutput.Text);
                });
                Assert.Equal(3, configuration.Saves);
                var cleared = configuration.Reload().CurrentConfig;
                Assert.Null(cleared.WebUiInputDirectory);
                Assert.Null(cleared.WebUiOutputDirectory);
                Assert.Equal(12345, cleared.WebUiPort);
                Assert.Equal(7, cleared.WebUiMaxWorkers);
                return true;
            }
            finally
            {
                owner.Close();
                folders.Delete(recursive: true);
            }

            async Task WaitForSave(WebUiSettingsWindow window)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (window.ServiceProgress.IsVisible)
                    await Task.Delay(10, timeout.Token);
                Assert.False(window.DirectoryError.IsVisible, window.DirectoryError.Text);
            }

            async Task Open(Action<WebUiSettingsWindow> use, Func<WebUiSettingsWindow, Task>? after = null)
            {
                var showing = WebUiLauncher.ShowAsync(owner);
                var window = owner.OwnedWindows.OfType<WebUiSettingsWindow>().Single();
                try
                {
                    use(window);
                    if (after is not null)
                        await after(window);
                }
                finally { window.Close(); await showing; }
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AccountPasswordDialogsValidateImmediatelyAndUsePlatformTextInputInBothWindowsThemes()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var languages = new LocalizationService();
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(languages);
            languages.SwitchLanguage("en-US");
            var initialHash = WebUiPassword.Hash("original-password");
            var owner = new ClassicWindow();
            owner.Show();
            var originalTheme = Avalonia.Application.Current!.RequestedThemeVariant;
            try
            {
                foreach (var theme in new[] { TSCutter.GUI.Themes.CustomTheme.Windows10, TSCutter.GUI.Themes.CustomTheme.Windows11 })
                {
                    Avalonia.Application.Current.RequestedThemeVariant = theme;
                    var configuration = new SavedConfiguration(new AppConfig
                    {
                        WebUiAccounts = [new WebUiAccount { UserName = "alice", PasswordHash = initialHash }]
                    });
                    Locator.CurrentMutable.RegisterConstant<IConfigurationService>(configuration);
                    var showing = WebUiLauncher.ShowAsync(owner);
                    var settings = owner.OwnedWindows.OfType<WebUiSettingsWindow>().Single();
                    try
                    {
                        settings.SettingsTabs.SelectedIndex = 1;
                        foreach (var button in new[] { settings.AddAccount, settings.ResetPassword })
                        {
                            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            var dialog = settings.OwnedWindows.Single(window => window.IsVisible);
                            try
                            {
                                var password = dialog.GetVisualDescendants().OfType<TextBox>().First(box => box.PasswordChar != default);
                                password.Focus();
                                Assert.Equal(OperatingSystem.IsMacOS(), InputMethod.GetIsInputMethodEnabled(password));
                                Assert.All(password.GetVisualDescendants().OfType<TextBox>(), box => Assert.Equal(OperatingSystem.IsMacOS(), InputMethod.GetIsInputMethodEnabled(box)));
                                var apply = dialog.GetVisualDescendants().OfType<Button>().Single(button => button.IsDefault);
                                var name = dialog.GetVisualDescendants().OfType<TextBox>().SingleOrDefault(box => box.Name == "AccountUserName");
                                if (name is not null)
                                    name.Text = "new-user";
                                password.Text = "valid-password";
                                var confirmation = dialog.GetVisualDescendants().OfType<TextBox>().SingleOrDefault(box => box.Name == "AccountPasswordConfirmation");
                                if (confirmation is not null)
                                {
                                    Assert.Equal(OperatingSystem.IsMacOS(), InputMethod.GetIsInputMethodEnabled(confirmation));
                                    Assert.False(apply.IsEnabled);
                                    confirmation.Text = "different-password";
                                    Assert.False(apply.IsEnabled);
                                    Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == languages.GetString("String.WebUi.PasswordMismatch"));
                                    confirmation.Text = password.Text;
                                }
                                Assert.True(apply.IsEnabled);
                                if (name is not null)
                                {
                                    name.Text = "alice";
                                    Assert.False(apply.IsEnabled);
                                    name.Text = "new-user";
                                    Assert.True(apply.IsEnabled);
                                }
                                password.Text = "short";
                                Assert.False(apply.IsEnabled);

                                var submittedPassword = name is null ? "reset-password" : "valid-password";
                                password.Text = submittedPassword;
                                if (confirmation is not null)
                                    confirmation.Text = submittedPassword;
                                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                                while (settings.ServiceProgress.IsVisible)
                                    await Task.Delay(10, timeout.Token);
                                var account = configuration.Reload().CurrentConfig.WebUiAccounts.Single(account => account.UserName == "new-user");
                                Assert.True(WebUiPassword.Verify(submittedPassword, account.PasswordHash));
                                if (name is null)
                                    Assert.False(WebUiPassword.Verify("valid-password", account.PasswordHash));
                            }
                            finally { dialog.Close(); }
                            await Task.Yield();
                        }
                    }
                    finally { settings.Close(); await showing; }
                }
                return true;
            }
            finally { owner.Close(); Avalonia.Application.Current.RequestedThemeVariant = originalTheme; }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task FolderValidationUsesTheSelectedLanguage()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        var root = Directory.CreateTempSubdirectory("tscutter-localized-folders-");
        try
        {
            await session.Dispatch<bool>(async () =>
            {
                using var resolver = new ModernDependencyResolver();
                using var scope = resolver.WithResolver();
                var languages = new LocalizationService();
                Locator.CurrentMutable.RegisterConstant<ILocalizationService>(languages);
                Locator.CurrentMutable.RegisterConstant<IConfigurationService>(new SavedConfiguration(new AppConfig
                {
                    WebUiAccounts = [new WebUiAccount { UserName = "alice", PasswordHash = "unused" }]
                }));
                var input = root.CreateSubdirectory("media").FullName;
                var owner = new ClassicWindow();
                owner.Show();
                try
                {
                    foreach (var language in new[] { "en-US", "zh-CN", "zh-TW" })
                    {
                        languages.SwitchLanguage(language);
                        var showing = WebUiLauncher.ShowAsync(owner);
                        var window = owner.OwnedWindows.OfType<WebUiSettingsWindow>().Single();
                        try
                        {
                            foreach (var (source, output, key) in new[]
                            {
                                (input, Path.Combine(root.FullName, "missing"), "WorkspaceFolderUnavailable")
                            })
                            {
                                window.SourcePath.Text = source;
                                window.DestinationPath.Text = output;
                                window.StartStop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                                while (window.ServiceProgress.IsVisible)
                                    await Task.Delay(10, timeout.Token);
                                Assert.False(WebUiLauncher.IsRunning);
                                Assert.Equal(languages.GetString("String.WebUi." + key), window.Status.Text);
                            }
                        }
                        finally { window.Close(); await showing; }
                    }
                    return true;
                }
                finally { owner.Close(); }
            }, CancellationToken.None);
        }
        finally { root.Delete(recursive: true); }
    }

    private sealed class SavedConfiguration(AppConfig config) : IConfigurationService
    {
        public AppConfig CurrentConfig { get; } = config;
        public int Saves
        {
            get; private set;
        }
        public string? Json
        {
            get; private set;
        }
        public void Load()
        {
        }
        public void Save()
        {
            Json = JsonSerializer.Serialize(RemoteEditingConfig.From(CurrentConfig), AppJsonContext.Default.RemoteEditingConfig);
            ++Saves;
        }
        public SavedConfiguration Reload()
        {
            var config = new AppConfig();
            JsonSerializer.Deserialize(Json!, AppJsonContext.Default.RemoteEditingConfig)!.ApplyTo(config);
            return new SavedConfiguration(config);
        }
        public void ApplyTheme(string theme)
        {
        }
        public void ApplyLanguage(string language)
        {
        }
    }
}
