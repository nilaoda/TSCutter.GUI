using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.Models.FrameSearch;
using TSCutter.GUI.Utils.Application;

namespace TSCutter.GUI.Services.FrameSearch;

internal sealed class SavedFrameStore
{
    private const int MaximumSize = 32 * 1024 * 1024;
    private readonly string? path;
    private string? memoryJson;

    public SavedFrameStore(string? path = null) : this(path, inMemory: false) { }

    private SavedFrameStore(string? path, bool inMemory)
    {
        this.path = inMemory ? null : path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TSCutter.GUI", "saved-frames.json");
    }

    // 内存实例由宿主持有，生命周期随工作区；每次读取仍从 JSON 还原独立的数据对象。
    internal static SavedFrameStore CreateInMemory() => new(null, inMemory: true);

    public async Task<SavedFrameLibrary> LoadAsync(CancellationToken token = default)
    {
        string json;
        if (path is null)
        {
            token.ThrowIfCancellationRequested();
            if (memoryJson is null)
                return new SavedFrameLibrary();
            json = memoryJson;
        }
        else
        {
            if (!File.Exists(path))
                return new SavedFrameLibrary();
            if (new FileInfo(path).Length > MaximumSize)
                throw new InvalidDataException("The saved frame library is too large.");
            json = await File.ReadAllTextAsync(path, token).ConfigureAwait(false);
        }
        if (json.Length > MaximumSize)
            throw new InvalidDataException("The saved frame library is too large.");
        var library = JsonSerializer.Deserialize(json, AppJsonContext.Default.SavedFrameLibrary)
            ?? throw new InvalidDataException("The saved frame library is empty.");
        Validate(library);
        return library;
    }

    public async Task SaveAsync(SavedFrameLibrary library, CancellationToken token = default)
    {
        Validate(library);
        var json = JsonSerializer.Serialize(library, AppJsonContext.Default.SavedFrameLibrary);
        if (json.Length > MaximumSize)
            throw new InvalidDataException("The saved frame library is too large.");
        if (path is null)
        {
            token.ThrowIfCancellationRequested();
            memoryJson = json;
            return;
        }
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
