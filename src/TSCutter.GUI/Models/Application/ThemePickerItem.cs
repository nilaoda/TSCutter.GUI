using System.Collections.Generic;
using System.Linq;

namespace TSCutter.GUI.Models.Application;

public sealed class ThemePickerItem
{
    public string Name { get; }
    public ThemeModel? Theme { get; }
    public bool IsSelectable => Theme != null;

    private ThemePickerItem(string name, ThemeModel? theme = null)
    {
        Name = name;
        Theme = theme;
    }

    public static IReadOnlyList<ThemePickerItem> Create(IEnumerable<ThemeModel> themes)
    {
        // 分组标题只用于展示，实际选中值仍是原有主题，保持配置兼容。
        return themes.GroupBy(theme => theme.Family)
            .OrderBy(group => group.Key switch
            {
                "Windows 11" => 0,
                "Windows 10" => 1,
                "Windows 8" => 2,
                "Windows 7" => 3,
                "Windows XP" => 4,
                _ => 5
            })
            .SelectMany(group => new[] { new ThemePickerItem(group.Key) }
                .Concat(group.Select(theme => new ThemePickerItem(theme.Name, theme))))
            .ToArray();
    }
}
