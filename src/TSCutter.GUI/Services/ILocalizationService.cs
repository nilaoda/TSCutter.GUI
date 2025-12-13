using System;
using System.Collections.Generic;
using TSCutter.GUI.Models;

namespace TSCutter.GUI.Services;

public interface ILocalizationService
{
    // 获取所有支持的语言
    List<SupportedLang> SupportedLanguages { get; }
        
    // 当前语言代码 (e.g. "zh-CN")
    string CurrentLanguageCode { get; }

    // 切换语言
    void SwitchLanguage(string code);

    // 在 C# 代码里获取翻译
    string GetString(string key);
    
    // 语言切换事件
    event Action? LanguageChanged;
}