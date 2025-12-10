using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TSCutter.GUI.Utils;

public record SupportedLang(string Code, string Description);

public partial class LocalizationManager : ObservableObject
{
    // 单例
    public static LocalizationManager Instance { get; } = new();
    // 用于识别语言字典的特征
    private const string LangPathIdentifier = "/Lang/";
    // 所有支持的语言
    public static List<SupportedLang> SupportedLanguages { get; } =
    [
        new("en-US", "English"),
        new("zh-CN", "简体中文"),
        new("zh-TW", "繁體中文")
    ];

    private LocalizationManager() { }

    // 索引器
    private string this[string key]
    {
        get
        {
            if (Application.Current?.TryGetResource(key, null, out var res) == true && res is string str)
            {
                return str;
            }
            return $"[{key}]";
        }
    }

    /// <summary>
    /// 切换语言
    /// </summary>
    /// <param name="code">例如 en-US</param>
    public void SwitchLanguage(string code)
    {
        var app = Application.Current;
        if (app == null)
        {
            return;
        }
        
        var supportedLanguages = SupportedLanguages.Find(x => x.Code == code);
        if (supportedLanguages == null)
        {
            return;
        }

        var newUri = new Uri($"avares://TSCutterGUI/Lang/{code}.axaml");
        var newRes = new ResourceInclude(new Uri("avares://TSCutterGUI/App.axaml")) 
        { 
            Source = newUri
        };

        // 查找路径包含 "/Lang/" 的字典
        var existingDictionary = app.Resources.MergedDictionaries
            .OfType<ResourceInclude>()
            .FirstOrDefault(r => r.Source?.OriginalString.Contains(LangPathIdentifier) == true);

        if (existingDictionary != null)
        {
            // 替换字典
            var index = app.Resources.MergedDictionaries.IndexOf(existingDictionary);
            app.Resources.MergedDictionaries[index] = newRes;
        }
        else
        {
            // 没找到，直接添加
            app.Resources.MergedDictionaries.Add(newRes);
        }

        // 通知索引器 "Item[]" 更新
        OnPropertyChanged("Item[]");
    }
}
