using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Classic.Avalonia.Theme;
using Classic.CommonControls.Dialogs;
using CommunityToolkit.Mvvm.ComponentModel;
using HanumanInstitute.MvvmDialogs.Avalonia;
using HanumanInstitute.MvvmDialogs.FrameworkDialogs;
using HanumanInstitute.MvvmDialogs.FileSystem;
using Splat;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Themes;
using TSCutter.GUI.Utils.Files;
using TSCutter.GUI.Web;
using Xunit;

namespace TSCutter.GUI.Tests.UI;

[Collection("Avalonia UI")]
public sealed class WebUiFilePickerTests
{
    [Fact]
    public async Task FileTypesFilterIndependentlyAndKeepFoldersNavigableInEachLanguage()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        var root = Directory.CreateTempSubdirectory("tscutter-file-types-");
        try
        {
            var input = root.CreateSubdirectory("media").FullName;
            var output = root.CreateSubdirectory("output").FullName;
            var nested = Directory.CreateDirectory(Path.Combine(input, "sub")).FullName;
            foreach (var file in new[] { "video.MP4", "recording.ts", "movie.mkv", "notes.txt", "video.mp4.bak", ".hidden.mp4" })
                File.WriteAllText(Path.Combine(input, file), "test");
            File.WriteAllText(Path.Combine(nested, "nested.MP4"), "test");
            File.WriteAllText(Path.Combine(nested, "notes.txt"), "test");
            File.WriteAllText(Path.Combine(output, "points.tscutpoints"), "test");
            await session.Dispatch<bool>(async () =>
            {
                using var resolver = new ModernDependencyResolver();
                using var scope = resolver.WithResolver();
                var languages = new LocalizationService();
                Locator.CurrentMutable.RegisterConstant<ILocalizationService>(languages);
                var owner = new ClassicWindow();
                owner.Show();
                var view = new ViewWrapper();
                view.InitializeExisting(new Model(), owner);
                var factory = new WebUiDialogFactory(new MediaWorkspace(input, output));
                try
                {
                    foreach (var language in new[] { "en-US", "zh-CN", "zh-TW" })
                    {
                        languages.SwitchLanguage(language);
                        var showing = factory.ShowDialogAsync(view, new OpenFileDialogSettings
                        {
                            Filters = [new(languages.GetString("String.VideoFiles"), ["mp4", ".TS", "*.mkv"]),
                                new(languages.GetString("String.AllFiles"), "*.*")]
                        });
                        var dialog = owner.OwnedWindows.Single();
                        try
                        {
                            Assert.False(dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "NewFolder").IsVisible);
                            var list = dialog.GetVisualDescendants().OfType<ListBox>().Single();
                            var types = dialog.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "FileType");
                            string[] Names() => list.ItemsSource!.Cast<object>().Select(item => item.ToString()!).ToArray();
                            Assert.Equal(0, types.SelectedIndex);
                            if (language == "en-US")
                            {
                                var app = Avalonia.Application.Current!;
                                var original = app.RequestedThemeVariant;
                                try
                                {
                                    foreach (var theme in new[] { ClassicTheme.Standard, CustomTheme.DarkStandard,
                                        CustomTheme.LunaBlue, CustomTheme.LunaDark, CustomTheme.Aero, CustomTheme.AeroDark,
                                        CustomTheme.Windows8, CustomTheme.Windows8Dark, CustomTheme.Windows10, CustomTheme.Windows10Dark,
                                        CustomTheme.Windows11, CustomTheme.Windows11Dark, CustomTheme.MacOS, CustomTheme.MacOSDark })
                                    {
                                        app.RequestedThemeVariant = theme;
                                        Dispatcher.UIThread.RunJobs();
                                        dialog.UpdateLayout();
                                        var icons = dialog.GetVisualDescendants().OfType<Image>()
                                            .Where(image => image.Classes.Contains("file-entry-icon")).ToArray();
                                        Assert.NotEmpty(icons);
                                        Assert.All(icons, image => Assert.NotNull(image.Source));
                                        Assert.Contains(icons, image => ReferenceEquals(image.Source, dialog.FindResource(dialog.ActualThemeVariant, "Icon.folder")));
                                        Assert.Contains(icons, image => ReferenceEquals(image.Source, dialog.FindResource(dialog.ActualThemeVariant, "Icon.file")));
                                        Assert.Equal(0, types.SelectedIndex);
                                    }
                                }
                                finally { app.RequestedThemeVariant = original; }
                            }
                            Assert.Equal(new[] { "movie.mkv", "recording.ts", "sub/", "video.MP4" }, Names().Order().ToArray());
                            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(),
                                text => text.Text == languages.GetString("String.WebUi.FileType"));
                            list.SelectedIndex = 1;
                            types.SelectedIndex = 1;
                            Assert.Null(list.SelectedItem);
                            Assert.Contains("notes.txt", Names());
                            Assert.Contains("video.mp4.bak", Names());
                            Assert.DoesNotContain(".hidden.mp4", Names());
                            types.SelectedIndex = 0;
                            list.SelectedItem = list.ItemsSource!.Cast<object>().Single(item => item.ToString() == "sub/");
                            dialog.GetVisualDescendants().OfType<Button>().Single(button =>
                                Equals(button.Content, languages.GetString("String.WebUi.Select"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Assert.Equal(new[] { "nested.MP4" }, Names());
                        }
                        finally { dialog.Close(); await showing; }
                        var pointSelection = factory.ShowDialogAsync(view, new OpenFileDialogSettings
                        {
                            Filters = [new(languages.GetString("String.ClipPoints.Files"), ["TSCUTPOINTS"])]
                        });
                        var pointDialog = owner.OwnedWindows.Single(window => window.IsVisible);
                        try
                        {
                            pointDialog.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "WorkspaceLocation").SelectedIndex = 1;
                            var pointList = pointDialog.GetVisualDescendants().OfType<ListBox>().Single();
                            Assert.Equal("points.tscutpoints", Assert.Single(pointList.ItemsSource!.Cast<object>()).ToString());
                            pointList.SelectedIndex = 0;
                            pointDialog.GetVisualDescendants().OfType<Button>().Single(button =>
                                Equals(button.Content, languages.GetString("String.WebUi.Select"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            var picked = Assert.IsAssignableFrom<IEnumerable<IDialogStorageFile>>(await pointSelection);
                            Assert.Equal(Path.Combine(output, "points.tscutpoints"), Assert.Single(picked).LocalPath);
                        }
                        finally { pointDialog.Close(); await pointSelection; }
                    }
                    return true;
                }
                finally { owner.Close(); }
            }, CancellationToken.None);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task SaveAndExportFolderPickersCreateFoldersWithinTheOutputWorkspace()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        var root = Directory.CreateTempSubdirectory("tscutter-new-folders-");
        try
        {
            var input = root.CreateSubdirectory("media").FullName;
            var output = root.CreateSubdirectory("output").FullName;
            File.WriteAllText(Path.Combine(output, "taken"), "existing");
            await session.Dispatch<bool>(async () =>
            {
                using var resolver = new ModernDependencyResolver();
                using var scope = resolver.WithResolver();
                var languages = new LocalizationService();
                Locator.CurrentMutable.RegisterConstant<ILocalizationService>(languages);
                var owner = new ClassicWindow();
                owner.Show();
                var view = new ViewWrapper();
                view.InitializeExisting(new Model(), owner);
                var factory = new WebUiDialogFactory(new MediaWorkspace(input, output));
                try
                {
                    foreach (var language in new[] { "en-US", "zh-CN", "zh-TW" })
                    {
                        languages.SwitchLanguage(language);
                        foreach (var selectingFolder in new[] { false, true })
                        {
                            var showing = selectingFolder
                                ? factory.ShowDialogAsync(view, new OpenFolderDialogSettings { SuggestedStartLocation = new DesktopDialogStorageFolder(output) })
                                : factory.ShowDialogAsync(view, new SaveFileDialogSettings { SuggestedFileName = "clip", DefaultExtension = "mp4" });
                            var dialog = owner.OwnedWindows.Single();
                            try
                            {
                                var buttons = dialog.GetVisualDescendants().OfType<Button>().ToArray();
                                var newFolder = buttons.Single(button => button.Name == "NewFolder");
                                var create = buttons.Single(button => button.Name == "CreateFolder");
                                var name = dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "NewFolderName");
                                Assert.True(newFolder.IsVisible);
                                Assert.Equal(languages.GetString("String.WebUi.NewFolder"), newFolder.Content);
                                Click(newFolder);
                                foreach (var invalid in new[] { "../media/escape", "bad/name", ".." })
                                {
                                    name.Text = invalid;
                                    Click(create);
                                    Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), text =>
                                        text.Text == languages.GetString("String.WebUi.InvalidFolderName"));
                                }
                                name.Text = "taken";
                                Click(create);
                                Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), text =>
                                    text.Text == languages.GetString("String.WebUi.FolderExists"));
                                var folder = (selectingFolder ? "batch-" : "clip-") + language;
                                name.Text = folder;
                                Click(create);
                                var created = Path.Combine(output, folder);
                                Assert.True(Directory.Exists(created));
                                Click(buttons.Single(button => Equals(button.Content, languages.GetString(selectingFolder ? "String.WebUi.Select" : "String.WebUi.Save"))));
                                var chosen = await showing;
                                if (selectingFolder)
                                    Assert.Equal(created, Assert.Single(Assert.IsType<IDialogStorageFolder[]>(chosen)).LocalPath);
                                else
                                    Assert.Equal(Path.Combine(created, "clip.mp4"), Assert.IsType<DesktopDialogStorageFile>(chosen).LocalPath);
                            }
                            finally
                            {
                                dialog.Close();
                                await showing;
                            }
                        }
                    }
                    Assert.Empty(Directory.GetFileSystemEntries(input));
                    Assert.Equal("existing", File.ReadAllText(Path.Combine(output, "taken")));
                    return true;
                }
                finally { owner.Close(); }

                static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }, CancellationToken.None);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task SavingUsesListedNamesAndConfirmsOverwrite()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        var root = Directory.CreateTempSubdirectory("tscutter-save-name-");
        try
        {
            var input = root.CreateSubdirectory("media").FullName;
            var output = root.CreateSubdirectory("output").FullName;
            var nested = root.CreateSubdirectory("output/nested").FullName;
            File.WriteAllText(Path.Combine(output, "old.name.ts"), "existing");
            File.WriteAllText(Path.Combine(output, "wrong.mp4"), "other format");
            File.WriteAllText(Path.Combine(nested, "notes.txt"), "other file");
            File.WriteAllText(Path.Combine(nested, "reference.ts"), "reference");
            await session.Dispatch<bool>(async () =>
            {
                using var resolver = new ModernDependencyResolver();
                using var scope = resolver.WithResolver();
                var languages = new LocalizationService();
                Locator.CurrentMutable.RegisterConstant<ILocalizationService>(languages);
                languages.SwitchLanguage("zh-CN");
                var owner = new ClassicWindow();
                owner.Show();
                var view = new ViewWrapper();
                view.InitializeExisting(new Model(), owner);
                var workspace = new MediaWorkspace(input, output);
                var factory = new WebUiDialogFactory(workspace);
                var access = new MediaFileAccess(workspace, error => error.ToString());
                var previousAccess = MediaFileAccess.Current;
                MediaFileAccess.ConfigureForProcess(access);
                try
                {
                    var showing = factory.ShowDialogAsync(view, new SaveFileDialogSettings
                    {
                        SuggestedFileName = "suggested.ts", DefaultExtension = ".ts", Filters = [new("TS", "*.TS"), new("All files", "*.*")]
                    });
                    var dialog = owner.OwnedWindows.Single(window => window.IsVisible);
                    try
                    {
                        var list = dialog.GetVisualDescendants().OfType<ListBox>().Single();
                        var name = dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "FileName");
                        var save = dialog.GetVisualDescendants().OfType<Button>().Single(button =>
                            Equals(button.Content, languages.GetString("String.WebUi.Save")));
                        var types = dialog.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "FileType");
                        string[] Names() => list.ItemsSource!.Cast<object>().Select(item => item.ToString()!).Order().ToArray();
                        Assert.True(types.IsVisible);
                        Assert.Equal(0, types.SelectedIndex);
                        Assert.Equal(new[] { "nested/", "old.name.ts" }, Names());
                        name.Text = "wrong.mp4";
                        Click(save);
                        Assert.False(showing.IsCompleted);
                        Assert.Empty(dialog.OwnedWindows);
                        Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), text =>
                            text.Text == string.Format(languages.GetString("String.WebUi.InvalidFileExtension"), ".ts"));
                        Assert.False(access.CanOverwrite(Path.Combine(output, "wrong.mp4")));
                        types.SelectedIndex = 1;
                        Assert.Contains("wrong.mp4", Names());
                        list.SelectedItem = list.ItemsSource!.Cast<object>().Single(item => item.ToString() == "wrong.mp4");
                        Click(save);
                        Assert.False(showing.IsCompleted);
                        Assert.Empty(dialog.OwnedWindows);
                        Assert.False(access.CanOverwrite(Path.Combine(output, "wrong.mp4")));
                        types.SelectedIndex = 0;
                        Assert.DoesNotContain("wrong.mp4", Names());
                        list.SelectedItem = list.ItemsSource!.Cast<object>().Single(item => item.ToString() == "old.name.ts");
                        Assert.Equal("old.name.ts", name.Text);
                        Click(save);
                        var confirmation = dialog.OwnedWindows.Single(window => window.IsVisible);
                        Assert.Contains(confirmation.GetVisualDescendants().OfType<TextBlock>(), text =>
                            text.Text == string.Format(languages.GetString("String.WebUi.ConfirmOverwrite"), "old.name.ts"));
                        confirmation.Close(MessageBoxResult.No);
                        await Task.Delay(1);
                        Dispatcher.UIThread.RunJobs();
                        Assert.False(showing.IsCompleted);
                        Assert.Throws<IOException>(() => access.OpenOutput(Path.Combine(output, "old.name.ts")));
                        list.SelectedItem = list.ItemsSource!.Cast<object>().Single(item => item.ToString() == "nested/");
                        Click(save);
                        Assert.Equal("old.name.ts", name.Text);
                        Assert.Equal(new[] { "reference.ts" }, Names());
                        list.SelectedItem = list.ItemsSource!.Cast<object>().Single(item => item.ToString() == "reference.ts");
                        Assert.Equal("reference.ts", name.Text);
                        name.Text = "renamed";
                        Click(save);
                        Assert.Equal(Path.Combine(nested, "renamed.ts"),
                            Assert.IsType<DesktopDialogStorageFile>(await showing).LocalPath);
                    }
                    finally { dialog.Close(); await showing; }
                    var overwrite = factory.ShowDialogAsync(view, new SaveFileDialogSettings
                    {
                        SuggestedFileName = "old.name.ts", DefaultExtension = ".ts", Filters = [new("TS", "*.TS"), new("All files", "*.*")]
                    });
                    var saveDialog = owner.OwnedWindows.Single(window => window.IsVisible);
                    try
                    {
                        Click(saveDialog.GetVisualDescendants().OfType<Button>().Single(button =>
                            Equals(button.Content, languages.GetString("String.WebUi.Save"))));
                        saveDialog.OwnedWindows.Single(window => window.IsVisible).Close(MessageBoxResult.Yes);
                        var selected = Assert.IsType<DesktopDialogStorageFile>(await overwrite.WaitAsync(TimeSpan.FromSeconds(3))).LocalPath;
                        Assert.Equal(Path.Combine(output, "old.name.ts"), selected);
                        Assert.True(access.CanOverwrite(selected));
                        var discarded = factory.ShowDialogAsync(view, new SaveFileDialogSettings
                        {
                            SuggestedFileName = "another.ts", DefaultExtension = "ts", Filters = [new("TS", "ts")]
                        });
                        var discardedDialog = owner.OwnedWindows.Single(window => window.IsVisible);
                        try
                        {
                            Assert.False(access.CanOverwrite(selected));
                            Assert.Throws<IOException>(() => access.OpenOutput(selected));
                            Assert.Equal("existing", File.ReadAllText(selected));
                        }
                        finally { discardedDialog.Close(); await discarded; }
                        access.GrantOverwrite(selected);
                        using (var writer = new StreamWriter(access.OpenOutput(selected)))
                            writer.Write("updated");
                        Assert.Throws<IOException>(() => access.OpenOutput(selected));
                        Assert.Throws<IOException>(() => access.OpenOutput(Path.Combine(nested, "reference.ts")));
                        Assert.Equal("reference", File.ReadAllText(Path.Combine(nested, "reference.ts")));
                    }
                    finally { saveDialog.Close(); await overwrite; }
                    return true;
                }
                finally { owner.Close(); MediaFileAccess.ConfigureForProcess(previousAccess); }
                static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }, CancellationToken.None);
            Assert.Equal("updated", File.ReadAllText(Path.Combine(output, "old.name.ts")));
            Assert.Equal("other format", File.ReadAllText(Path.Combine(output, "wrong.mp4")));
        }
        finally { root.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothLocationsCanBeOpenedAndSavedAndIdenticalLocationsAreMerged(bool sameDirectory)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        var root = Directory.CreateTempSubdirectory("tscutter-picker-locations-");
        try
        {
            var input = root.CreateSubdirectory("media").FullName;
            var output = sameDirectory ? input : root.CreateSubdirectory("output").FullName;
            var repaired = Path.Combine(output, "repaired.ts");
            File.WriteAllText(repaired, "generated output");
            await session.Dispatch<bool>(async () =>
            {
                using var resolver = new ModernDependencyResolver();
                using var scope = resolver.WithResolver();
                var languages = new LocalizationService();
                Locator.CurrentMutable.RegisterConstant<ILocalizationService>(languages);
                languages.SwitchLanguage("en-US");
                var owner = new ClassicWindow { Width = 360, Height = 600 };
                owner.Show();
                var view = new ViewWrapper();
                view.InitializeExisting(new Model(), owner);
                var workspace = new MediaWorkspace(input, output);
                var factory = new WebUiDialogFactory(workspace);
                try
                {
                    var opening = factory.ShowDialogAsync(view, new OpenFileDialogSettings { Filters = [new("TS", "ts")] });
                    var dialog = owner.OwnedWindows.Single();
                    try
                    {
                        var location = dialog.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "WorkspaceLocation");
                        Assert.Equal(sameDirectory ? 1 : 2, location.Items.Count);
                        Assert.Equal(!sameDirectory, location.IsEnabled);
                        location.SelectedIndex = sameDirectory ? 0 : 1;
                        var list = dialog.GetVisualDescendants().OfType<ListBox>().Single();
                        list.SelectedItem = list.ItemsSource!.Cast<object>().Single(item => item.ToString() == "repaired.ts");
                        Click(dialog, "String.WebUi.Select");
                        Assert.Equal(repaired, Assert.Single(Assert.IsAssignableFrom<IEnumerable<IDialogStorageFile>>(await opening)).LocalPath);
                    }
                    finally { dialog.Close(); await opening; }

                    var saving = factory.ShowDialogAsync(view, new SaveFileDialogSettings
                    {
                        SuggestedStartLocation = new DesktopDialogStorageFolder(input),
                        SuggestedFileName = "project.tscut", DefaultExtension = "tscut"
                    });
                    var saveDialog = owner.OwnedWindows.Single(window => window.IsVisible);
                    try
                    {
                        Assert.Equal(0, saveDialog.GetVisualDescendants().OfType<ComboBox>()
                            .Single(box => box.Name == "WorkspaceLocation").SelectedIndex);
                        var create = saveDialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "NewFolder");
                        Assert.True(create.IsVisible);
                        create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        saveDialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "NewFolderName").Text = "projects";
                        saveDialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "CreateFolder")
                            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Click(saveDialog, "String.WebUi.Save");
                        var path = Assert.IsType<DesktopDialogStorageFile>(await saving).LocalPath;
                        Assert.Equal(Path.Combine(input, "projects", "project.tscut"), path);
                        new MediaFileAccess(workspace, error => error.ToString()).DemandProjectPaths([repaired], [path]);
                        Assert.True(Directory.Exists(Path.GetDirectoryName(path)));
                    }
                    finally { saveDialog.Close(); await saving; }
                    return true;
                }
                finally { owner.Close(); }
                void Click(Window window, string key) => window.GetVisualDescendants().OfType<Button>()
                    .Single(button => Equals(button.Content, languages.GetString(key))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }, CancellationToken.None);
        }
        finally { root.Delete(recursive: true); }
    }

    private sealed class Model : ObservableObject
    {
    }
}
