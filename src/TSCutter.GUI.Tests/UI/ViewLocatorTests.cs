using System.Runtime.CompilerServices;
using Avalonia.Controls;
using TSCutter.GUI.ViewModels;
using Xunit;

namespace TSCutter.GUI.Tests.UI;

public sealed class ViewLocatorTests
{
    [Fact]
    public void WindowViewModelsResolveToConstructibleViewsAfterNamespaceChanges()
    {
        var assembly = typeof(ViewModelBase).Assembly;
        var viewModels = assembly.GetTypes().Where(type => !type.IsAbstract &&
            typeof(ViewModelBase).IsAssignableFrom(type) && type.Name.EndsWith("ViewModel")).ToArray();
        Assert.NotEmpty(viewModels);
        var locator = new NameProbe();
        foreach (var viewModel in viewModels)
        {
            var name = locator.NameFor(viewModel);
            var view = assembly.GetType(name);
            Assert.True(view is not null, $"{viewModel.FullName} -> {name}");
            Assert.True(typeof(Window).IsAssignableFrom(view), name);
            Assert.NotNull(view.GetConstructor(Type.EmptyTypes));
        }
    }

    private sealed class NameProbe : ViewLocator
    {
        // 定位规则只读取对象类型，无需运行各窗口视图模型的构造函数。
        public string NameFor(Type type) => GetViewName(RuntimeHelpers.GetUninitializedObject(type));
    }
}
