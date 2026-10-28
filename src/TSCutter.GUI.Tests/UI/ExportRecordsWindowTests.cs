using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Classic.Avalonia.Theme;
using Splat;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Services.TransportStream.Analysis;
using TSCutter.GUI.Themes;
using TSCutter.GUI.ViewModels.Editing;
using TSCutter.GUI.Views.Editing;
using Xunit;

namespace TSCutter.GUI.Tests.UI;

[Collection("Avalonia UI")]
public sealed class ExportRecordsWindowTests
{
    [Fact]
    public async Task ExportRecordsKeepTheirSummaryAndWindowLifecycleAcrossThemes()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var locale = new LocalizationService();
            locale.SwitchLanguage("zh-CN");
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
            var registry = new ExportRecordRegistry();
            Locator.CurrentMutable.RegisterConstant(registry);
            var directory = Path.Combine(Path.GetTempPath(), "tscutter-export-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var source = Path.Combine(directory, "source.ts");
            var destination = Path.Combine(directory, "demo-output.ts");
            try
            {
                var bytes = new byte[188 * 20];
                Array.Fill(bytes, (byte)0xFF);
                for (var offset = 0; offset < bytes.Length; offset += 188)
                {
                    bytes[offset] = 0x47;
                    bytes[offset + 1] = 0x01;
                    bytes[offset + 2] = 0x01;
                    bytes[offset + 3] = (byte)(0x10 | (offset / 188 & 15));
                }
                await File.WriteAllBytesAsync(source, bytes);
                var output = new OutputWindowViewModel
                {
                    SourceFilePath = source, OutputPath = destination,
                    EndPosition = -1, EndSeconds = 1
                };
                output.SetProgramTableChoice([], TsProgramTableExportChoice.Original);
                await output.OutputCommand.ExecuteAsync(null);
                Assert.Null(output.Exception);
                Assert.True(output.DialogResult);
                var record = Assert.Single(registry.Records);
                Assert.Equal(TsExportCheckState.Completed, record.CheckState);
                Assert.Equal(bytes.Length, record.FileSize);
                Assert.NotNull(record.RetainedResult);
                // 原始流截取复用导出窗口，但不检测、不登记导出记录。
                var raw = new OutputWindowViewModel
                {
                    IsRawByteRange = true, SourceFilePath = source,
                    OutputPath = Path.Combine(directory, "raw.ts"), EndPosition = -1
                };
                await raw.OutputCommand.ExecuteAsync(null);
                Assert.Null(raw.Exception);
                Assert.True(raw.DialogResult);
                Assert.Null(Assert.Single(raw.Outcomes).RecordId);
                Assert.Equal(bytes, await File.ReadAllBytesAsync(raw.OutputPath));
                Assert.Single(registry.Records);
                // 写入失败不能误登记成功记录；先前的记录仍可查看。
                output = new OutputWindowViewModel
                {
                    SourceFilePath = source, OutputPath = directory, EndPosition = -1
                };
                output.SetProgramTableChoice([], TsProgramTableExportChoice.Original);
                await output.OutputCommand.ExecuteAsync(null);
                Assert.NotNull(output.Exception);
                Assert.False(output.DialogResult);
                Assert.Single(registry.Records);

                var vm = new ExportRecordsWindowViewModel(null!);
                var window = new ExportRecordsWindow { DataContext = vm };
                window.Show();
                Assert.Contains("demo-output.ts", vm.SummaryText);
                Assert.Contains(record.OutputPath, vm.SummaryText);
                foreach (var theme in new[]
                         {
                             ClassicTheme.Standard, CustomTheme.DarkStandard,
                             CustomTheme.Windows10, CustomTheme.Windows10Dark,
                             CustomTheme.Windows11, CustomTheme.Windows11Dark,
                             CustomTheme.MacOS, CustomTheme.MacOSDark
                         })
                {
                    Avalonia.Application.Current!.RequestedThemeVariant = theme;
                    foreach (var width in new[] { 900, 620 })
                    {
                        window.Width = width;
                        Dispatcher.UIThread.RunJobs();
                        window.UpdateLayout();
                        var summary = window.FindControl<TextBox>("Summary")!;
                        Assert.True(summary.Bounds.Width > 500);
                        Assert.True(summary.Bounds.Height > 100);
                        Assert.Contains("demo-output.ts", summary.Text);
                        if (Environment.GetEnvironmentVariable("TSCUTTER_EXPORT_RECORDS_CAPTURE") is { Length: > 0 } capture)
                        {
                            Directory.CreateDirectory(capture);
                            using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width * 2,
                                (int)window.Bounds.Height * 2), new Vector(192, 192));
                            bitmap.Render(window);
                            bitmap.Save(Path.Combine(capture, theme.Key + "-" + width + ".png"));
                        }
                    }
                }
                vm.CloseCommand.Execute(null);
                Assert.False(window.IsVisible);
                // 同一个 VM 再次打开后，新增记录仍能更新，关闭事件也不会残留。
                window = new ExportRecordsWindow { DataContext = vm };
                window.Show();
                registry.Register(new ExportRecord { OutputPath = "second.mp4", Kind = ExportKind.Mp4Remux });
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(2, vm.Records.Count);
                Assert.Equal(record.Id, vm.SelectedRecord!.Record.Id);
                File.Delete(destination);
                vm.ViewDetailsCommand.Execute(null);
                Assert.False(vm.CanViewDetails);
                Assert.NotEmpty(vm.StatusText);
                vm.CloseCommand.Execute(null);
                Assert.False(window.IsVisible);
                return true;
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }, CancellationToken.None);
    }
}
