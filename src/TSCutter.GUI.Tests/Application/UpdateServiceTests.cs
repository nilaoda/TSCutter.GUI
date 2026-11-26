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
        var service = CreateService(zip);
        var info = CreateInfo(Convert.ToHexString(SHA256.HashData(zip)), zip.Length);

        var staged = await service.PrepareAsync(info);
        try
        {
            Assert.Equal(StagedUpdateLayout.WindowsExe, staged.Layout);
            Assert.Equal("new executable", File.ReadAllText(Path.Combine(staged.PayloadDirectory, "TSCutterGUI.exe")));
        }
        finally
        {
            Directory.Delete(staged.PackageDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareAsync_RejectsWrongChecksum()
    {
        var zip = CreateZip(new Dictionary<string, string> { ["TSCutterGUI.exe"] = "new" });
        var service = CreateService(zip);
        var info = CreateInfo("0000000000000000000000000000000000000000000000000000000000000000", zip.Length);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(info));
    }

    [Fact]
    public async Task PrepareAsync_RejectsZipSlip()
    {
        var zip = CreateZip(new Dictionary<string, string>
        {
            ["TSCutterGUI.exe"] = "new",
            ["../escaped.txt"] = "bad"
        });
        var service = CreateService(zip);
        var info = CreateInfo(Convert.ToHexString(SHA256.HashData(zip)), zip.Length);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(info));
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
        var service = CreateService(zip);
        var info = CreateInfo(Convert.ToHexString(SHA256.HashData(zip)), zip.Length);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.PrepareAsync(info, token: cancellation.Token));
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

    private static UpdateService CreateService(byte[] zip) =>
        new(new HttpClient(new ByteArrayHandler(zip)));

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
