using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.Models;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.Services;

internal sealed class SavedFrameStore(string? path = null)
{
    private readonly string path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TSCutter.GUI", "saved-frames.json");

    public async Task<SavedFrameLibrary> LoadAsync(CancellationToken token = default)
    {
        if (!File.Exists(path)) return new SavedFrameLibrary();
        if (new FileInfo(path).Length > 32 * 1024 * 1024)
            throw new InvalidDataException("The saved frame library is too large.");
        var json = await File.ReadAllTextAsync(path, token).ConfigureAwait(false);
        var library = JsonSerializer.Deserialize(json, AppJsonContext.Default.SavedFrameLibrary)
            ?? throw new InvalidDataException("The saved frame library is empty.");
        Validate(library);
        return library;
    }

    public async Task SaveAsync(SavedFrameLibrary library, CancellationToken token = default)
    {
        Validate(library);
        var json = JsonSerializer.Serialize(library, AppJsonContext.Default.SavedFrameLibrary);
        if (json.Length > 32 * 1024 * 1024)
            throw new InvalidDataException("The saved frame library is too large.");
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".saved-frames-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, json, token).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void Validate(SavedFrameLibrary library)
    {
        if (library.Version != 1 || library.Templates is null || library.Templates.Count > 256 ||
            library.Templates.Any(item => item is null || item.Id == Guid.Empty ||
                string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 100 ||
                item.Jpeg is null || item.Jpeg.Length is < 1 or > 512_000) ||
            library.Templates.Select(item => item.Id).Distinct().Count() != library.Templates.Count)
            throw new InvalidDataException("The saved frame library is invalid.");
    }
}
