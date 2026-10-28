using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Classic.Avalonia.Theme;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.ViewModels.Application;

namespace TSCutter.GUI.Views.Application;

public partial class SettingsWindow : ClassicWindow
{
    // 预先创建条目，使未展开的下拉框也能识别禁用标题，避免键盘选中标题。
    public static FuncValueConverter<IReadOnlyList<ThemePickerItem>?, ComboBoxItem[]?> ThemeItemsConverter { get; } =
        new(items => CreateThemeItems(items, false));

    public static FuncValueConverter<IReadOnlyList<ThemePickerItem>?, ComboBoxItem[]?> ThemePresetItemsConverter { get; } =
        new(items => CreateThemeItems(items, true));

    private static ComboBoxItem[]? CreateThemeItems(IReadOnlyList<ThemePickerItem>? items, bool presets) =>
        items?.Select(item =>
        {
            var control = new ComboBoxItem
            {
                DataContext = item,
                Tag = presets ? item : item.Theme,
                IsEnabled = item.IsSelectable
            };
            if (item.IsCustom)
            {
                // 未展开的条目还没有逻辑父级，直接监听应用资源以显示并更新本地化文案。
                control.Bind(ContentProperty, Avalonia.Application.Current!.GetResourceObservable("String.Settings.CustomTheme"));
            }
            else
                control.Content = item.Name;
            return control;
        }).ToArray();

    private SettingsWindowViewModel? _closeRequestSource;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        UpdateCloseRequestSource(DataContext as SettingsWindowViewModel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (IsVisible)
            UpdateCloseRequestSource(DataContext as SettingsWindowViewModel);
    }

    protected override void OnClosed(EventArgs e)
    {
        // 关闭或更换上下文时解除旧订阅，避免视图模型继续持有窗口。
        UpdateCloseRequestSource(null);
        base.OnClosed(e);
    }

    private void UpdateCloseRequestSource(SettingsWindowViewModel? source)
    {
        if (ReferenceEquals(_closeRequestSource, source))
            return;
        if (_closeRequestSource != null)
            _closeRequestSource.RequestClose -= Close;
        _closeRequestSource = source;
        if (_closeRequestSource != null)
            _closeRequestSource.RequestClose += Close;
    }
}