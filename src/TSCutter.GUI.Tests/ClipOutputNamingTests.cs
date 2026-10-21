using System.Text;
using TSCutter.GUI.Utils;
using Xunit;

namespace TSCutter.GUI.Tests;

public sealed class ClipOutputNamingTests
{
    [Theory]
    [InlineData("mp4")]
    [InlineData("mkv")]
    public void RemuxNamesUseTheOutputContainerAndAvoidSourceCollisions(string extension)
    {
        Assert.Equal("source_(00.00.00.000-00.00.02.000)." + extension,
            ClipOutputNaming.Generate(ClipOutputNaming.DefaultTemplate, "source." + extension, "", 1, 0, 2, extension));
        Assert.Equal("source_clip001." + extension,
            ClipOutputNaming.Generate("{source}", "source." + extension, "", 1, 0, 2, extension));
    }

    [Theory]
    [InlineData("{source}_{index}_{name}_({start}-{end})")]
    public void TemplatesExpandLabelsAndTimes(string template)
    {
        Assert.Equal("Recording_003_精彩片段_(00.00.10.125-00.00.20.500).m2ts",
            ClipOutputNaming.Generate(template, "Recording.m2ts", "精彩片段", 3, 10.125, 20.5, "m2ts"));
        Assert.Equal("clip003_00.01.00.000.ts",
            ClipOutputNaming.Generate("{name}_{end}", "Recording.ts", " ", 3, 0, 60, "ts"));
        Assert.Equal("25.01.01.125.ts",
            ClipOutputNaming.Generate("{start}", "Recording.ts", "", 1, 90_061.125, -1, "ts"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{unknown}")]
    [InlineData("{0}")]
    [InlineData("{source")]
    [InlineData("source}")]
    [InlineData("{{name}}")]
    public void InvalidTemplatesAreRejected(string template)
    {
        Assert.False(ClipOutputNaming.IsValidTemplate(template));
        Assert.Throws<ArgumentException>(() =>
            ClipOutputNaming.Generate(template, "Recording.ts", "", 1, 0, 1, "ts"));
    }

    [Theory]
    [InlineData("../a:b\\c?*\"<>|\n", ".._a_b_c______.ts")]
    [InlineData("CON.txt", "_CON.txt.ts")]
    [InlineData("LPT1 .txt", "_LPT1 .txt.ts")]
    [InlineData("... ", "clip.ts")]
    public void NamesAreSafeOnWindowsAndMac(string name, string expected)
    {
        Assert.Equal(expected, ClipOutputNaming.Generate("{name}", "Recording.ts", name, 1, 0, 1, "ts"));
    }

    [Fact]
    public void LongUnicodeNamesStayBoundedAndCannotSuggestTheSource()
    {
        var result = ClipOutputNaming.Generate("{name}", "Recording.ts", string.Concat(Enumerable.Repeat("剪辑😀", 120)), 1, 0, 1, "ts");
        Assert.InRange(Encoding.UTF8.GetByteCount(result), 1, 184);
        Assert.DoesNotContain('\uFFFD', Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(result)));
        Assert.Equal("Recording_clip002.ts",
            ClipOutputNaming.Generate("{source}", "Recording.ts", "", 2, 0, 1, "ts"));
        Assert.Equal("Recording_(00.00.00.000-00.00.01.000).ts",
            ClipOutputNaming.Generate(ClipOutputNaming.DefaultTemplate, "Recording.ts", "", 1, 0, 1, "ts"));
    }

    [Fact]
    public void TruncatedNamesCannotBecomeEmptyOrReserved()
    {
        Assert.Equal("clip.ts", ClipOutputNaming.Generate(new string('.', 180) + "x",
            "Recording.ts", "", 1, 0, 1, "ts"));
        Assert.Equal("_CON.ts", ClipOutputNaming.Generate("CON" + new string(' ', 177) + "x",
            "Recording.ts", "", 1, 0, 1, "ts"));
    }

    [Fact]
    public void BatchNamesAvoidExistingFilesDirectoriesAndReservedPaths()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"clip-names-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "clip.ts"), "keep");
            Directory.CreateDirectory(Path.Combine(directory, "clip_2.ts"));
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine(directory, "clip_3.ts") };
            Assert.Equal(Path.Combine(directory, "clip_4.ts"), ClipOutputNaming.UniquePath(directory, "clip.ts", reserved));
            Assert.Equal(Path.Combine(directory, "clip_5.ts"), ClipOutputNaming.UniquePath(directory, "clip.ts", reserved));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(directory, "clip.ts")));
        }
        finally { Directory.Delete(directory, true); }
    }
}
