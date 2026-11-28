using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Web;
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
        AppDataPaths.ResetForTests();
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
        AppDataPaths.ResetForTests();
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
    public void ReadOnlyExistingDataFolderIsMadeWritableAndPreserved()
    {
        if (OperatingSystem.IsWindows())
            return;
        var dataDirectory = AppDataPaths.DataDirectory;
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(Path.Combine(dataDirectory, "stale.txt"), "old");
        var originalMode = File.GetUnixFileMode(dataDirectory);
        File.WriteAllText(Path.Combine(AppDataPaths.InstallRoot, AppDataPaths.MarkerFileName), "");
        try
        {
            File.SetUnixFileMode(dataDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            AppDataPaths.Initialize();

            Assert.True(AppDataPaths.IsPortable);
            Assert.Null(AppDataPaths.FallbackMessage);
            Assert.Equal("old", File.ReadAllText(Path.Combine(dataDirectory, "stale.txt")));
            Assert.True((File.GetUnixFileMode(dataDirectory) & UnixFileMode.UserWrite) != 0);
            Assert.True(File.Exists(Path.Combine(dataDirectory, ".tscutter-portable-ready")));
        }
        finally
        {
            File.SetUnixFileMode(dataDirectory, originalMode);
        }
    }

    [Fact]
    public void UnwritableInstallRootFallsBack()
    {
        if (OperatingSystem.IsWindows())
            return;
        var installRoot = AppDataPaths.InstallRoot;
        var originalMode = File.GetUnixFileMode(installRoot);
        File.WriteAllText(Path.Combine(installRoot, AppDataPaths.MarkerFileName), "");
        try
        {
            File.SetUnixFileMode(installRoot, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            AppDataPaths.Initialize();

            Assert.NotNull(AppDataPaths.FallbackMessage);
            Assert.False(AppDataPaths.IsPortable);
            Assert.Equal(ProfileConfig(AppDataPaths.ApplicationDataRootOverride!, "config.json"),
                AppDataPaths.GetPath(Environment.SpecialFolder.ApplicationData, "config.json"));
        }
        finally
        {
            File.SetUnixFileMode(installRoot, originalMode);
        }
    }

    [Fact]
    public void PartialMigrationIsRetriedAndCompleted()
    {
        var configRoot = Path.Combine(AppDataPaths.ApplicationDataRootOverride!, AppDataPaths.AppDirectoryName);
        Directory.CreateDirectory(configRoot);
        File.WriteAllText(Path.Combine(configRoot, "remote-editing.json"), "remote");
        File.WriteAllText(Path.Combine(configRoot, "saved-frames.json"), "frames");
        File.WriteAllText(Path.Combine(AppDataPaths.InstallRoot, AppDataPaths.MarkerFileName), "");
        Directory.CreateDirectory(AppDataPaths.DataDirectory);
        File.WriteAllText(Path.Combine(AppDataPaths.DataDirectory, "config.json"), "existing config");

        AppDataPaths.Initialize();

        Assert.True(File.Exists(Path.Combine(AppDataPaths.DataDirectory, "remote-editing.json")));
        Assert.True(File.Exists(Path.Combine(AppDataPaths.DataDirectory, "saved-frames.json")));
        Assert.True(File.Exists(Path.Combine(AppDataPaths.DataDirectory, ".tscutter-portable-ready")));
    }

    [Fact]
    public void CompletedReadOnlyDataFallsBackWithoutLosingContents()
    {
        if (OperatingSystem.IsWindows())
            return;
        var dataDirectory = AppDataPaths.DataDirectory;
        File.WriteAllText(Path.Combine(AppDataPaths.InstallRoot, AppDataPaths.MarkerFileName), "");
        AppDataPaths.Initialize();
        var userFile = Path.Combine(dataDirectory, "user.txt");
        File.WriteAllText(userFile, "keep me");
        var originalMode = File.GetUnixFileMode(dataDirectory);
        try
        {
            File.SetUnixFileMode(dataDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            AppDataPaths.Initialize();

            Assert.NotNull(AppDataPaths.FallbackMessage);
            Assert.False(AppDataPaths.IsPortable);
            Assert.True(Directory.Exists(dataDirectory));
            Assert.Equal("keep me", File.ReadAllText(userFile));
        }
        finally
        {
            File.SetUnixFileMode(dataDirectory, originalMode);
        }
    }

    [Fact]
    public void StagingCleanupDoesNotFollowDirectorySymlinks()
    {
        if (OperatingSystem.IsWindows())
            return;
        var external = Path.Combine(_root.FullName, "external");
        Directory.CreateDirectory(external);
        var externalFile = Path.Combine(external, "keep.txt");
        File.WriteAllText(externalFile, "keep");
        var externalDirMode = File.GetUnixFileMode(external);
        var externalFileMode = File.GetUnixFileMode(externalFile);

        var profileConfigRoot = Path.Combine(AppDataPaths.ApplicationDataRootOverride!, "TSCutter.GUI");
        Directory.CreateDirectory(profileConfigRoot);
        File.WriteAllText(Path.Combine(profileConfigRoot, "config.json"), "old");

        var staging = Path.Combine(_root.FullName, "staging-link");
        Directory.CreateDirectory(staging);
        var link = Path.Combine(staging, "link");
        Directory.CreateSymbolicLink(link, external);
        // A directory at a file target forces migration to fail during copying.
        Directory.CreateDirectory(Path.Combine(staging, "config.json"));
        File.WriteAllText(Path.Combine(AppDataPaths.InstallRoot, AppDataPaths.MarkerFileName), "");

        var previous = AppDataPaths.MigrationStagingOverride;
        AppDataPaths.MigrationStagingOverride = staging;
        try
        {
            AppDataPaths.Initialize();

            Assert.NotNull(AppDataPaths.FallbackMessage);
            Assert.True(File.Exists(externalFile));
            Assert.Equal("keep", File.ReadAllText(externalFile));
            Assert.Equal(externalDirMode, File.GetUnixFileMode(external));
            Assert.Equal(externalFileMode, File.GetUnixFileMode(externalFile));
        }
        finally
        {
            AppDataPaths.MigrationStagingOverride = previous;
            if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    [Fact]
    public void FailedMigrationRetryDoesNotOverwriteEdits()
    {
        var configRoot = Path.Combine(AppDataPaths.ApplicationDataRootOverride!, AppDataPaths.AppDirectoryName);
        Directory.CreateDirectory(configRoot);
        File.WriteAllText(Path.Combine(configRoot, "config.json"), "old");
        File.WriteAllText(Path.Combine(configRoot, "remote-editing.json"), "remote");
        File.WriteAllText(Path.Combine(AppDataPaths.InstallRoot, AppDataPaths.MarkerFileName), "");

        var staging = Path.Combine(_root.FullName, "staging");
        var previous = AppDataPaths.MigrationStagingOverride;
        try
        {
            AppDataPaths.MigrationStagingOverride = staging;
            Directory.CreateDirectory(staging);
            // A directory at the copy destination makes File.Copy fail mid-migration.
            Directory.CreateDirectory(Path.Combine(staging, "config.json"));

            AppDataPaths.Initialize();

            Assert.NotNull(AppDataPaths.FallbackMessage);
            Assert.False(AppDataPaths.IsPortable);
            Assert.False(Directory.Exists(AppDataPaths.DataDirectory));

            // The user keeps editing the profile while portable mode is unavailable.
            File.WriteAllText(Path.Combine(configRoot, "config.json"), "edited");

            AppDataPaths.MigrationStagingOverride = null;
            AppDataPaths.ResetForTests();
            AppDataPaths.Initialize();

            Assert.Equal("edited", File.ReadAllText(Path.Combine(AppDataPaths.DataDirectory, "config.json")));
            Assert.True(File.Exists(Path.Combine(AppDataPaths.DataDirectory, ".tscutter-portable-ready")));
        }
        finally
        {
            AppDataPaths.MigrationStagingOverride = previous;
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    [Fact]
    public void UnrelatedDataFolderIsIgnoredInNormalMode()
    {
        var dataDirectory = AppDataPaths.DataDirectory;
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(Path.Combine(dataDirectory, "config.json"), "foreign settings");

        AppDataPaths.Initialize();

        Assert.False(File.Exists(AppDataPaths.OptOutFilePath));
        Assert.False(File.Exists(ProfileConfig(AppDataPaths.ApplicationDataRootOverride!, "config.json")));
    }

    [Fact]
    public void ActiveDataDirectoryFollowsMode()
    {
        Assert.Equal(Path.Combine(AppDataPaths.ApplicationDataRootOverride!, AppDataPaths.AppDirectoryName),
            AppDataPaths.ActiveDataDirectory);

        File.WriteAllText(Path.Combine(AppDataPaths.InstallRoot, AppDataPaths.MarkerFileName), "");
        AppDataPaths.Initialize();

        Assert.Equal(AppDataPaths.DataDirectory, AppDataPaths.ActiveDataDirectory);
    }

    [Fact]
    public void HelperDataDirectoryOverrideRedirectsPaths()
    {
        var helperData = Path.Combine(_root.FullName, "helper-data");
        var previous = UpdateService.UpdateLockFilePathOverride;
        UpdateService.UpdateLockFilePathOverride = null;
        try
        {
            AppDataPaths.UseDataDirectory(helperData);

            Assert.Equal(Path.Combine(helperData, "config.json"),
                AppDataPaths.GetPath(Environment.SpecialFolder.ApplicationData, "config.json"));
            Assert.Equal(helperData, AppDataPaths.ActiveDataDirectory);
            Assert.Equal(Path.Combine(helperData, "update.lock"), UpdateService.UpdateLockFilePath);
        }
        finally
        {
            UpdateService.UpdateLockFilePathOverride = previous;
        }
    }

    [Fact]
    public void HelperResultUsesLocalProfileInNormalMode()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var localRoot = Path.Combine(AppDataPaths.LocalApplicationDataRootOverride!, AppDataPaths.AppDirectoryName);
        var roamingRoot = Path.Combine(AppDataPaths.ApplicationDataRootOverride!, AppDataPaths.AppDirectoryName);
        var localResult = Path.Combine(localRoot, "UpdateResult.txt");
        var roamingResult = Path.Combine(roamingRoot, "UpdateResult.txt");
        Directory.CreateDirectory(localRoot);

        var previousLockPath = UpdateService.UpdateLockFilePathOverride;
        var helperRoot = Directory.CreateTempSubdirectory("tscutter-helper-normal-");
        try
        {
            var install = Path.Combine(helperRoot.FullName, "install");
            var payload = Path.Combine(helperRoot.FullName, "payload");
            var package = Path.Combine(helperRoot.FullName, ".TSCutter.GUI.update-1");
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(payload);
            Directory.CreateDirectory(package);
            var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            var stagedExe = Path.Combine(payload, "TSCutterGUI.exe");
            var targetExe = Path.Combine(install, "TSCutterGUI.exe");
            File.Copy(cmd, stagedExe, overwrite: true);
            File.Copy(cmd, targetExe, overwrite: true);
            UpdateService.UpdateLockFilePathOverride = Path.Combine(localRoot, "update.lock");

            UpdateService.RunWindowsHelper("999999", stagedExe, targetExe, payload, package,
                ["/c", "exit", "0"], localRoot);

            // The helper set a data override; clear it so paths resolve from the
            // local profile again before reading back the result.
            AppDataPaths.ResetForTests();

            Assert.True(UpdateService.ReadHelperResult()?.Success);
            Assert.True(File.Exists(localResult));
            Assert.False(File.Exists(roamingResult));
        }
        finally
        {
            AppDataPaths.ResetForTests();
            UpdateService.UpdateLockFilePathOverride = previousLockPath;
            if (File.Exists(localResult))
                File.Delete(localResult);
            helperRoot.Delete(recursive: true);
        }
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
    public void PortableDataFilesArePrivateOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var profileConfig = Path.Combine(AppDataPaths.ApplicationDataRootOverride!, "TSCutter.GUI");
        Directory.CreateDirectory(profileConfig);
        File.WriteAllText(Path.Combine(profileConfig, "config.json"), "settings");
        File.WriteAllText(Path.Combine(AppDataPaths.InstallRoot, AppDataPaths.MarkerFileName), "");
        AppDataPaths.Initialize();

        var dataDirectory = new DirectoryInfo(AppDataPaths.DataDirectory);
        var file = new FileInfo(Path.Combine(AppDataPaths.DataDirectory, "config.json"));
        Assert.True(dataDirectory.GetAccessControl().AreAccessRulesProtected);
        Assert.True(file.GetAccessControl().AreAccessRulesProtected);
    }

    [Fact]
    public void WebUiSnapshotDoesNotRedirectPortablePaths()
    {
        File.WriteAllText(Path.Combine(AppDataPaths.InstallRoot, AppDataPaths.MarkerFileName), "");
        AppDataPaths.Initialize();
        var snapshot = WebUiPreferences.CreateSnapshot(new AppConfig
        {
            LastImportFolder = Path.GetTempPath()
        });
        var previous = Environment.GetEnvironmentVariable(WebUiPreferences.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(WebUiPreferences.EnvironmentVariable, snapshot);
            Assert.NotNull(WebUiPreferences.ReadSnapshot());
            Assert.Equal(Path.Combine(AppDataPaths.DataDirectory, "config.json"),
                AppDataPaths.GetPath(Environment.SpecialFolder.ApplicationData, "config.json"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(WebUiPreferences.EnvironmentVariable, previous);
        }
    }

    [Fact]
    public void ForeignMachineLockBlocksUpdates()
    {
        var previous = UpdateService.UpdateLockFilePathOverride;
        UpdateService.UpdateLockFilePathOverride = Path.Combine(_root.FullName, "update.lock");
        try
        {
            File.WriteAllText(UpdateService.UpdateLockFilePath, "OTHER-MACHINE|1-123456789");
            Assert.False(UpdateService.TryAcquireUpdateLock());

            UpdateService.ReleaseUpdateLock();
            Assert.True(UpdateService.TryAcquireUpdateLock());
        }
        finally
        {
            UpdateService.UpdateLockFilePathOverride = previous;
        }
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
