using System.Text.Json;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Utils.Application;
using Xunit;

namespace TSCutter.GUI.Tests.Application;

[Collection("Avalonia UI")]
public sealed class RemoteEditingConfigurationTests
{
    [Fact]
    public void RemoteSettingsAreIndependentOfDesktopConfigAndRoundTripAccounts()
    {
        using var files = new ConfigurationFiles();
        var service = files.Create();
        var config = service.CurrentConfig;
        config.WebUiInputDirectory = Path.Combine(files.Root.FullName, "media");
        config.WebUiOutputDirectory = Path.Combine(files.Root.FullName, "exports");
        config.WebUiPort = 12345;
        Assert.False(config.WebUiEnableIpv6);
        Assert.True(config.WebUiAllowLan);
        config.WebUiAllowLan = false;
        config.WebUiEnableIpv6 = true;
        config.WebUiMaxWorkers = 7;
        var account = new WebUiAccount { UserName = "alice", PasswordHash = "test-hash", InputDirectory = "account-media", OutputDirectory = "account-exports" };
        config.WebUiAccounts = [account];
        service.Save();
        using (var desktop = JsonDocument.Parse(File.ReadAllText(files.Path)))
            Assert.DoesNotContain(desktop.RootElement.EnumerateObject(), property => property.Name.StartsWith("WebUi", StringComparison.Ordinal));
        var remote = JsonSerializer.Deserialize(File.ReadAllText(files.RemotePath), AppJsonContext.Default.RemoteEditingConfig)!;
        Assert.Equal(config.WebUiInputDirectory, remote.InputDirectory);
        Assert.Equal(config.WebUiOutputDirectory, remote.OutputDirectory);
        Assert.True(remote.EnableIpv6);
        Assert.Equal(account, Assert.Single(remote.Accounts));

        // 另一个构建只重写普通配置，远程配置仍能独立读取；普通文件损坏也不影响它。
        foreach (var desktopJson in new[] { "{}", "{ damaged json" })
        {
            File.WriteAllText(files.Path, desktopJson);
            var reopened = files.Create();
            reopened.Load();
            Assert.Equal(config.WebUiInputDirectory, reopened.CurrentConfig.WebUiInputDirectory);
            Assert.Equal(config.WebUiOutputDirectory, reopened.CurrentConfig.WebUiOutputDirectory);
            Assert.Equal(12345, reopened.CurrentConfig.WebUiPort);
            Assert.False(reopened.CurrentConfig.WebUiAllowLan);
            Assert.True(reopened.CurrentConfig.WebUiEnableIpv6);
            Assert.Equal(7, reopened.CurrentConfig.WebUiMaxWorkers);
            Assert.Equal(account, Assert.Single(reopened.CurrentConfig.WebUiAccounts));
        }
        Assert.Empty(Directory.GetFiles(files.Root.FullName, "*.tmp"));
    }

    [Fact]
    public void SavingOtherPreferencesDoesNotOverwriteAnotherClientsRemoteSettings()
    {
        using var files = new ConfigurationFiles();
        files.Create().Save();
        var first = files.Create();
        var second = files.Create();
        first.Load();
        second.Load();
        first.CurrentConfig.WebUiInputDirectory = Path.Combine(files.Root.FullName, "media");
        first.CurrentConfig.WebUiOutputDirectory = Path.Combine(files.Root.FullName, "exports");
        first.Save();
        var saved = File.ReadAllBytes(files.RemotePath);
        second.CurrentConfig.ClipOutputTemplate = "another-client";
        second.Save();
        Assert.Equal(saved, File.ReadAllBytes(files.RemotePath));
        var reopened = files.Create();
        reopened.Load();
        Assert.Equal(first.CurrentConfig.WebUiInputDirectory, reopened.CurrentConfig.WebUiInputDirectory);
        Assert.Equal(first.CurrentConfig.WebUiOutputDirectory, reopened.CurrentConfig.WebUiOutputDirectory);
    }

    [Fact]
    public void MemoryOnlyWorkersNeitherReadNorWriteEitherConfigurationFile()
    {
        using var files = new ConfigurationFiles();
        var desktop = files.Create();
        desktop.CurrentConfig.WebUiInputDirectory = "desktop-media";
        desktop.Save();
        var mainBytes = File.ReadAllBytes(files.Path);
        var remoteBytes = File.ReadAllBytes(files.RemotePath);
        var worker = files.Create(persistent: false);
        worker.CurrentConfig.WebUiInputDirectory = "worker-only";
        worker.Load();
        Assert.Equal("worker-only", worker.CurrentConfig.WebUiInputDirectory);
        worker.Save();
        Assert.Equal(mainBytes, File.ReadAllBytes(files.Path));
        Assert.Equal(remoteBytes, File.ReadAllBytes(files.RemotePath));
    }

    private sealed class ConfigurationFiles : IDisposable
    {
        public DirectoryInfo Root { get; } = Directory.CreateTempSubdirectory("tscutter-config-");
        public string Path => System.IO.Path.Combine(Root.FullName, "config.json");
        public string RemotePath => System.IO.Path.Combine(Root.FullName, "remote-editing.json");
        public ConfigurationService Create(bool persistent = true) => new(new TestLocalization(), Path, persistent,
            new AppConfig { Language = "en-US", AutoDetectLanguage = false, ThemeName = "", DarkThemeName = "" });
        public void Dispose() => Root.Delete(recursive: true);
    }

    private sealed class TestLocalization : ILocalizationService
    {
        public List<SupportedLang> SupportedLanguages { get; } = [];
        public string CurrentLanguageCode => "en-US";
        public void SwitchLanguage(string code)
        {
        }
        public string GetString(string key) => key;
        public event Action? LanguageChanged { add { } remove { } }
    }
}
