using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Splat;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Rendering;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Themes;
using TSCutter.GUI.ViewModels.Preview;
using TSCutter.GUI.Views.Preview;
using Xunit;

namespace TSCutter.GUI.Tests.UI;

[Collection("Avalonia UI")]
public sealed class ThumbnailSheetBackgroundTests
{
    [Editing.RemuxExportFact]
    public async Task BackgroundChangesReuseTheSheetAndPersistAcrossThemes()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var locale = new LocalizationService();
            locale.SwitchLanguage("zh-CN");
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
            var app = Avalonia.Application.Current!;
            app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://TSCutterGUI/"))
            {
                Source = new Uri("avares://TSCutterGUI/Themes/StatusColors.axaml")
            });
            var config = new MemoryConfiguration();
            var vm = new ThumbnailSheetWindowViewModel(config)
            {
                FilePath = Path.Combine(FFmpeg.NativeTestEnvironment.FixtureDirectory("remux"), "multi.mp4")
            };
            var window = new ThumbnailSheetWindow { DataContext = vm };
            try
            {
                await vm.InitializeAsync();
                Assert.True(vm.HasPreview, vm.StatusText);
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var options = window.FindControl<WrapPanel>("SheetOptionsRow")!;
                var backgroundOptions = window.FindControl<StackPanel>("BackgroundOptions")!;
                Assert.InRange(Math.Abs(options.Children[0].Bounds.Center.Y - backgroundOptions.Bounds.Center.Y), 0, 1);
                Assert.Equal(SheetBrushes.DefaultBackground, Pixel(vm.PreviewImage!, 0, 0));
                var completion = vm.RebuildCompletion;
                var selector = window.FindControl<ComboBox>("BackgroundSelector")!;
                selector.SelectedIndex = 1;
                Assert.Equal(Colors.White, Pixel(vm.PreviewImage!, 0, 0));
                var picker = window.FindControl<ColorPicker>("BackgroundColorPicker")!;
                // 保存和复制会持有同一把锁；换色期间旧位图必须继续可用，直到导出结束。
                var exportLock = (SemaphoreSlim)typeof(ThumbnailSheetWindowViewModel)
                    .GetField("rebuildLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
                var exportingImage = vm.PreviewImage!;
                await exportLock.WaitAsync();
                try
                {
                    selector.SelectedIndex = 0;
                    picker.Color = Colors.Cyan;
                    Assert.Equal(2, selector.SelectedIndex);
                    Assert.False(vm.SaveCommand.CanExecute(null));
                    await Task.Delay(200);
                    Assert.Same(exportingImage, vm.PreviewImage);
                    Assert.Equal(Colors.White, Pixel(exportingImage, 0, 0));
                    Assert.False(vm.SaveCommand.CanExecute(null));
                }
                finally
                {
                    exportLock.Release();
                }
                await Task.Delay(200);
                Assert.True(vm.SaveCommand.CanExecute(null));
                Assert.Equal(Colors.Cyan, Pixel(vm.PreviewImage!, 0, 0));
                Assert.Same(completion, vm.RebuildCompletion);
                Assert.Empty(vm.Cells);
                window.Width = window.MinWidth;
                foreach (var theme in ThemeModel.AllThemes.Concat(ThemeModel.AllDarkThemes).Select(model => model.Variant))
                {
                    app.RequestedThemeVariant = theme;
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Assert.Equal(2, selector.SelectedIndex);
                    Assert.True(picker.IsVisible);
                    Assert.Equal(Colors.Cyan, Pixel(vm.PreviewImage!, 0, 0));
                    var point = picker.TranslatePoint(new Point(picker.Bounds.Width, 0), window)!.Value;
                    Assert.True(point.X <= window.Bounds.Width);
                    var button = picker.GetVisualDescendants().OfType<Button>()
                        .First(control => control.Flyout is not null);
                    button.Flyout!.ShowAt(button);
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Assert.True(button.Flyout.IsOpen);
                    var presenter = window.GetVisualDescendants().OfType<FlyoutPresenter>().Single();
                    Assert.Equal(CustomTheme.IsDesktopTheme(theme), presenter.Classes.Contains("desktop-theme"));
                    Assert.Equal(CustomTheme.IsWindows11(theme) || CustomTheme.IsMacOS(theme), presenter.Classes.Contains("fluent"));
                    var inputs = presenter.GetVisualDescendants().OfType<NumericUpDown>().ToArray();
                    Assert.Equal(6, inputs.Length);
                    Assert.True(inputs[0].Bounds.Right < inputs[3].Bounds.Left,
                        $"{theme.Key}: HSV and RGB inputs overlap.");
                    var componentGrid = Assert.IsType<Grid>(inputs[0].Parent);
                    Assert.All(componentGrid.Children.OfType<TextBlock>(), label => Assert.True(label.Bounds.Width > 0));
                    foreach (var input in inputs)
                    {
                        var text = input.GetVisualDescendants().OfType<TextPresenter>().Single();
                        var textBox = input.GetVisualDescendants().OfType<TextBox>().Single();
                        using var digits = new TextLayout("359", new Typeface(textBox.FontFamily), textBox.FontSize,
                            textBox.Foreground);
                        Assert.True(text.Bounds.Width >= digits.Width,
                            $"{theme.Key}: {input.Name} has only {text.Bounds.Width}px for three digits ({digits.Width}px).");
                        Assert.True(text.Bounds.Height >= digits.Height,
                            $"{theme.Key}: {input.Name} clips the text height.");
                    }
                    var spectrumToggle = presenter.GetVisualDescendants().OfType<ToggleButton>().Single();
                    Assert.DoesNotContain("desktop-template-button", spectrumToggle.Classes);
                    spectrumToggle.IsChecked = false;
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Assert.False(picker.IsColorSpectrumVisible);
                    var uncheckedBackground = spectrumToggle.Background;
                    var togglePoint = spectrumToggle.TranslatePoint(new Rect(spectrumToggle.Bounds.Size).Center, window)!.Value;
                    window.MouseMove(togglePoint);
                    window.MouseDown(togglePoint, MouseButton.Left);
                    Assert.True(spectrumToggle.IsPressed);
                    if (CustomTheme.IsDesktopTheme(theme))
                        Assert.NotEqual(uncheckedBackground, spectrumToggle.Background);
                    window.MouseUp(togglePoint, MouseButton.Left);
                    window.MouseMove(new Point(2, 2));
                    Assert.True(spectrumToggle.IsChecked);
                    Assert.True(picker.IsColorSpectrumVisible);
                    if (CustomTheme.IsDesktopTheme(theme))
                        Assert.NotEqual(uncheckedBackground, spectrumToggle.Background);
                    button.Flyout.Hide();
                }
            }
            finally
            {
                window.DataContext = null;
                window.Close();
                await vm.OnClosedAsync();
            }
            Assert.Equal(1, config.Saves);
            var restored = new ThumbnailSheetWindowViewModel(config);
            Assert.Equal(2, restored.SelectedBackgroundIndex);
            Assert.Equal(Colors.Cyan, restored.SheetBackgroundColor);
            await restored.OnClosedAsync();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task RecoloringPreservesThumbnailsAndRedrawsMissingCells()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var thumbnail = new RenderTargetBitmap(new PixelSize(64, 36));
            using (var context = thumbnail.CreateDrawingContext(true))
                context.FillRectangle(Brushes.Magenta, new Rect(0, 0, 64, 36));
            using var cell = new ThumbnailSheetCell(0, TimeSpan.Zero)
            {
                State = ThumbnailSheetCellState.Ready, Thumbnail = thumbnail
            };
            using var missing = new ThumbnailSheetCell(1, TimeSpan.FromSeconds(1));
            var layout = ThumbnailSheetComposer.CalculateLayout(2, 1, new PixelSize(64, 36), false, true);
            using var original = ThumbnailSheetComposer.Compose(layout, [cell, missing], [], true, true);
            using var metadata = new ThumbnailSheetCell(0, TimeSpan.Zero) { State = ThumbnailSheetCellState.Ready };
            using var recolored = ThumbnailSheetComposer.Compose(layout, [metadata, missing], [], true, true,
                Colors.White, original);
            Assert.Equal(Colors.White, Pixel(recolored, 0, 0));
            Assert.Equal(Pixel(original, 30, 30), Pixel(recolored, 30, 30));
            Assert.Equal(Colors.Magenta, Pixel(recolored, 30, 30));
            Assert.NotEqual(Pixel(original, 102, 30), Pixel(recolored, 102, 30));
        }, CancellationToken.None);
    }

    private static Color Pixel(Bitmap bitmap, int x, int y)
    {
        var buffer = Marshal.AllocHGlobal(4);
        try
        {
            bitmap.CopyPixels(new PixelRect(x, y, 1, 1), buffer, 4, 4);
            var rgba = bitmap.Format == Avalonia.Platform.PixelFormat.Rgba8888;
            return Color.FromArgb(Marshal.ReadByte(buffer, 3), Marshal.ReadByte(buffer, rgba ? 0 : 2),
                Marshal.ReadByte(buffer, 1), Marshal.ReadByte(buffer, rgba ? 2 : 0));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private sealed class MemoryConfiguration : IConfigurationService
    {
        public AppConfig CurrentConfig { get; } = new()
        {
            ThumbnailSheetPreferences = new() { Columns = 2, Rows = 1, OutputWidth = 1280 }
        };
        public int Saves { get; private set; }
        public void Load() { }
        public void Save() => Saves++;
        public void ApplyTheme(string theme) { }
        public void ApplyLanguage(string language) { }
    }
}
