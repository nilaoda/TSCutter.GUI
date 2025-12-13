using TSCutter.GUI.Models;

namespace TSCutter.GUI.Services;

public interface IConfigurationService
{
    AppConfig CurrentConfig { get; }
    void Load();
    void Save();
    void ApplyTheme(string theme);
    void ApplyLanguage(string language);
}