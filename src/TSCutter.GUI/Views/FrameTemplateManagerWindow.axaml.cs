using System;
using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.Views;

public partial class FrameTemplateManagerWindow : ClassicWindow
{
    private FrameTemplateManagerWindowViewModel? viewModel;

    public FrameTemplateManagerWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (viewModel is not null) viewModel.RequestClose -= OnRequestClose;
            viewModel = DataContext as FrameTemplateManagerWindowViewModel;
            if (viewModel is not null) viewModel.RequestClose += OnRequestClose;
        };
        Loaded += async (_, _) =>
        {
            if (viewModel is not null) await viewModel.InitializeAsync();
        };
        Closed += (_, _) =>
        {
            if (viewModel is null) return;
            viewModel.RequestClose -= OnRequestClose;
            viewModel.OnClosed();
        };
    }

    private void OnRequestClose() => Close();
}
