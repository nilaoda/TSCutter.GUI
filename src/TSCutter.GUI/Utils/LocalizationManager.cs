using CommunityToolkit.Mvvm.ComponentModel;
using TSCutter.GUI.Services;

namespace TSCutter.GUI.Utils;

public partial class LocalizationManager : ObservableObject
{
    // 单例
    public static LocalizationManager Instance { get; } = new();

    private LocalizationManager()
    {
        InitializeSubscription();
    }

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
