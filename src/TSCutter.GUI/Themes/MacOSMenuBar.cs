using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace TSCutter.GUI.Themes;

/// <summary>把现有菜单投射到 macOS 菜单栏，共用命令、语言资源和主题选择状态。</summary>
internal sealed class MacOSMenuBar : IDisposable
{
    private readonly Window _window;
    private readonly Control _menuFrame;
    private readonly Menu _sourceMenu;
    private readonly NativeMenu _windowMenu;
    private readonly NativeMenuItemBase[] _previousItems;
    private MenuProjection? _projection;

    public MacOSMenuBar(Window window, Menu menu, Control menuFrame)
    {
        _window = window;
        _menuFrame = menuFrame;
        _sourceMenu = menu;
        _windowMenu = NativeMenu.GetMenu(window) ?? new NativeMenu();
        _previousItems = _windowMenu.Items.ToArray();
        if (NativeMenu.GetMenu(window) == null)
            NativeMenu.SetMenu(window, _windowMenu);
        window.PropertyChanged += WindowPropertyChanged;
        Update();
    }

    private void WindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TopLevel.ActualThemeVariantProperty || e.Property == NativeMenu.IsNativeMenuExportedProperty)
            Update();
    }

    private void Update()
    {
        var active = OperatingSystem.IsMacOS();
        // Avalonia.Native 11.3 的导出器固定持有首次导出的根菜单，只能更新条目，不能替换根对象。
        if (active && _projection == null)
            _projection = new MenuProjection(_sourceMenu, _windowMenu);
        else if (!active)
            RestoreMenu();
        if (_projection is { } projection)
        {
            // 主题通知早于旧弹出模板清理；等模板切换完成后再恢复源菜单的逻辑树。
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_projection, projection)) projection.RefreshSource();
            }, DispatcherPriority.Loaded);
        }
        // macOS 外观由系统菜单接管整行；Windows 系列主题同时保留窗口内的完整菜单栏。
        _menuFrame.SetCurrentValue(Visual.IsVisibleProperty,
            !CustomTheme.UsesNativeTitleBar(_window.ActualThemeVariant) ||
            !NativeMenu.GetIsNativeMenuExported(_window));
    }

    public void Dispose()
    {
        _window.PropertyChanged -= WindowPropertyChanged;
        RestoreMenu();
        _menuFrame.SetCurrentValue(Visual.IsVisibleProperty, true);
    }

    private void RestoreMenu()
    {
        if (_projection == null) return;
        _projection.Dispose();
        _projection = null;
        foreach (var item in _previousItems) _windowMenu.Add(item);
    }

    /// <summary>只支持应用已有的 MenuItem 与 Separator，保留原菜单的逻辑树和绑定上下文。</summary>
    internal sealed class MenuProjection : IDisposable
    {
        private readonly ItemsControl _source;
        private readonly List<ItemProjection> _children = [];
        public NativeMenu Menu { get; }

        public MenuProjection(ItemsControl source, NativeMenu? target = null)
        {
            // 隐藏的菜单不会参与布局，主动生成条目容器，让子项获得逻辑父级、资源和 DataContext。
            source.ApplyTemplate();
            source.Presenter?.ApplyTemplate();
            _source = source;
            Menu = target ?? new NativeMenu();
            source.Items.CollectionChanged += ItemsChanged;
            Menu.NeedsUpdate += MenuNeedsUpdate;
            Rebuild();
        }

        private void MenuNeedsUpdate(object? sender, EventArgs e) => RefreshSource();

        internal void RefreshSource()
        {
            // 切换主题会替换隐藏菜单的模板；系统打开菜单或处理快捷键前，重新接上逻辑树。
            _source.ApplyTemplate();
            _source.Presenter?.ApplyTemplate();
            foreach (var child in _children) child.RefreshSource();
        }

        private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

        private void Rebuild()
        {
            foreach (var child in _children) child.Dispose();
            _children.Clear();
            Menu.Items.Clear();
            foreach (var item in _source.Items)
            {
                if (item is MenuItem source)
                {
                    var child = new ItemProjection(source);
                    _children.Add(child);
                    Menu.Add(child.Item);
                }
                else if (item is Separator)
                    Menu.Add(new NativeMenuItemSeparator());
            }
        }

        public void Dispose()
        {
            _source.Items.CollectionChanged -= ItemsChanged;
            Menu.NeedsUpdate -= MenuNeedsUpdate;
            foreach (var child in _children) child.Dispose();
            _children.Clear();
            Menu.Items.Clear();
        }
    }

    private sealed class ItemProjection : IDisposable, ICommand
    {
        private readonly MenuItem _source;
        private readonly MenuProjection _children;
        private ICommand? _command;
        public NativeMenuItem Item { get; } = new();

        public ItemProjection(MenuItem source)
        {
            _source = source;
            _children = new MenuProjection(source);
            source.PropertyChanged += SourcePropertyChanged;
            source.Items.CollectionChanged += ItemsChanged;
            Item.Command = this;
            Synchronize();
        }

        private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Synchronize();

        private void SourcePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == MenuItem.HeaderProperty || e.Property == MenuItem.InputGestureProperty ||
                e.Property == MenuItem.IsCheckedProperty || e.Property == MenuItem.ToggleTypeProperty ||
                e.Property == Visual.IsVisibleProperty || e.Property == InputElement.IsEffectivelyEnabledProperty ||
                e.Property == MenuItem.CommandProperty || e.Property == MenuItem.CommandParameterProperty)
                Synchronize();
        }

        private void Synchronize()
        {
            // Avalonia 不更新关闭状态下子菜单的 CanExecute，原生菜单需直接监听命令。
            if (!ReferenceEquals(_command, _source.Command))
            {
                if (_command != null) _command.CanExecuteChanged -= CommandCanExecuteChanged;
                _command = _source.Command;
                if (_command != null) _command.CanExecuteChanged += CommandCanExecuteChanged;
            }
            // 原生菜单不使用 Windows 助记键；保留英文下划线转义，由 Avalonia 导出器处理。
            var header = _source.Header?.ToString();
            Item.Header = header is null ? null : Regex.Replace(header, @"\(_[A-Za-z0-9]\)", "");
            Item.Gesture = _source.InputGesture;
            Item.IsVisible = _source.IsVisible;
            Item.ToggleType = (NativeMenuItemToggleType)_source.ToggleType;
            Item.IsChecked = _source.IsChecked;
            Item.Menu = _source.ItemCount > 0 ? _children.Menu : null;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }

        public void RefreshSource()
        {
            _children.RefreshSource();
            Synchronize();
        }

        public event EventHandler? CanExecuteChanged;
        private void CommandCanExecuteChanged(object? sender, EventArgs e) => CanExecuteChanged?.Invoke(this, e);

        public bool CanExecute(object? parameter) => _source.IsEnabled &&
            _source.GetLogicalAncestors().OfType<InputElement>().All(parent => parent.IsEnabled) &&
            (_source.Command?.CanExecute(_source.CommandParameter) ?? true);

        public void Execute(object? parameter)
        {
            if (!CanExecute(parameter) || _source.ItemCount > 0) return;
            if (_source.ToggleType != MenuItemToggleType.None)
                _source.SetCurrentValue(MenuItem.IsCheckedProperty,
                    _source.ToggleType == MenuItemToggleType.Radio || !_source.IsChecked);
            // MenuItem 的 Click 类处理器会执行命令；统一走这里，避免命令被执行两次。
            _source.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        }

        public void Dispose()
        {
            _source.PropertyChanged -= SourcePropertyChanged;
            _source.Items.CollectionChanged -= ItemsChanged;
            if (_command != null) _command.CanExecuteChanged -= CommandCanExecuteChanged;
            Item.Command = null;
            _children.Dispose();
        }
    }
}
