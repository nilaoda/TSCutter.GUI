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
                            var types = dialog.GetVisualDescendants().OfType<ComboBox>().Single();
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
                            pointDialog.GetVisualDescendants().OfType<Button>().Single(button =>
                                Equals(button.Content, languages.GetString("String.WebUi.OutputFolder"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
            File.WriteAllText(Path.Combine(output, "old.name.mp4"), "existing");
            File.WriteAllText(Path.Combine(nested, "reference.mp4"), "reference");
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
                var factory = new WebUiDialogFactory(new MediaWorkspace(input, output));
                try
                {
                    var showing = factory.ShowDialogAsync(view, new SaveFileDialogSettings
                    {
                        SuggestedFileName = "suggested.mp4", DefaultExtension = "mp4", Filters = [new("MP4", "mp4")]
                    });
                    var dialog = owner.OwnedWindows.Single(window => window.IsVisible);
                    try
                    {
                        var list = dialog.GetVisualDescendants().OfType<ListBox>().Single();
                        var name = dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "FileName");
                        var save = dialog.GetVisualDescendants().OfType<Button>().Single(button =>
                            Equals(button.Content, languages.GetString("String.WebUi.Save")));
                        list.SelectedItem = list.ItemsSource!.Cast<object>().Single(item => item.ToString() == "old.name.mp4");
                        Assert.Equal("old.name.mp4", name.Text);
                        Click(save);
                        var confirmation = dialog.OwnedWindows.Single(window => window.IsVisible);
                        Assert.Contains(confirmation.GetVisualDescendants().OfType<TextBlock>(), text =>
                            text.Text == string.Format(languages.GetString("String.WebUi.ConfirmOverwrite"), "old.name.mp4"));
                        confirmation.Close(MessageBoxResult.No);
                        await Task.Delay(1);
                        Dispatcher.UIThread.RunJobs();
                        Assert.False(showing.IsCompleted);
                        list.SelectedItem = list.ItemsSource!.Cast<object>().Single(item => item.ToString() == "nested/");
                        Click(save);
                        Assert.Equal("old.name.mp4", name.Text);
                        list.SelectedItem = list.ItemsSource!.Cast<object>().Single(item => item.ToString() == "reference.mp4");
                        Assert.Equal("reference.mp4", name.Text);
                        name.Text = "renamed";
                        Click(save);
                        Assert.Equal(Path.Combine(nested, "renamed.mp4"),
                            Assert.IsType<DesktopDialogStorageFile>(await showing).LocalPath);
                    }
                    finally { dialog.Close(); await showing; }
                    var overwrite = factory.ShowDialogAsync(view, new SaveFileDialogSettings
                    {
                        SuggestedFileName = "old.name.mp4", DefaultExtension = "mp4", Filters = [new("MP4", "mp4")]
                    });
                    var saveDialog = owner.OwnedWindows.Single(window => window.IsVisible);
                    try
                    {
                        Click(saveDialog.GetVisualDescendants().OfType<Button>().Single(button =>
                            Equals(button.Content, languages.GetString("String.WebUi.Save"))));
                        saveDialog.OwnedWindows.Single(window => window.IsVisible).Close(MessageBoxResult.Yes);
                        Assert.Equal(Path.Combine(output, "old.name.mp4"),
                            Assert.IsType<DesktopDialogStorageFile>(await overwrite.WaitAsync(TimeSpan.FromSeconds(3))).LocalPath);
                    }
                    finally { saveDialog.Close(); await overwrite; }
                    return true;
                }
                finally { owner.Close(); }
                static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }, CancellationToken.None);
            Assert.Equal("existing", File.ReadAllText(Path.Combine(output, "old.name.mp4")));
        }
        finally { root.Delete(recursive: true); }
    }

    private sealed class Model : ObservableObject
    {
    }
}
