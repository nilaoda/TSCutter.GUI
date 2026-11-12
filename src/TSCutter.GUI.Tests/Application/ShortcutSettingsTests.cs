using System.ComponentModel;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Tests.UI;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.ViewModels.Application;
using TSCutter.GUI.Views.Application;
using Xunit;

namespace TSCutter.GUI.Tests.Application;

/// <summary>设置窗口的快捷键页：滚轮设置的保存与取值范围、只读快捷键列表的内容与搜索、三种语言的文字。</summary>
[Collection("Avalonia UI")]
public sealed class ShortcutSettingsTests
{
    private static readonly InputPreferences[] AllVariants =
    [
        new(),
        new() { WheelStepUnit = WheelStepUnit.Frame },
        new() { WheelStepUnit = WheelStepUnit.Second },
        new() { PreviewWheelAction = PreviewWheelAction.Navigate, TimelineWheelAction = TimelineWheelAction.Navigate },
        new() { TimelineWheelAction = TimelineWheelAction.Pan }
    ];

    [Fact]
    public void InputPreferencesDefaultToPreviousBehaviourAndSurviveSerialization()
    {
        var config = JsonSerializer.Deserialize("{}", AppJsonContext.Default.AppConfig)!;
        Assert.Equal(new InputPreferences(), config.Input);
        Assert.Equal(1, config.Input.WheelStep);
        Assert.Equal(10, config.Input.ShiftWheelStep);
        Assert.False(config.Input.ReverseWheel);
        Assert.Equal(WheelStepUnit.KeyFrame, config.Input.WheelStepUnit);
        Assert.Equal(PreviewWheelAction.Zoom, config.Input.PreviewWheelAction);
        Assert.Equal(TimelineWheelAction.None, config.Input.TimelineWheelAction);

        string? changed = null;
        config.PropertyChanged += (_, args) => changed = args.PropertyName;
        var custom = new InputPreferences
        {
            WheelStep = 5, ShiftWheelStep = 30, ReverseWheel = true, WheelStepUnit = WheelStepUnit.Second,
            PreviewWheelAction = PreviewWheelAction.Navigate, TimelineWheelAction = TimelineWheelAction.Pan
        };
        config.Input = custom;
        Assert.Equal(nameof(AppConfig.Input), changed);
        var json = JsonSerializer.Serialize(config, AppJsonContext.Default.AppConfig);
        Assert.Equal(custom, JsonSerializer.Deserialize(json, AppJsonContext.Default.AppConfig)!.Input);
    }

    [Fact]
    public void OutOfRangeValuesInTheConfigurationFileAreClamped()
    {
        var config = JsonSerializer.Deserialize(
            """{ "Input": { "WheelStepUnit": 9, "WheelStep": 0, "ShiftWheelStep": 1000, "PreviewWheelAction": 7, "TimelineWheelAction": -1 } }""",
            AppJsonContext.Default.AppConfig)!;
        Assert.Equal(WheelStepUnit.KeyFrame, config.Input.WheelStepUnit);
        Assert.Equal(InputPreferences.MinWheelStep, config.Input.WheelStep);
        Assert.Equal(InputPreferences.MaxShiftWheelStep, config.Input.ShiftWheelStep);
        Assert.Equal(PreviewWheelAction.Zoom, config.Input.PreviewWheelAction);
        Assert.Equal(TimelineWheelAction.None, config.Input.TimelineWheelAction);
        config.Input = null!;
        Assert.Equal(new InputPreferences(), config.Input);
    }

    [Fact]
    public void MouseRowsFollowTheWheelSettings()
    {
        static ShortcutGroup Mouse(InputPreferences input) =>
            ShortcutCatalog.Build(Strings(), input).Single(group => group.Title == "String.Shortcuts.Group.Mouse");

        var defaults = Mouse(new InputPreferences());
        Assert.Contains(defaults.Entries, entry => entry is
            { Action: "String.Shortcuts.PreviewZoom", WindowsKeys: "String.Shortcuts.Key.Wheel" });
        Assert.Contains(defaults.Entries, entry => entry is
            { Action: "String.Shortcuts.TimelinePan", WindowsKeys: "Shift+String.Shortcuts.Key.Wheel" });
        Assert.DoesNotContain(defaults.Entries, entry => entry.Action is "String.Shortcuts.PreviewNavigate"
            or "String.Shortcuts.TimelineNavigate");

        var navigate = Mouse(AllVariants[3]);
        Assert.Contains(navigate.Entries, entry => entry is
            { Action: "String.Shortcuts.PreviewNavigate", WindowsKeys: "String.Shortcuts.Key.Wheel" });
        // 预览设为定位后，缩放改为 Ctrl / ⌘ 加滚轮。
        Assert.Contains(navigate.Entries, entry => entry is
            { Action: "String.Shortcuts.PreviewZoom", WindowsKeys: "Ctrl+String.Shortcuts.Key.Wheel", MacKeys: "⌘+String.Shortcuts.Key.Wheel" });
        Assert.Contains(navigate.Entries, entry => entry.Action == "String.Shortcuts.TimelineNavigate");

        var pan = Mouse(AllVariants[4]);
        Assert.Contains(pan.Entries, entry => entry is
            { Action: "String.Shortcuts.TimelinePan", WindowsKeys: "String.Shortcuts.Key.Wheel / Shift+String.Shortcuts.Key.Wheel" });

        // 步长与单位写进操作说明；按帧时注明其他模式按关键帧。
        var strings = Strings(new()
        {
            ["String.Shortcuts.StripWheel"] = "move {0}",
            ["String.Shortcuts.StripWheelFrames"] = "smart cut {0}",
            ["String.Settings.Shortcuts.Step.KeyFrameOne"] = "1 keyframe",
            ["String.Settings.Shortcuts.Step.KeyFrames"] = "{0} keyframes",
            ["String.Settings.Shortcuts.Step.Frames"] = "{0} frames",
            ["String.Settings.Shortcuts.Step.Seconds"] = "{0} s"
        });
        IEnumerable<string> Actions(InputPreferences input) => ShortcutCatalog.Build(strings, input)
            .Single(group => group.Title == "String.Shortcuts.Group.Mouse").Entries.Select(entry => entry.Action);
        Assert.Contains("move 1 keyframe", Actions(new InputPreferences()));
        Assert.Contains("move 3 keyframes", Actions(new InputPreferences { WheelStep = 3, ShiftWheelStep = 25 }));
        Assert.Contains("move 25 keyframes", Actions(new InputPreferences { WheelStep = 3, ShiftWheelStep = 25 }));
        Assert.Contains("smart cut 2 frames", Actions(new InputPreferences { WheelStepUnit = WheelStepUnit.Frame, WheelStep = 2 }));
        Assert.Contains("move 30 s", Actions(new InputPreferences { WheelStepUnit = WheelStepUnit.Second, ShiftWheelStep = 30 }));
    }

    [Fact]
    public void KeyboardRowsMatchTheWindowKeyBindings()
    {
        var entries = ShortcutCatalog.Build(Strings(), new InputPreferences()).SelectMany(group => group.Entries).ToArray();
        Assert.Contains(entries, entry => entry is { Action: "String.Shortcuts.OpenVideo", WindowsKeys: "Ctrl+O", MacKeys: "⌘O" });
        Assert.Contains(entries, entry => entry is { Action: "String.Shortcuts.Redo", WindowsKeys: "Ctrl+Y", MacKeys: "⇧⌘Z" });
        Assert.Contains(entries, entry => entry is { Action: "String.Shortcuts.Forward10KeyFrames", WindowsKeys: "Shift+→" });
        Assert.Contains(entries, entry => entry is { Action: "String.Shortcuts.ToggleFullScreen", WindowsKeys: "F11", MacKeys: "⌃⌘F" });
        Assert.Contains(entries, entry => entry is { Action: "String.Shortcuts.MarkStart", WindowsKeys: "I" });
    }

    [Fact]
    public void SearchMatchesActionsKeysAndGroupTitles()
    {
        var groups = ShortcutCatalog.Build(Strings(new()
        {
            ["String.Shortcuts.Group.Edit"] = "Edit",
            ["String.Shortcuts.Undo"] = "Undo"
        }), new InputPreferences());

        Assert.Same(groups, ShortcutCatalog.Filter(groups, "  ", macOS: false));
        var undo = Assert.Single(ShortcutCatalog.Filter(groups, "undo", macOS: false));
        Assert.Equal("Undo", Assert.Single(undo.Entries).Action);
        // 按键写法同样可以搜索，但只按显示的平台匹配。
        Assert.Contains(ShortcutCatalog.Filter(groups, "ctrl+z", macOS: false).SelectMany(group => group.Entries),
            entry => entry.Action == "Undo");
        Assert.Empty(ShortcutCatalog.Filter(groups, "ctrl+z", macOS: true));
        Assert.Contains(ShortcutCatalog.Filter(groups, "⇧⌘Z", macOS: true).SelectMany(group => group.Entries),
            entry => entry.Action == "String.Shortcuts.Redo");
        Assert.Empty(ShortcutCatalog.Filter(groups, "⇧⌘Z", macOS: false));
        // 分类标题匹配时保留整个分类。
        var edit = Assert.Single(ShortcutCatalog.Filter(groups, "edit", macOS: false));
        Assert.Equal(groups.Single(group => group.Title == "Edit").Entries.Count, edit.Entries.Count);
        Assert.Empty(ShortcutCatalog.Filter(groups, "no such shortcut", macOS: false));
    }

    [Fact]
    public void AllShortcutTextsExistInEveryLanguage()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        session.Dispatch(() =>
        {
            var dictionaries = new[] { "en-US", "zh-CN", "zh-TW" }.ToDictionary(code => code, code =>
                (Avalonia.Controls.ResourceDictionary)AvaloniaXamlLoader.Load(new Uri($"avares://TSCutterGUI/Lang/{code}.axaml")));
            // 记录列表与步长文字实际读取的资源键。
            var localization = new KeyLocalization();
            var strings = new LocalizationManager(localization);
            foreach (var input in AllVariants)
            {
                ShortcutCatalog.Build(strings, input);
                foreach (var value in new[] { 1, 2 })
                    ShortcutCatalog.StepText(strings, input.WheelStepUnit, value);
            }
            var used = localization.Requested;
            var english = dictionaries["en-US"].Keys.OfType<string>()
                .Where(key => key.StartsWith("String.Shortcuts.", StringComparison.Ordinal)
                    || key.StartsWith("String.Settings.Shortcuts", StringComparison.Ordinal));
            foreach (var key in used.Concat(english))
                foreach (var (code, dictionary) in dictionaries)
                    Assert.True(dictionary.TryGetValue(key, out var value) && value is string { Length: > 0 },
                        $"{code} is missing {key}");
        }, CancellationToken.None);
    }

    [Fact]
    public void SettingsKeepWheelChangesUntilCommitted()
    {
        var config = new MemoryConfiguration();
        config.CurrentConfig.Input = new InputPreferences { WheelStep = 4, TimelineWheelAction = TimelineWheelAction.Pan };
        var viewModel = new SettingsWindowViewModel(config, new KeyLocalization());
        Assert.Equal(4, viewModel.WheelStep);
        Assert.Equal((int)TimelineWheelAction.Pan, viewModel.TimelineWheelActionIndex);
        Assert.Equal((int)WheelStepUnit.KeyFrame, viewModel.WheelStepUnitIndex);

        var changed = new List<string?>();
        ((INotifyPropertyChanged)viewModel).PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        viewModel.PreviewWheelActionIndex = (int)PreviewWheelAction.Navigate;
        viewModel.WheelStepUnitIndex = (int)WheelStepUnit.Second;
        viewModel.ShiftWheelStep = 40;
        Assert.Contains(nameof(SettingsWindowViewModel.ShortcutGroups), changed);
        Assert.Contains(viewModel.ShortcutGroups.SelectMany(group => group.Entries),
            entry => entry.Action == "String.Shortcuts.PreviewNavigate");
        // 未提交前不改动配置。
        Assert.Equal(PreviewWheelAction.Zoom, config.CurrentConfig.Input.PreviewWheelAction);

        viewModel.ApplyCommand.Execute(null);
        Assert.Equal(new InputPreferences
        {
            WheelStepUnit = WheelStepUnit.Second, WheelStep = 4, ShiftWheelStep = 40,
            PreviewWheelAction = PreviewWheelAction.Navigate, TimelineWheelAction = TimelineWheelAction.Pan
        }, config.CurrentConfig.Input);

        viewModel.ShortcutSearchText = "nothing matches this";
        Assert.True(viewModel.HasNoShortcutMatch);
    }

    [Fact]
    public void StepDropdownsOfferPresetsAndKeepAValueFromTheConfigurationFile()
    {
        var config = new MemoryConfiguration();
        // 4 与 40 不在档位中（例如手动编辑过配置文件），仍列出并选中，打开设置不会改掉。
        config.CurrentConfig.Input = new InputPreferences { WheelStep = 4, ShiftWheelStep = 40 };
        var viewModel = new SettingsWindowViewModel(config, new KeyLocalization());
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 10 }, viewModel.WheelStepValues);
        Assert.Equal(new[] { 5, 10, 20, 30, 40, 60 }, viewModel.ShiftWheelStepValues);
        Assert.Equal(3, viewModel.WheelStepIndex);
        Assert.Equal(4, viewModel.ShiftWheelStepIndex);
        // 档位只写数量，单位由“移动单位”决定；切换单位不重建档位。
        Assert.Equal("String.Settings.Shortcuts.Step.UnitOne", viewModel.WheelStepTexts[0]);
        Assert.Equal("String.Settings.Shortcuts.Step.Units", viewModel.WheelStepTexts[1]);
        var texts = viewModel.WheelStepTexts;
        viewModel.WheelStepUnitIndex = (int)WheelStepUnit.Second;
        Assert.Same(texts, viewModel.WheelStepTexts);

        var changed = new List<string?>();
        ((INotifyPropertyChanged)viewModel).PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        viewModel.WheelStepIndex = viewModel.WheelStepValues.ToList().IndexOf(10);
        Assert.Equal(10, viewModel.WheelStep);
        Assert.Contains(nameof(SettingsWindowViewModel.WheelStepIndex), changed);
        // 下拉框重建条目时可能短暂写回 -1，忽略。
        viewModel.ShiftWheelStepIndex = -1;
        Assert.Equal(40, viewModel.ShiftWheelStep);

        viewModel.OkCommand.Execute(null);
        Assert.Equal(10, config.CurrentConfig.Input.WheelStep);
        Assert.Equal(40, config.CurrentConfig.Input.ShiftWheelStep);
    }

    [Fact]
    public void ShortcutsPageShowsTheGroupedList()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        session.Dispatch(() =>
        {
            var viewModel = new SettingsWindowViewModel(new MemoryConfiguration(), new KeyLocalization())
            {
                SelectedTabIndex = 5
            };
            var window = new SettingsWindow { DataContext = viewModel };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToArray();
            Assert.Contains("String.Shortcuts.Group.Mouse", texts);
            Assert.Contains("String.Shortcuts.OpenVideo", texts);
            // 只显示当前平台的按键。
            var macOS = OperatingSystem.IsMacOS();
            Assert.Contains(macOS ? "⌘O" : "Ctrl+O", texts);
            Assert.DoesNotContain(macOS ? "Ctrl+O" : "⌘O", texts);

            viewModel.ShortcutSearchText = macOS ? "⌘O" : "Ctrl+O";
            Dispatcher.UIThread.RunJobs();
            texts = window.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToArray();
            Assert.Contains("String.Shortcuts.OpenVideo", texts);
            Assert.DoesNotContain("String.Shortcuts.Undo", texts);

            // 滚轮设置位于「预览」页；切换移动单位后步长下拉框仍显示所选档位，不会变成空白。
            viewModel.SelectedTabIndex = 2;
            Dispatcher.UIThread.RunJobs();
            viewModel.WheelStepUnitIndex = (int)WheelStepUnit.Second;
            Dispatcher.UIThread.RunJobs();
            var comboBoxes = window.GetVisualDescendants().OfType<ComboBox>().ToArray();
            var step = Assert.Single(comboBoxes, box => ReferenceEquals(box.ItemsSource, viewModel.WheelStepTexts));
            Assert.Equal("String.Settings.Shortcuts.Step.UnitOne", step.SelectionBoxItem);
            var shift = Assert.Single(comboBoxes, box => ReferenceEquals(box.ItemsSource, viewModel.ShiftWheelStepTexts));
            Assert.Equal("String.Settings.Shortcuts.Step.Units", shift.SelectionBoxItem);
            // 应用设置后按当前语言重建档位文字，选中项仍保留。
            viewModel.ApplyCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(viewModel.WheelStepTexts, step.ItemsSource);
            Assert.Equal(0, step.SelectedIndex);
            Assert.Equal(1, shift.SelectedIndex);
            window.Close();
        }, CancellationToken.None);
    }

    private sealed class MemoryConfiguration : IConfigurationService
    {
        public AppConfig CurrentConfig { get; } = new();
        public void Load() { }
        public void Save() { }
        public void ApplyTheme(string theme) { }
        public void ApplyLanguage(string language) { }
    }

    private static LocalizationManager Strings(Dictionary<string, string>? overrides = null) =>
        new(new KeyLocalization(overrides));

    /// <summary>直接返回资源键（可按键替换为指定文字），便于断言列表内容；同时记录读取过的键。</summary>
    private sealed class KeyLocalization(Dictionary<string, string>? overrides = null) : ILocalizationService
    {
        public HashSet<string> Requested { get; } = [];
        public List<SupportedLang> SupportedLanguages { get; } = [new("en-US", "English")];
        public string CurrentLanguageCode => "en-US";
        public void SwitchLanguage(string code) { }

        public string GetString(string key)
        {
            Requested.Add(key);
            return overrides?.GetValueOrDefault(key) ?? key;
        }

        public event Action? LanguageChanged { add { } remove { } }
    }
}
