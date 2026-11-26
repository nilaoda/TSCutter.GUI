using System;
using System.Collections.Generic;
using System.IO;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Services.Application;
using Xunit;

namespace TSCutter.GUI.Tests.Application;

[CollectionDefinition("Portable paths", DisableParallelization = true)]
public sealed class PortablePathsCollection
{
}

[Collection("Portable paths")]
public sealed class AppDataPathsTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("tscutter-portable-");

    public AppDataPathsTests()
    {
        AppDataPaths.InstallRootOverride = Path.Combine(_root.FullName, "install");
        AppDataPaths.ApplicationDataRootOverride = Path.Combine(_root.FullName, "appdata");
        AppDataPaths.LocalApplicationDataRootOverride = Path.Combine(_root.FullName, "localappdata");
        Directory.CreateDirectory(AppDataPaths.InstallRoot);
        Directory.CreateDirectory(AppDataPaths.ApplicationDataRootOverride!);
        Directory.CreateDirectory(AppDataPaths.LocalApplicationDataRootOverride!);
    }

    public void Dispose()
    {
        AppDataPaths.InstallRootOverride = null;
        AppDataPaths.ApplicationDataRootOverride = null;
        AppDataPaths.LocalApplicationDataRootOverride = null;
        _root.Delete(recursive: true);
    }

    private static string ProfileConfig(string root, string file) =>
        Path.Combine(root, "TSCutter.GUI", file);

    [Fact]
    public void WithoutMarker_UsesProfileFolders()
    {
        Assert.False(AppDataPaths.IsPortable);
        Assert.Equal(ProfileConfig(AppDataPaths.ApplicationDataRootOverride!, "config.json"),
            AppDataPaths.GetPath(Environment.SpecialFolder.ApplicationData, "config.json"));
        Assert.Equal(ProfileConfig(AppDataPaths.LocalApplicationDataRootOverride!, "Recovery"),
            AppDataPaths.GetPath(Environment.SpecialFolder.LocalApplicationData, "Recovery"));
    }

    [Fact]
    public void WithMarker_RedirectsBothProfileRootsToTheDataFolder()
    {
        Assert.Equal("TSCutter.GUI.portable", AppDataPaths.MarkerFileName);
        File.WriteAllText(Path.Combine(AppDataPaths.InstallRoot, AppDataPaths.MarkerFileName), "");

        Assert.True(AppDataPaths.IsPortable);
        Assert.Equal(Path.Combine(AppDataPaths.DataDirectory, "config.json"),
            AppDataPaths.GetPath(Environment.SpecialFolder.ApplicationData, "config.json"));
        Assert.Equal(Path.Combine(AppDataPaths.DataDirectory, "Recovery"),
            AppDataPaths.GetPath(Environment.SpecialFolder.LocalApplicationData, "Recovery"));
    }

    [Fact]
    public void DeletingMarker_RestoresProfileFoldersAndData()
    {
        var marker = Path.Combine(AppDataPaths.InstallRoot, AppDataPaths.MarkerFileName);
        File.WriteAllText(marker, "");
        AppDataPaths.Initialize();
        File.WriteAllText(Path.Combine(AppDataPaths.DataDirectory, "config.json"), "portable settings");
        Assert.True(AppDataPaths.IsPortable);

        File.Delete(marker);
        AppDataPaths.Initialize();

        Assert.False(AppDataPaths.IsPortable);
        Assert.Equal("portable settings", File.ReadAllText(ProfileConfig(
            AppDataPaths.ApplicationDataRootOverride!, "config.json")));
        Assert.True(File.Exists(AppDataPaths.OptOutFilePath));
        Assert.Equal(ProfileConfig(AppDataPaths.LocalApplicationDataRootOverride!, "update.lock"),
            AppDataPaths.GetPath(Environment.SpecialFolder.LocalApplicationData, "update.lock"));
    }

    [Fact]
    public void AddingMarkerAgainClearsTheOptOut()
    {
        Directory.CreateDirectory(AppDataPaths.DataDirectory);
        File.WriteAllText(AppDataPaths.OptOutFilePath, "");
        Assert.True(AppDataPaths.IsOptedOut);

        File.WriteAllText(Path.Combine(AppDataPaths.InstallRoot, AppDataPaths.MarkerFileName), "");
        AppDataPaths.Initialize();

        Assert.False(AppDataPaths.IsOptedOut);
        Assert.True(AppDataPaths.IsPortable);
    }

    [Fact]
    public void PortableDataFilesArePrivateOnUnix()
    {
        if (OperatingSystem.IsWindows())
            return;
        var profileConfig = Path.Combine(AppDataPaths.ApplicationDataRootOverride!, "TSCutter.GUI");
        Directory.CreateDirectory(profileConfig);
        File.WriteAllText(Path.Combine(profileConfig, "config.json"), "settings");
        File.WriteAllText(Path.Combine(AppDataPaths.InstallRoot, AppDataPaths.MarkerFileName), "");
        AppDataPaths.Initialize();

        var config = Path.Combine(AppDataPaths.DataDirectory, "config.json");
        var mode = File.GetUnixFileMode(config);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void EnsurePortableData_CopiesPersistentDataOnceAndLeavesOriginals()
    {
        var configRoot = Path.Combine(AppDataPaths.ApplicationDataRootOverride!, "TSCutter.GUI");
        var localRoot = Path.Combine(AppDataPaths.LocalApplicationDataRootOverride!, "TSCutter.GUI");
        Directory.CreateDirectory(configRoot);
        Directory.CreateDirectory(Path.Combine(localRoot, "Recovery"));
        File.WriteAllText(Path.Combine(configRoot, "config.json"), "config");
        File.WriteAllText(Path.Combine(configRoot, "remote-editing.json"), "remote");
        File.WriteAllText(Path.Combine(configRoot, "saved-frames.json"), "frames");
        File.WriteAllText(Path.Combine(localRoot, "Recovery", "one.tscut-recovery"), "recovery");
        File.WriteAllText(Path.Combine(localRoot, "update.lock"), "stale lock");
        File.WriteAllText(Path.Combine(AppDataPaths.InstallRoot, AppDataPaths.MarkerFileName), "");

        AppDataPaths.Initialize();

        Assert.Equal("config", File.ReadAllText(Path.Combine(AppDataPaths.DataDirectory, "config.json")));
        Assert.Equal("remote", File.ReadAllText(Path.Combine(AppDataPaths.DataDirectory, "remote-editing.json")));
        Assert.Equal("frames", File.ReadAllText(Path.Combine(AppDataPaths.DataDirectory, "saved-frames.json")));
        Assert.Equal("recovery", File.ReadAllText(
            Path.Combine(AppDataPaths.DataDirectory, "Recovery", "one.tscut-recovery")));
        Assert.False(File.Exists(Path.Combine(AppDataPaths.DataDirectory, "update.lock")));
        Assert.True(File.Exists(Path.Combine(configRoot, "config.json")));

        // A second launch must not overwrite portable data with profile data.
        File.WriteAllText(Path.Combine(configRoot, "config.json"), "changed");
        AppDataPaths.Initialize();
        Assert.Equal("config", File.ReadAllText(Path.Combine(AppDataPaths.DataDirectory, "config.json")));
    }

    [Fact]
    public void ConfigurationService_WritesConfigIntoThePortableDataFolder()
    {
        File.WriteAllText(Path.Combine(AppDataPaths.InstallRoot, AppDataPaths.MarkerFileName), "");
        AppDataPaths.Initialize();

        new ConfigurationService(new TestLocalization()).Save();

        Assert.True(File.Exists(Path.Combine(AppDataPaths.DataDirectory, "config.json")));
    }

    [Fact]
    public void UpdateStatePathsFollowPortableMode()
    {
        File.WriteAllText(Path.Combine(AppDataPaths.InstallRoot, AppDataPaths.MarkerFileName), "");
        var previous = UpdateService.UpdateLockFilePathOverride;
        UpdateService.UpdateLockFilePathOverride = null;
        try
        {
            Assert.Equal(Path.Combine(AppDataPaths.DataDirectory, "update.lock"),
                UpdateService.UpdateLockFilePath);

            UpdateService.WriteHelperResult(success: true, "");
            Assert.True(File.Exists(Path.Combine(AppDataPaths.DataDirectory, "UpdateResult.txt")));
            Assert.True(UpdateService.ReadHelperResult()?.Success);
            UpdateService.ClearHelperResult();
        }
        finally
        {
            UpdateService.UpdateLockFilePathOverride = previous;
        }
    }

    private sealed class TestLocalization : ILocalizationService
    {
        public List<SupportedLang> SupportedLanguages => [];
        public string CurrentLanguageCode => "en-US";
        public event Action? LanguageChanged;
        public void SwitchLanguage(string code) => LanguageChanged?.Invoke();
        public string GetString(string key) => key;
    }
}
