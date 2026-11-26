using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Utils.Application;
using Xunit;

namespace TSCutter.GUI.Tests.Application;

public class UpdateServiceTests
{
    private const string PackageName = "TSCutter.GUI_Beta_9.9.9_win-x64.zip";

    [Fact]
    public async Task PrepareAsync_VerifiesChecksumAndExtractsPayload()
    {
        var zip = CreateZip(new Dictionary<string, string>
        {
            ["TSCutterGUI.exe"] = "new executable",
            ["THIRD_PARTY_NOTICES.txt"] = "notices"
        });
        var (root, installRoot) = CreateTempInstall();
        try
        {
            var service = CreateService(zip, installRoot);
            var info = CreateInfo(Convert.ToHexString(SHA256.HashData(zip)), zip.Length);

            var staged = await service.PrepareAsync(info);
            Assert.Equal(installRoot, staged.InstallRoot);
            Assert.Equal(StagedUpdateLayout.WindowsExe, staged.Layout);
            Assert.Equal("new executable", File.ReadAllText(Path.Combine(staged.PayloadDirectory, "TSCutterGUI.exe")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PrepareAsync_StagesOnSameFilesystemAsInstallRoot()
    {
        var zip = CreateZip(new Dictionary<string, string> { ["TSCutterGUI.exe"] = "new" });
        var (root, installRoot) = CreateTempInstall();
        try
        {
            var service = CreateService(zip, installRoot);
            var info = CreateInfo(Convert.ToHexString(SHA256.HashData(zip)), zip.Length);

            var staged = await service.PrepareAsync(info);
            // Windows/Linux replace files inside the install folder; macOS swaps
            // the whole bundle and therefore stages beside it.
            var expectedParent = OperatingSystem.IsMacOS()
                ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(staged.InstallRoot))
                : staged.InstallRoot;
            Assert.Equal(expectedParent, Path.GetDirectoryName(staged.PackageDirectory),
                StringComparer.OrdinalIgnoreCase);
            Assert.StartsWith(".TSCutter.GUI.update-", Path.GetFileName(staged.PackageDirectory));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PrepareAsync_RejectsWrongChecksum()
    {
        var zip = CreateZip(new Dictionary<string, string> { ["TSCutterGUI.exe"] = "new" });
        var (root, installRoot) = CreateTempInstall();
        try
        {
            var service = CreateService(zip, installRoot);
            var info = CreateInfo("0000000000000000000000000000000000000000000000000000000000000000", zip.Length);

            await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(info));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PrepareAsync_RejectsZipSlip()
    {
        var zip = CreateZip(new Dictionary<string, string>
        {
            ["TSCutterGUI.exe"] = "new",
            ["../escaped.txt"] = "bad"
        });
        var (root, installRoot) = CreateTempInstall();
        try
        {
            var service = CreateService(zip, installRoot);
            var info = CreateInfo(Convert.ToHexString(SHA256.HashData(zip)), zip.Length);

            await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(info));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void DetectLayout_RecognizesMacBundle()
    {
        var root = Path.Combine(Path.GetTempPath(), "tscutter-layout-" + Guid.NewGuid().ToString("N"));
        var executable = Path.Combine(root, "TSCutter.GUI.app", "Contents", "MacOS");
        Directory.CreateDirectory(executable);
        File.WriteAllText(Path.Combine(executable, "TSCutterGUI"), "binary");
        try
        {
            Assert.Equal(StagedUpdateLayout.MacAppBundle, UpdateService.DetectLayout(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareAsync_HonorsCancellation()
    {
        var zip = CreateZip(new Dictionary<string, string> { ["TSCutterGUI.exe"] = "new" });
        var (root, installRoot) = CreateTempInstall();
        try
        {
            var service = CreateService(zip, installRoot);
            var info = CreateInfo(Convert.ToHexString(SHA256.HashData(zip)), zip.Length);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.PrepareAsync(info, token: cancellation.Token));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void SwapDirectories_ReplacesWholeTree()
    {
        var root = Directory.CreateTempSubdirectory("tscutter-swap-");
        try
        {
            var source = Path.Combine(root.FullName, "source");
            var target = Path.Combine(root.FullName, "target");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(source, "new.txt"), "new");
            File.WriteAllText(Path.Combine(target, "removed.txt"), "old");

            UpdateService.SwapDirectories(source, target, Path.Combine(root.FullName, "backup"));

            Assert.True(File.Exists(Path.Combine(target, "new.txt")));
            Assert.False(File.Exists(Path.Combine(target, "removed.txt")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void SwapDirectories_RestoresOriginalWhenPostSwapStepFails()
    {
        var root = Directory.CreateTempSubdirectory("tscutter-rollback-");
        try
        {
            var source = Path.Combine(root.FullName, "source");
            var target = Path.Combine(root.FullName, "target");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(source, "a.txt"), "new");
            File.WriteAllText(Path.Combine(target, "a.txt"), "old");

            Assert.Throws<InvalidOperationException>(() => UpdateService.SwapDirectories(
                source, target, Path.Combine(root.FullName, "backup"),
                () => throw new InvalidOperationException("simulated failure")));

            Assert.Equal("old", File.ReadAllText(Path.Combine(target, "a.txt")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void SwapDirectories_CreatesMissingBackupParent()
    {
        var root = Directory.CreateTempSubdirectory("tscutter-backup-parent-");
        try
        {
            var source = Path.Combine(root.FullName, "source");
            var target = Path.Combine(root.FullName, "target");
            var backup = Path.Combine(root.FullName, "rollback", "install");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(source, "a.txt"), "new");
            File.WriteAllText(Path.Combine(target, "old.txt"), "old");

            UpdateService.SwapDirectories(source, target, backup);

            Assert.True(Directory.Exists(backup));
            Assert.Equal("new", File.ReadAllText(Path.Combine(target, "a.txt")));
            Assert.Equal("old", File.ReadAllText(Path.Combine(backup, "old.txt")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void ApplyUnixUpdate_ReplacesOnlyPackageFilesAndCleansUpDefaultBackup()
    {
        var root = Directory.CreateTempSubdirectory("tscutter-linux-apply-");
        try
        {
            var install = Path.Combine(root.FullName, "install");
            var package = Path.Combine(root.FullName, ".TSCutter.GUI.update-1");
            var payload = Path.Combine(package, "payload");
            Directory.CreateDirectory(Path.Combine(install, "notes"));
            Directory.CreateDirectory(payload);
            File.WriteAllText(Path.Combine(install, "TSCutterGUI"), "old exe");
            File.WriteAllText(Path.Combine(install, "THIRD_PARTY_NOTICES.txt"), "old notices");
            File.WriteAllText(Path.Combine(install, "other-tool"), "keep me");
            File.WriteAllText(Path.Combine(install, "notes", "todo.txt"), "keep me too");
            File.WriteAllText(Path.Combine(payload, "TSCutterGUI"), "new exe");
            File.WriteAllText(Path.Combine(payload, "THIRD_PARTY_NOTICES.txt"), "new notices");

            var staged = CreateStagedUpdate(package, payload, install, StagedUpdateLayout.LinuxFolder);
            UpdateService.ApplyUnixUpdate(staged, [], relaunch: false);

            Assert.Equal("new exe", File.ReadAllText(Path.Combine(install, "TSCutterGUI")));
            Assert.Equal("new notices", File.ReadAllText(Path.Combine(install, "THIRD_PARTY_NOTICES.txt")));
            Assert.Equal("keep me", File.ReadAllText(Path.Combine(install, "other-tool")));
            Assert.Equal("keep me too", File.ReadAllText(Path.Combine(install, "notes", "todo.txt")));
            Assert.False(Directory.Exists(Path.Combine(package, "rollback")));
            Assert.Empty(Directory.EnumerateFiles(install, "*.new.*", SearchOption.AllDirectories));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void ApplyUnixUpdate_RestoresOriginalsWhenAFileCannotBeReplaced()
    {
        var root = Directory.CreateTempSubdirectory("tscutter-linux-rollback-");
        try
        {
            var install = Path.Combine(root.FullName, "install");
            var package = Path.Combine(root.FullName, ".TSCutter.GUI.update-1");
            var payload = Path.Combine(package, "payload");
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(Path.Combine(install, "collision"));
            Directory.CreateDirectory(payload);
            File.WriteAllText(Path.Combine(install, "TSCutterGUI"), "old exe");
            File.WriteAllText(Path.Combine(install, "THIRD_PARTY_NOTICES.txt"), "old notices");
            File.WriteAllText(Path.Combine(payload, "TSCutterGUI"), "new exe");
            File.WriteAllText(Path.Combine(payload, "THIRD_PARTY_NOTICES.txt"), "new notices");
            File.WriteAllText(Path.Combine(payload, "AAA-added.txt"), "new file");
            // A directory at the target path makes the final rename fail last.
            File.WriteAllText(Path.Combine(payload, "collision"), "new file");

            var staged = CreateStagedUpdate(package, payload, install, StagedUpdateLayout.LinuxFolder);
            Assert.ThrowsAny<Exception>(() => UpdateService.ApplyUnixUpdate(staged, [], relaunch: false));

            Assert.Equal("old exe", File.ReadAllText(Path.Combine(install, "TSCutterGUI")));
            Assert.Equal("old notices", File.ReadAllText(Path.Combine(install, "THIRD_PARTY_NOTICES.txt")));
            Assert.False(File.Exists(Path.Combine(install, "AAA-added.txt")));
            Assert.True(Directory.Exists(Path.Combine(install, "collision")));
            Assert.False(Directory.Exists(Path.Combine(package, "rollback")));
            Assert.Empty(Directory.EnumerateFiles(install, "*.new.*", SearchOption.AllDirectories));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void UnixUpdate_PassesRelaunchArguments()
    {
        if (OperatingSystem.IsWindows())
            return;
        var root = Directory.CreateTempSubdirectory("tscutter-unix-args-");
        try
        {
            var install = Path.Combine(root.FullName, "install");
            var package = Path.Combine(Path.GetDirectoryName(install)!, ".TSCutter.GUI.update-1");
            var payload = Path.Combine(package, "payload");
            var argsFile = Path.Combine(root.FullName, "args with space.txt");
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(payload);
            File.WriteAllText(Path.Combine(payload, "TSCutterGUI"),
                "#!/bin/sh\nprintf '%s' \"$*\" > \"" + argsFile.Replace("\"", "\\\"") + "\"\n");

            var staged = CreateStagedUpdate(package, payload, install, StagedUpdateLayout.LinuxFolder);
            UpdateService.ApplyUnixUpdate(staged, ["plain", "path with space", "--flag"], relaunch: true);

            Assert.Equal("plain path with space --flag", File.ReadAllText(argsFile));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void UnixUpdate_RollsBackWhenRelaunchedAppExitsWithError()
    {
        if (OperatingSystem.IsWindows())
            return;
        var root = Directory.CreateTempSubdirectory("tscutter-unix-health-");
        try
        {
            var install = Path.Combine(root.FullName, "install");
            var package = Path.Combine(Path.GetDirectoryName(install)!, ".TSCutter.GUI.update-1");
            var payload = Path.Combine(package, "payload");
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(payload);
            File.WriteAllText(Path.Combine(install, "TSCutterGUI"), "#!/bin/sh\nexit 0\n");
            File.WriteAllText(Path.Combine(payload, "TSCutterGUI"), "#!/bin/sh\nexit 1\n");

            var staged = CreateStagedUpdate(package, payload, install, StagedUpdateLayout.LinuxFolder);
            Assert.Throws<InvalidOperationException>(
                () => UpdateService.ApplyUnixUpdate(staged, [], relaunch: true));

            Assert.Equal("#!/bin/sh\nexit 0\n",
                File.ReadAllText(Path.Combine(install, "TSCutterGUI")));
            Assert.False(Directory.Exists(Path.Combine(package, "rollback")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void LinuxUpdate_DoesNotReAddMarkerWhenOptedOut()
    {
        var root = Directory.CreateTempSubdirectory("tscutter-linux-optout-");
        try
        {
            var install = Path.Combine(root.FullName, "install");
            var package = Path.Combine(Path.GetDirectoryName(install)!, ".TSCutter.GUI.update-1");
            var payload = Path.Combine(package, "payload");
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(Path.Combine(install, "data"));
            Directory.CreateDirectory(payload);
            File.WriteAllText(Path.Combine(install, "TSCutterGUI"), "old exe");
            File.WriteAllText(Path.Combine(payload, "TSCutterGUI"), "new exe");
            File.WriteAllText(Path.Combine(payload, AppDataPaths.MarkerFileName), "");
            File.WriteAllText(Path.Combine(install, "data", AppDataPaths.OptOutFileName), "");

            var staged = CreateStagedUpdate(package, payload, install, StagedUpdateLayout.LinuxFolder);
            UpdateService.ApplyUnixUpdate(staged, [], relaunch: false);

            Assert.Equal("new exe", File.ReadAllText(Path.Combine(install, "TSCutterGUI")));
            Assert.False(File.Exists(Path.Combine(install, AppDataPaths.MarkerFileName)));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void ApplyUnixUpdate_SwapsMacBundleAsAWhole()
    {
        var root = Directory.CreateTempSubdirectory("tscutter-mac-apply-");
        try
        {
            var install = Path.Combine(root.FullName, "TSCutter.GUI.app");
            var package = Path.Combine(root.FullName, ".TSCutter.GUI.update-1");
            var payload = Path.Combine(package, "payload");
            var sourceBundle = Path.Combine(payload, "TSCutter.GUI.app", "Contents", "MacOS");
            var targetBundle = Path.Combine(install, "Contents", "MacOS");
            Directory.CreateDirectory(sourceBundle);
            Directory.CreateDirectory(targetBundle);
            File.WriteAllText(Path.Combine(sourceBundle, "TSCutterGUI"), "new exe");
            File.WriteAllText(Path.Combine(targetBundle, "TSCutterGUI"), "old exe");
            File.WriteAllText(Path.Combine(install, "obsolete.txt"), "old");
            File.WriteAllText(Path.Combine(install, AppDataPaths.MarkerFileName), "");
            // On macOS the data folder is beside the bundle, not inside it.
            var data = Path.Combine(root.FullName, "data");
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "config.json"), "settings");

            var staged = CreateStagedUpdate(package, payload, install, StagedUpdateLayout.MacAppBundle);
            UpdateService.ApplyUnixUpdate(staged, [], relaunch: false);

            Assert.Equal("new exe", File.ReadAllText(Path.Combine(targetBundle, "TSCutterGUI")));
            Assert.False(File.Exists(Path.Combine(install, "obsolete.txt")));
            Assert.True(File.Exists(Path.Combine(install, AppDataPaths.MarkerFileName)));
            Assert.Equal("settings", File.ReadAllText(Path.Combine(data, "config.json")));
            Assert.False(Directory.Exists(Path.Combine(package, "rollback")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void ApplyUnixUpdate_CopiesPackageMarkerIntoMacBundle()
    {
        var root = Directory.CreateTempSubdirectory("tscutter-mac-marker-");
        try
        {
            var install = Path.Combine(root.FullName, "TSCutter.GUI.app");
            var package = Path.Combine(root.FullName, ".TSCutter.GUI.update-1");
            var payload = Path.Combine(package, "payload");
            var sourceBundle = Path.Combine(payload, "TSCutter.GUI.app", "Contents", "MacOS");
            var targetBundle = Path.Combine(install, "Contents", "MacOS");
            Directory.CreateDirectory(sourceBundle);
            Directory.CreateDirectory(targetBundle);
            File.WriteAllText(Path.Combine(sourceBundle, "TSCutterGUI"), "new exe");
            File.WriteAllText(Path.Combine(targetBundle, "TSCutterGUI"), "old exe");
            // A new portable release ships the marker at the zip root.
            File.WriteAllText(Path.Combine(payload, AppDataPaths.MarkerFileName), "");

            var staged = CreateStagedUpdate(package, payload, install, StagedUpdateLayout.MacAppBundle);
            UpdateService.ApplyUnixUpdate(staged, [], relaunch: false);

            Assert.True(File.Exists(Path.Combine(install, AppDataPaths.MarkerFileName)));
            Assert.Equal("new exe", File.ReadAllText(Path.Combine(targetBundle, "TSCutterGUI")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void UpdateLock_CanBeAcquiredAndReleased()
    {
        var root = Directory.CreateTempSubdirectory("tscutter-lock-");
        var previous = UpdateService.UpdateLockFilePath;
        UpdateService.UpdateLockFilePath = Path.Combine(root.FullName, "update.lock");
        try
        {
            Assert.True(UpdateService.TryAcquireUpdateLock());
            Assert.True(File.Exists(UpdateService.UpdateLockFilePath));
            UpdateService.ReleaseUpdateLock();
            Assert.False(File.Exists(UpdateService.UpdateLockFilePath));
        }
        finally
        {
            UpdateService.UpdateLockFilePath = previous;
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void UpdateManifest_DeserializesReleases()
    {
        const string json = """
        {
          "version": "9.9.9",
          "tag": "v9.9.9",
          "assets": {
            "win-x64": { "file": "TSCutter.GUI_Beta_9.9.9_win-x64.zip", "size": 42, "sha256": "abc" }
          }
        }
        """;
        var manifest = JsonSerializer.Deserialize(json, AppJsonContext.Default.UpdateManifest);
        Assert.NotNull(manifest);
        Assert.Equal("v9.9.9", manifest.Tag);
        Assert.Equal(42, manifest.Assets!["win-x64"].Size);
    }

    private static StagedUpdate CreateStagedUpdate(string package, string payload, string install,
        StagedUpdateLayout layout) => new()
    {
        PackageDirectory = package,
        PayloadDirectory = payload,
        InstallRoot = install,
        Layout = layout,
        ExecutableName = layout == StagedUpdateLayout.WindowsExe ? "TSCutterGUI.exe" : "TSCutterGUI"
    };

    private static (DirectoryInfo Root, string InstallRoot) CreateTempInstall()
    {
        var root = Directory.CreateTempSubdirectory("tscutter-prepare-");
        return (root, Path.Combine(root.FullName, "install"));
    }

    private static UpdateService CreateService(byte[] zip, string? installRoot = null) =>
        new(new HttpClient(new ByteArrayHandler(zip)), installRoot);

    private static UpdateInfo CreateInfo(string sha256, long size) => new()
    {
        Tag = "v9.9.9",
        Version = "9.9.9",
        Asset = new UpdateAsset
        {
            FileName = PackageName,
            Url = "https://github.com/nilaoda/TSCutter.GUI/releases/download/v9.9.9/package.zip",
            Size = size,
            Sha256 = sha256
        }
    };

    private static byte[] CreateZip(IDictionary<string, string> entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }
        return stream.ToArray();
    }

    private sealed class ByteArrayHandler : HttpMessageHandler
    {
        private readonly byte[] _content;

        public ByteArrayHandler(byte[] content) => _content = content;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_content)
            });
    }
}
