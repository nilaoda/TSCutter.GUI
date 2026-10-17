using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;

namespace TSCutter.GUI.Themes;

/// <summary>自定义标题模板继续遵循 Classic 对话框和不可调整大小窗口的按钮规则。</summary>
public sealed class DesktopCaptionButtons : CaptionButtons
{
    private IDisposable? _canResizeSubscription;

    protected override Type StyleKeyOverride => typeof(CaptionButtons);

    public override void Attach(Window hostWindow)
    {
        _canResizeSubscription?.Dispose();
        base.Attach(hostWindow);
        PseudoClasses.Set(":dialog", hostWindow.IsDialog);
        _canResizeSubscription = hostWindow.GetObservable(Window.CanResizeProperty).Subscribe(canResize =>
            PseudoClasses.Set(":cantresize", !canResize));
    }

    public override void Detach()
    {
        _canResizeSubscription?.Dispose();
        _canResizeSubscription = null;
        base.Detach();
    }
}
