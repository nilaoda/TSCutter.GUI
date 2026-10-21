using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Classic.Avalonia.Theme;
using TSCutter.GUI.Themes;
using Xunit;

namespace TSCutter.GUI.Tests.UI;

[Collection("Avalonia UI")]
public sealed class TextInputLayoutTests
{
    [Fact]
    public async Task SingleLineInputsKeepTheirSizeAcrossScriptsFontsAndThemes()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var editable = new TextBox();
            var readOnly = new TextBox { IsReadOnly = true };
            var window = new ClassicWindow
            {
                Width = 400, SizeToContent = SizeToContent.Height,
                Content = new StackPanel { Children = { editable, readOnly } }
            };
            try
            {
                window.Show();
                foreach (var theme in new[]
                         {
                             ClassicTheme.Standard, CustomTheme.DarkStandard,
                             CustomTheme.LunaBlue, CustomTheme.LunaDark,
                             CustomTheme.Aero, CustomTheme.AeroDark,
                             CustomTheme.Windows8, CustomTheme.Windows8Dark,
                             CustomTheme.Windows10, CustomTheme.Windows10Dark,
                             CustomTheme.Windows11, CustomTheme.Windows11Dark
                         })
                {
                    Avalonia.Application.Current!.RequestedThemeVariant = theme;
                    foreach (var fontSize in new[] { 12d, 13d, 20d })
                    foreach (var input in new[] { editable, readOnly })
                    {
                        input.FontSize = fontSize;
                        input.Text = "";
                        Flush(window);
                        var emptySize = input.Bounds.Size;
                        var windowSize = window.Bounds.Size;
                        Assert.True(emptySize.Height >= 28);
                        foreach (var text in new[] { "Opening", "中文名称", new string('中', 120), "" })
                        {
                            input.Text = text;
                            Flush(window);
                            Assert.Equal(emptySize, input.Bounds.Size);
                            Assert.Equal(windowSize, window.Bounds.Size);
                        }

                        if (!input.IsReadOnly)
                        {
                            var presenter = input.GetVisualDescendants().OfType<TextPresenter>().Single();
                            presenter.PreeditText = "正在输入";
                            Flush(window);
                            Assert.Equal(emptySize, input.Bounds.Size);
                            Assert.Equal(windowSize, window.Bounds.Size);
                            presenter.PreeditText = null;
                        }
                    }
                }
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task NumericInputsFitTheirFrameAndMultilineInputsRetainTheirLayout()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var numeric = new NumericUpDown { Value = 1, Height = 28 };
            var notes = new TextBox { AcceptsReturn = true, Height = 104 };
            var multiline = new TextBox { AcceptsReturn = true };
            var window = new ClassicWindow
            {
                Width = 400, SizeToContent = SizeToContent.Height,
                Content = new StackPanel { Children = { numeric, notes, multiline } }
            };
            try
            {
                window.Show();
                foreach (var theme in new[] { ClassicTheme.Standard, CustomTheme.Aero, CustomTheme.Windows11 })
                {
                    Avalonia.Application.Current!.RequestedThemeVariant = theme;
                    multiline.Text = "单行";
                    Flush(window);
                    var singleHeight = multiline.Bounds.Height;
                    multiline.Text = "第一行\n第二行\n第三行";
                    notes.Text = multiline.Text;
                    Flush(window);
                    Assert.True(multiline.Bounds.Height > singleHeight);
                    Assert.Equal(104, notes.Bounds.Height);
                    var inner = numeric.GetVisualDescendants().OfType<TextBox>().Single();
                    var position = inner.TranslatePoint(default, numeric)!.Value;
                    Assert.True(position.Y >= 0);
                    Assert.True(position.Y + inner.Bounds.Height <= numeric.Bounds.Height);
                    var presenter = inner.GetVisualDescendants().OfType<TextPresenter>().Single();
                    Assert.True(presenter.Bounds.Height >= inner.LineHeight);
                }
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task FileNameEditorsKeepTheirDataGridRowHeight()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var input = new TextBox { Margin = new Thickness(2) };
            var grid = new DataGrid { AutoGenerateColumns = false, ItemsSource = new[] { new object() } };
            grid.Styles.Add(new StyleInclude(new Uri("avares://TSCutterGUI/"))
            {
                Source = new Uri("avares://Classic.Avalonia.Theme.DataGrid/Classic.axaml")
            });
            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = "File name", Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                CellTemplate = new FuncDataTemplate<object>((_, _) => input)
            });
            var window = new ClassicWindow { Width = 400, Height = 250, Content = grid };
            try
            {
                window.Show();
                foreach (var theme in new[] { ClassicTheme.Standard, CustomTheme.Aero, CustomTheme.Windows11 })
                {
                    Avalonia.Application.Current!.RequestedThemeVariant = theme;
                    input.Text = "Opening.ts";
                    Flush(window);
                    var row = input.GetVisualAncestors().OfType<DataGridRow>().Single();
                    var rowSize = row.Bounds.Size;
                    var inputSize = input.Bounds.Size;
                    input.Text = "中文文件名称.ts";
                    Flush(window);
                    Assert.Equal(inputSize, input.Bounds.Size);
                    Assert.Equal(rowSize, row.Bounds.Size);
                    Assert.True(input.Bounds.Height <= row.Bounds.Height);
                }
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static void Flush(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
    }
}
