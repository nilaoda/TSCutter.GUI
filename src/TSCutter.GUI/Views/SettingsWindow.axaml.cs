using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Classic.Avalonia.Theme;
using TSCutter.GUI.Models;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.Views;

public partial class SettingsWindow : ClassicWindow
{
    // 预先创建条目，使未展开的下拉框也能识别禁用标题，避免键盘选中标题。
    public static FuncValueConverter<IReadOnlyList<ThemePickerItem>?, ComboBoxItem[]?> ThemeItemsConverter { get; } =
        new(items => items?.Select(item => new ComboBoxItem
        {
            Content = item.Name,
            DataContext = item,
            Tag = item.Theme,
            IsEnabled = item.IsSelectable
        }).ToArray());

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