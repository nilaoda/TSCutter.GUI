using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using TSCutter.GUI.Tests.UI;
using TSCutter.GUI.Utils.Application;
using Xunit;

namespace TSCutter.GUI.Tests.Application;

/// <summary>打开对话框前等待时禁用窗口内容并显示忙碌指针；多处同时等待时，最后一处结束才恢复原状。</summary>
[Collection("Avalonia UI")]
public sealed class BusyCursorTests
{
    [Fact]
    public async Task OverlappingWaitsRestoreTheWindowOnlyAfterTheLastOneEnds()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var content = new Button();
            var original = new Cursor(StandardCursorType.Hand);
            var window = new Window { Content = content, Cursor = original };
            window.Show();
            try
            {
                var first = BusyCursor.Show(window);
                var second = BusyCursor.Show(window);
                Assert.False(content.IsEnabled);
                Assert.NotSame(original, window.Cursor);

                first.Dispose();
                first.Dispose();
                Assert.False(content.IsEnabled);

                second.Dispose();
                Assert.True(content.IsEnabled);
                Assert.Same(original, window.Cursor);

                // 原本就禁用的内容在等待结束后保持禁用。
                content.IsEnabled = false;
                BusyCursor.Show(window).Dispose();
                Assert.False(content.IsEnabled);
            }
            finally
            {
                window.Close();
                original.Dispose();
            }
            return true;
        }, CancellationToken.None);
    }
}
