using TSCutter.GUI.Models.Application;


namespace TSCutter.GUI.Services.Application;

public interface IConfigurationService
{
    AppConfig CurrentConfig { get; }
    void Load();
    void Save();
    void ApplyTheme(string theme);
    void ApplyLanguage(string language);
}