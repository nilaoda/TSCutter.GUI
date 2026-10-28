using System;
using CommunityToolkit.Mvvm.ComponentModel;
using TSCutter.GUI.Services.Application;

namespace TSCutter.GUI.Utils.Application;

public partial class LocalizationManager : ObservableObject
{
    // 单例
    private static readonly Lazy<LocalizationManager> Shared = new(() => new LocalizationManager());
    public static LocalizationManager Instance => Shared.Value;

    private LocalizationManager()
    {
        InitializeSubscription();
    }

    // 调用方自行处理语言切换时，可通过注入的服务直接读取生成属性，无需全局应用实例。
    internal LocalizationManager(ILocalizationService service) => _service = service;

    private ILocalizationService? _service;
    
    private void InitializeSubscription()
    {
        // 获取服务并订阅
        var service = App.LocalizationService;
        _service = service;
        _service.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        // 通知 UI 更新
        OnPropertyChanged("Item[]");
    }

    // 索引器
    private string this[string key] => _service!.GetString(key);
}
