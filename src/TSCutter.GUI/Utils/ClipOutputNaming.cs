using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TSCutter.GUI.Utils;

public static class ClipOutputNaming
{
    public const string DefaultTemplate = "{source}_({start}-{end})";

    private enum Placeholder { Source, Name, Index, Start, End }

    private static readonly FrozenDictionary<string, Placeholder> Placeholders = Enum.GetValues<Placeholder>()
        .ToFrozenDictionary(value => value.ToString().ToLowerInvariant(), StringComparer.Ordinal);

    public static bool IsValidTemplate(string? template)
    {
        try
        {
            Generate(template!, "Recording.ts", "Clip", 1, 10, 20, "ts");
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    public static string Generate(string template, string sourcePath, string clipName, int index,
        double start, double end, string extension)
    {
        if (string.IsNullOrWhiteSpace(template) || template.Length > 240)
            throw new ArgumentException("Invalid output name template.", nameof(template));
        var result = new StringBuilder();
        for (var position = 0; position < template.Length; position++)
        {
            var character = template[position];
            if (character == '}') throw new ArgumentException("Unexpected closing brace.", nameof(template));
            if (character != '{') { result.Append(character); continue; }
            var close = template.IndexOf('}', position + 1);
            if (close < 0) throw new ArgumentException("Unclosed placeholder.", nameof(template));
            var token = template[(position + 1)..close];
            if (!Placeholders.TryGetValue(token, out var placeholder))
                throw new ArgumentException($"Unknown placeholder: {token}.", nameof(template));
            result.Append(placeholder switch
            {
                Placeholder.Source => Path.GetFileNameWithoutExtension(sourcePath),
                Placeholder.Name => string.IsNullOrWhiteSpace(clipName) ? $"clip{index:000}" : clipName.Trim(),
                Placeholder.Index => index.ToString("000", CultureInfo.InvariantCulture),
                Placeholder.Start => FormatTime(start),
                Placeholder.End => FormatTime(end),
                _ => throw new ArgumentOutOfRangeException(nameof(placeholder))
            });
            position = close;
        }
        var name = Sanitize(result.ToString());
        var suffix = "." + (extension is "m2ts" or "mp4" or "mkv" ? extension : "ts");
        // A template such as {source} must not suggest overwriting the input.
        if (string.Equals(name + suffix, Path.GetFileName(sourcePath), StringComparison.OrdinalIgnoreCase))
            name += $"_clip{index:000}";
        return name + suffix;
    }

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(seconds);
        return string.Create(CultureInfo.InvariantCulture,
            $"{(long)time.TotalHours:00}.{time.Minutes:00}.{time.Seconds:00}.{time.Milliseconds:000}");
    }

    private static string Sanitize(string value)
    {
        var result = new StringBuilder();
        foreach (var character in value)
            result.Append(char.IsControl(character) || "<>:\"/\\|?*".Contains(character) ? '_' : character);
        var name = result.ToString().Trim().TrimEnd('.', ' ');
        // Bound the UTF-8 filename length, including room for extensions and collision suffixes.
        while (Encoding.UTF8.GetByteCount(name) > 180)
            name = name[..(name.Length - (char.IsLowSurrogate(name[^1]) ? 2 : 1))];
        name = name.TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(name)) name = "clip";
        var firstPart = name.Split('.')[0].TrimEnd().ToUpperInvariant();
        if (firstPart is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            firstPart.Length == 4 && (firstPart.StartsWith("COM") || firstPart.StartsWith("LPT")) &&
            (firstPart[3] is >= '1' and <= '9' or '¹' or '²' or '³'))
            name = "_" + name;
        return name;
    }

    public static string UniquePath(string directory, string fileName, ISet<string> reserved)
    {
        directory = Path.GetFullPath(directory);
        var path = Path.Combine(directory, fileName);
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var suffix = 2; reserved.Contains(path) || File.Exists(path) || Directory.Exists(path); suffix++)
            path = Path.Combine(directory, $"{stem}_{suffix}{extension}");
        reserved.Add(path);
        return path;
    }
}
