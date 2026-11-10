using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;

namespace TSCutter.GUI.Themes;

/// <summary>按应用语言提供 macOS 字体回退，避免 LastResort 缓存影响正常文字。</summary>
internal sealed class MacOSFontFallbacks : IReadOnlyList<FontFallback>
{
    private readonly ConcurrentDictionary<string, FontFallback[]> cache = new(StringComparer.OrdinalIgnoreCase);

    private FontFallback[] Current
    {
        get
        {
            var language = Application.Current is App
                ? App.LocalizationService?.CurrentLanguageCode : null;
            return cache.GetOrAdd(language ?? CultureInfo.CurrentUICulture.Name, CreateFallbacks);
        }
    }

    public int Count => Current.Length;
    public FontFallback this[int index] => Current[index];
    public IEnumerator<FontFallback> GetEnumerator() => ((IEnumerable<FontFallback>)Current).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static FontFallback[] CreateFallbacks(string language)
    {
        var fonts = FontManager.Current.SystemFonts;
        var preferred = language switch
        {
            "zh-CN" => "PingFang SC",
            "zh-TW" => "PingFang TC",
            _ => null
        };
        if (preferred is not null && fonts.Any(font => font.Name == preferred))
            return [new FontFallback { FontFamily = new FontFamily(preferred) }];

        // 英文界面或首选字体缺失时，直接向系统查询 CJK 字体，绕开 Avalonia 中可能已缓存的 LastResort。
        using var matched = SKFontManager.Default.MatchCharacter(null, SKFontStyle.Normal, [language], '中');
        var family = matched?.FamilyName;
        if (string.IsNullOrEmpty(family) || family.Contains("LastResort", StringComparison.OrdinalIgnoreCase) ||
            !fonts.Any(font => font.Name == family))
            return [];
        return [new FontFallback { FontFamily = new FontFamily(family) }];
    }
}
