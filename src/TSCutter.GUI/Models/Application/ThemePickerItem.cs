using System.Collections.Generic;
using System.Linq;

namespace TSCutter.GUI.Models.Application;

public sealed class ThemePickerItem
{
    public string Name { get; }
    public ThemeModel? Theme { get; }
    public bool IsCustom { get; }
    public bool IsSelectable => Theme != null || IsCustom;

    private ThemePickerItem(string name, ThemeModel? theme = null, bool isCustom = false)
    {
        Name = name;
        Theme = theme;
        IsCustom = isCustom;
    }

    // 自定义项使用独立对象，避免空的选中值被 ComboBox 当作未选择。
    public static IReadOnlyList<ThemePickerItem> CreatePresets(IEnumerable<ThemeModel> themes) =>
        [new ThemePickerItem(string.Empty, isCustom: true), .. Create(themes)];

    public static IReadOnlyList<ThemePickerItem> Create(IEnumerable<ThemeModel> themes)
    {
        // 分组标题只用于展示，实际选中值仍是原有主题，保持配置兼容。
        // macOS 外观排在首位，因为它在 macOS 上是唯一使用系统标题栏的选项。
        return themes.GroupBy(theme => theme.Family)
            .OrderBy(group => group.Key switch
            {
                "macOS" => 0,
                "Windows 11" => 1,
                "Windows 10" => 2,
                "Windows 8" => 3,
                "Windows 7" => 4,
                "Windows XP" => 5,
                _ => 6
            })
            .SelectMany(group => new[] { new ThemePickerItem(group.Key) }
                .Concat(group.Select(theme => new ThemePickerItem(theme.Name, theme))))
            .ToArray();
    }
}
