using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Classic.Avalonia.Theme;
using Classic.CommonControls.Dialogs;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.Avalonia;
using HanumanInstitute.MvvmDialogs.FileSystem;
using HanumanInstitute.MvvmDialogs.FrameworkDialogs;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.Utils.Files;

namespace TSCutter.GUI.Web;

internal sealed class WebUiDialogFactory(MediaWorkspace workspace) : DialogFactory
{
    public override async Task<object?> ShowDialogAsync<TSettings>(IView? owner, TSettings settings)
    {
        var window = owner?.RefObj as Window ??
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window is null)
            return null;

        switch (settings)
        {
            case OpenFileDialogSettings open:
                return (await PickAsync(window, open.Title, workspace.InputDirectory, PickerKind.Open,
                    open.AllowMultiple == true, open.Filters, suggestedDirectory: open.SuggestedStartLocation?.LocalPath))
                    .Select(path => (IDialogStorageFile)new DesktopDialogStorageFile(path)).ToArray();
            case SaveFileDialogSettings save:
                MediaFileAccess.Current.ClearOverwriteGrants();
                var destination = await PickAsync(window, save.Title, workspace.OutputDirectory, PickerKind.Save,
                    false, save.Filters, save.SuggestedFileName, save.DefaultExtension, save.SuggestedStartLocation?.LocalPath);
                return destination.Length == 0 ? null : new DesktopDialogStorageFile(destination[0]);
            case OpenFolderDialogSettings folder:
                return (await PickAsync(window, folder.Title, workspace.InputDirectory, PickerKind.Folder, false, [],
                    suggestedDirectory: folder.SuggestedStartLocation?.LocalPath))
                    .Select(path => (IDialogStorageFolder)new DesktopDialogStorageFolder(path)).ToArray();
            default:
                return await base.ShowDialogAsync(owner, settings);
        }
    }

    private enum PickerKind
    {
        Open, Save, Folder
    }

    private sealed record Entry(string Name, bool IsDirectory)
    {
        public override string ToString() => IsDirectory ? Name + "/" : Name;
    }

    private sealed record Location(string Name, string Path)
    {
        public override string ToString() => Name;
    }

    private sealed record FileType(string Name, string[] Extensions)
    {
        public bool Matches(string name) => Extensions.Length == 0 || Extensions.Any(extension =>
            extension.Length == 0 || name.EndsWith("." + extension, StringComparison.OrdinalIgnoreCase));
        public override string ToString() => Name;
    }

    private async Task<string[]> PickAsync(Window owner, string? title, string root, PickerKind kind,
        bool multiple, IList<FileFilter> filters, string? suggestedName = null, string? defaultExtension = null,
        string? suggestedDirectory = null)
    {
        var strings = new LocalizationManager(App.LocalizationService);
        var types = filters.Select(filter => new FileType(filter.Name,
            (filter.Extensions ?? []).Select(extension => extension.Trim().TrimStart('*', '.')).ToArray())).ToArray();
        var outputExtensions = string.IsNullOrWhiteSpace(defaultExtension)
            ? types.SelectMany(type => type.Extensions).Where(extension => extension.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : [defaultExtension.Trim().TrimStart('.')];
        var fileType = new ComboBox
        {
            Name = "FileType",
            ItemsSource = types,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsVisible = kind != PickerKind.Folder && types.Length > 0
        };
        var directory = root;
        if (suggestedDirectory is not null && workspace.AllowsDirectory(suggestedDirectory))
        {
            directory = suggestedDirectory;
            if (directory != root && !MediaFileUtil.IsWithinDirectory(directory, root))
                root = root == workspace.InputDirectory ? workspace.OutputDirectory : workspace.InputDirectory;
        }
        var saving = kind == PickerKind.Save;
        var pathLabel = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        var error = new TextBlock { TextWrapping = global::Avalonia.Media.TextWrapping.Wrap };
        var entries = new ListBox { SelectionMode = multiple ? SelectionMode.Multiple : SelectionMode.Single };
        entries.ItemTemplate = new FuncDataTemplate<Entry>((item, _) =>
        {
            if (item is not { } entry)
                return null;
            var icon = new Image
            {
                Width = 18,
                Height = 18,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Classes = { "theme-icon", "file-entry-icon" }
            };
            // 动态资源随主题及深浅色切换，文件列表与软件其余图标保持一致。
            icon.Bind(Image.SourceProperty, new DynamicResourceExtension(entry.IsDirectory ? "Icon.folder" : "Icon.file"));
            var label = new TextBlock
            {
                Text = entry.Name,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { icon, label } };
            Grid.SetColumn(label, 1);
            ToolTip.SetTip(row, entry.Name);
            return row;
        });
        var name = new TextBox { Name = "FileName", Text = Path.GetFileName(suggestedName), IsVisible = saving };
        var up = new Button { Content = strings.String_WebUi_Up };
        var sameDirectory = MediaFileUtil.PathsEqual(workspace.InputDirectory, workspace.OutputDirectory);
        var locations = sameDirectory
            ? new[] { new Location(strings.String_WebUi_WorkspaceFolder, workspace.InputDirectory) }
            : new[] { new Location(strings.String_WebUi_InputFolder, workspace.InputDirectory),
                new Location(strings.String_WebUi_OutputFolder, workspace.OutputDirectory) };
        var location = new ComboBox
        {
            Name = "WorkspaceLocation", ItemsSource = locations,
            SelectedIndex = sameDirectory || root == workspace.InputDirectory ? 0 : 1,
            MinWidth = 140, IsEnabled = !sameDirectory
        };
        global::Avalonia.Automation.AutomationProperties.SetName(location, strings.String_WebUi_Location);
        var locationLabel = new TextBlock { Text = strings.String_WebUi_Location, VerticalAlignment = VerticalAlignment.Center };
        var accept = new Button { Content = saving ? strings.String_WebUi_Save : strings.String_WebUi_Select, IsDefault = true };
        var cancel = new Button { Content = strings.String_WebUi_Cancel, IsCancel = true };
        var newFolder = new Button { Name = "NewFolder", Content = strings.String_WebUi_NewFolder };
        var folderName = new TextBox { Name = "NewFolderName", Watermark = strings.String_WebUi_FolderName };
        global::Avalonia.Automation.AutomationProperties.SetName(folderName, strings.String_WebUi_FolderName);
        var createFolder = new Button { Name = "CreateFolder", Content = strings.String_WebUi_CreateFolder, Margin = new Thickness(8, 0, 0, 0) };
        var cancelFolder = new Button { Content = strings.String_WebUi_Cancel, Margin = new Thickness(8, 0, 0, 0) };
        var creation = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), IsVisible = false,
            Children = { folderName, createFolder, cancelFolder } };
        Grid.SetColumn(createFolder, 1);
        Grid.SetColumn(cancelFolder, 2);
        var dialog = new ClassicWindow
        {
            Title = title ?? strings.String_WebUi_SharedFiles,
            CanResize = false,
            ShowInTaskbar = false,
            CanMaximize = false,
            CanMinimize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        string? Resolve(string path) => workspace.ResolvePath(root, Path.GetRelativePath(root, path));

        bool MatchesFile(string fileName) => fileType.SelectedItem is not FileType selected || selected.Matches(fileName);

        void SetCreatingFolder(bool visible)
        {
            creation.IsVisible = visible;
            createFolder.IsDefault = visible;
            accept.IsDefault = !visible;
            error.Text = null;
            if (visible)
            {
                folderName.Text = string.Empty;
                folderName.Focus();
            }
            else
                entries.Focus();
        }

        void ShowDirectory()
        {
            error.Text = null;
            newFolder.IsVisible = kind != PickerKind.Open;
            entries.SelectedItems?.Clear();
            var relative = Path.GetRelativePath(root, directory);
            pathLabel.Text = relative == "." ? "/" : "/" + relative;
            up.IsEnabled = relative != ".";
            try
            {
                entries.ItemsSource = new DirectoryInfo(directory).EnumerateFileSystemInfos()
                    .Where(item => (item.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) == 0 &&
                        !item.Name.StartsWith('.') && (item.Attributes.HasFlag(FileAttributes.Directory) ||
                            kind != PickerKind.Folder && MatchesFile(item.Name)) && Resolve(item.FullName) is not null)
                    .Select(item => new Entry(item.Name, item.Attributes.HasFlag(FileAttributes.Directory)))
                    .OrderByDescending(item => item.IsDirectory).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                entries.ItemsSource = Array.Empty<Entry>();
                error.Text = strings.String_WebUi_FolderUnreadable;
            }
        }

        void CreateFolder()
        {
            var folder = folderName.Text?.Trim() ?? string.Empty;
            // 每次只在当前目录创建一层，复用工作区校验拒绝越界、链接及非法名称。
            var target = folder.Length == 0 || folder.Contains('/') || folder.Contains('\\') || folder.StartsWith('.') ||
                folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                ? null : Resolve(Path.Combine(directory, folder));
            if (!workspace.AllowsDirectory(directory) || target is null)
            {
                error.Text = strings.String_WebUi_InvalidFolderName;
                return;
            }
            if (Path.Exists(target))
            {
                error.Text = strings.String_WebUi_FolderExists;
                return;
            }
            try
            {
                Directory.CreateDirectory(target);
                if (!workspace.AllowsDirectory(target))
                    throw new UnauthorizedAccessException();
                directory = target;
                SetCreatingFolder(false);
                ShowDirectory();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                error.Text = strings.String_WebUi_FolderCreateFailed;
            }
        }

        async Task ChooseAsync()
        {
            if (entries.SelectedItem is Entry { IsDirectory: true } selected)
            {
                var next = Resolve(Path.Combine(directory, selected.Name));
                if (next is not null && Directory.Exists(next))
                {
                    directory = next;
                    ShowDirectory();
                }
                return;
            }

            if (saving)
            {
                error.Text = null;
                var fileName = name.Text?.Trim() ?? string.Empty;
                if (fileName.Length != 0 && !fileName.Contains('/') && !fileName.Contains('\\'))
                {
                    var extension = outputExtensions.FirstOrDefault();
                    if (!Path.HasExtension(fileName) && !string.IsNullOrEmpty(extension))
                        fileName += "." + extension;
                    // 文件列表可切换过滤项，输出格式仍由导出任务决定，保存前必须校验完整文件名。
                    if (outputExtensions.Length > 0 && !outputExtensions.Any(value =>
                            fileName.EndsWith("." + value, StringComparison.OrdinalIgnoreCase)))
                    {
                        error.Text = string.Format(strings.String_WebUi_InvalidFileExtension,
                            string.Join(", ", outputExtensions.Select(value => "." + value)));
                        return;
                    }
                    var target = Resolve(Path.Combine(directory, fileName));
                    if (target is not null && !Directory.Exists(target))
                    {
                        if (File.Exists(target))
                        {
                            var answer = await MessageBox.ShowDialog(dialog,
                                string.Format(strings.String_WebUi_ConfirmOverwrite, fileName),
                                strings.String_WebUi_Save, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                            if (answer != MessageBoxResult.Yes)
                                return;
                            target = Resolve(target);
                            if (target is null || Directory.Exists(target))
                            {
                                error.Text = strings.String_WebUi_InvalidFileName;
                                return;
                            }
                            MediaFileAccess.Current.GrantOverwrite(target);
                        }
                        dialog.Close(new[] { target });
                        return;
                    }
                }
                error.Text = strings.String_WebUi_InvalidFileName;
                return;
            }

            if (kind == PickerKind.Folder)
            {
                if (workspace.AllowsDirectory(directory))
                    dialog.Close(new[] { directory });
                return;
            }

            var files = entries.SelectedItems?.OfType<Entry>().Where(item => !item.IsDirectory)
                .Select(item => Resolve(Path.Combine(directory, item.Name))).OfType<string>()
                .Where(path => File.Exists(path) && MatchesFile(Path.GetFileName(path))).ToArray() ?? [];
            if (files.Length != 0)
                dialog.Close(files);
        }

        newFolder.Click += (_, _) => SetCreatingFolder(true);
        createFolder.Click += (_, _) => CreateFolder();
        cancelFolder.Click += (_, _) => SetCreatingFolder(false);
        creation.AddHandler(InputElement.KeyDownEvent, (_, args) =>
        {
            if (args.Key != Key.Escape)
                return;
            args.Handled = true;
            SetCreatingFolder(false);
        }, RoutingStrategies.Tunnel);
        up.Click += (_, _) =>
        {
            SetCreatingFolder(false);
            if (directory != root)
                directory = Path.GetDirectoryName(directory)!;
            ShowDirectory();
        };
        location.SelectionChanged += (_, _) =>
        {
            if (location.SelectedItem is not Location selected)
                return;
            SetCreatingFolder(false);
            root = selected.Path;
            directory = root;
            ShowDirectory();
        };
        // 各过滤项独立生效，只有用户切换到“所有文件”时才放开后缀限制。
        fileType.SelectionChanged += (_, _) => ShowDirectory();
        entries.SelectionChanged += (_, _) =>
        {
            if (saving && entries.SelectedItem is Entry { IsDirectory: false } file)
            {
                name.Text = file.Name;
                name.SelectionStart = 0;
                name.SelectionEnd = Path.GetFileNameWithoutExtension(file.Name).Length;
                error.Text = null;
            }
        };
        async void ChooseSafely(object? sender, RoutedEventArgs args)
        {
            try { await ChooseAsync(); }
            catch (Exception exception) { error.Text = exception.Message; }
        }
        entries.DoubleTapped += ChooseSafely;
        accept.Click += ChooseSafely;
        cancel.Click += (_, _) => dialog.Close();
        var navigation = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,*"), ColumnSpacing = 8,
            Children = { locationLabel, location, up, newFolder, pathLabel } };
        Grid.SetColumn(location, 1);
        Grid.SetColumn(up, 2);
        Grid.SetColumn(newFolder, 3);
        Grid.SetColumn(pathLabel, 4);
        var header = new StackPanel { Spacing = 8, Children = { navigation, creation } };
        pathLabel.Margin = new Thickness(8, 0, 0, 0);
        pathLabel.VerticalAlignment = VerticalAlignment.Center;
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { accept, cancel }
        };
        var typeLabel = new TextBlock
        {
            Text = strings.String_WebUi_FileType,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var typeRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            IsVisible = fileType.IsVisible,
            Children = { typeLabel, fileType }
        };
        Grid.SetColumn(fileType, 1);
        var nameLabel = new TextBlock { Text = strings.String_WebUi_FileName, VerticalAlignment = VerticalAlignment.Center };
        var nameRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8,
            IsVisible = saving, Children = { nameLabel, name } };
        Grid.SetColumn(name, 1);
        var footer = new StackPanel { Spacing = 6, Children = { nameRow, typeRow, error, actions } };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        header.Margin = new Thickness(0, 0, 0, 8);
        footer.Margin = new Thickness(0, 8, 0, 0);
        accept.MinWidth = cancel.MinWidth = 80;
        dialog.Content = new DockPanel { Margin = new Thickness(12), Children = { header, footer, entries } };
        dialog.Opened += (_, _) =>
        {
            if (saving)
                name.Focus();
            else
                entries.Focus();
        };
        var viewport = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow ?? owner;
        void FitViewport(object? sender, SizeChangedEventArgs? args)
        {
            // 手机旋转时收缩文件列表，确保窗口底部的操作按钮仍可点击。
            var size = viewport.ClientSize;
            dialog.Width = size.Width > 16 ? Math.Min(640, size.Width - 16) : 640;
            dialog.Height = size.Height > 16 ? Math.Min(460, size.Height - 16) : 460;
            var narrow = dialog.Width < 520;
            navigation.ColumnDefinitions = new ColumnDefinitions(narrow ? "Auto,*,Auto" : "Auto,Auto,Auto,Auto,*");
            navigation.RowDefinitions = new RowDefinitions(narrow ? "Auto,Auto" : "Auto");
            navigation.RowSpacing = narrow ? 6 : 0;
            Grid.SetRow(newFolder, narrow ? 1 : 0);
            Grid.SetColumn(newFolder, narrow ? 2 : 3);
            Grid.SetRow(pathLabel, narrow ? 1 : 0);
            Grid.SetColumn(pathLabel, narrow ? 0 : 4);
            Grid.SetColumnSpan(pathLabel, narrow ? 2 : 1);
        }
        FitViewport(null, null);
        ShowDirectory();
        viewport.SizeChanged += FitViewport;
        try
        {
            return await dialog.ShowDialog<string[]?>(owner) ?? [];
        }
        finally { viewport.SizeChanged -= FitViewport; }
    }
}
