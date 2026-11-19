using System.Text.Json;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Tests.Editing;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.ViewModels.Application;
using Xunit;

namespace TSCutter.GUI.Tests.Application;

/// <summary>自动保存设置：默认值与取值范围、设置窗口的间隔档位与文字、提交到配置。</summary>
public sealed class AutoSaveSettingsTests
{
    [Fact]
    public void DefaultsToEnabledEveryMinuteAndStaysInRange()
    {
        var config = JsonSerializer.Deserialize("{}", AppJsonContext.Default.AppConfig)!;
        Assert.True(config.AutoSaveRecovery);
        Assert.Equal(60, config.AutoSaveIntervalSeconds);

        var changed = new List<string?>();
        config.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        config.AutoSaveRecovery = false;
        config.AutoSaveIntervalSeconds = 300;
        Assert.Equal([nameof(AppConfig.AutoSaveRecovery), nameof(AppConfig.AutoSaveIntervalSeconds)], changed);
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(config, AppJsonContext.Default.AppConfig),
            AppJsonContext.Default.AppConfig)!;
        Assert.False(restored.AutoSaveRecovery);
        Assert.Equal(300, restored.AutoSaveIntervalSeconds);

        foreach (var (saved, expected) in new[] { (0, 60), (5, 15), (99999, 3600) })
            Assert.Equal(expected, JsonSerializer.Deserialize($$"""{"AutoSaveIntervalSeconds":{{saved}}}""",
                AppJsonContext.Default.AppConfig)!.AutoSaveIntervalSeconds);
    }

    [Fact]
    public void SettingsOfferIntervalPresetsAndKeepAValueFromTheConfigurationFile()
    {
        var config = new RemoveModeWorkflowTests.MemoryConfiguration();
        config.CurrentConfig.AutoSaveIntervalSeconds = 45;
        var viewModel = new SettingsWindowViewModel(config, new ShortcutSettingsTests.KeyLocalization(new()
        {
            ["String.Settings.AutoSaveInterval.Seconds"] = "{0} s",
            ["String.Settings.AutoSaveInterval.Minute"] = "1 min",
            ["String.Settings.AutoSaveInterval.Minutes"] = "{0} mins"
        }));
        Assert.True(viewModel.AutoSaveRecovery);
        // 45 秒不在档位中（例如手动编辑过配置文件），仍列出并选中；整分钟按分钟显示。
        Assert.Equal(new[] { 30, 45, 60, 120, 300, 600 }, viewModel.AutoSaveIntervalValues);
        Assert.Equal(["30 s", "45 s", "1 min", "2 mins", "5 mins", "10 mins"], viewModel.AutoSaveIntervalTexts);
        Assert.Equal(1, viewModel.AutoSaveIntervalIndex);

        // 下拉框重建条目时可能短暂写回 -1，忽略。
        viewModel.AutoSaveIntervalIndex = -1;
        Assert.Equal(45, viewModel.AutoSaveIntervalSeconds);
        viewModel.AutoSaveIntervalIndex = 4;
        viewModel.AutoSaveRecovery = false;
        // 未提交前不改动配置。
        Assert.True(config.CurrentConfig.AutoSaveRecovery);
        viewModel.OkCommand.Execute(null);
        Assert.False(config.CurrentConfig.AutoSaveRecovery);
        Assert.Equal(300, config.CurrentConfig.AutoSaveIntervalSeconds);
    }
}
