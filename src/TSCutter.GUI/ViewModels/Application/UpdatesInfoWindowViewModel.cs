using System;
using HanumanInstitute.MvvmDialogs;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.ViewModels.Application;

public class UpdatesInfoWindowViewModel : ViewModelBase, IModalDialogViewModel
{
    public GitHubVersionInfo GitHubVersionInfo { get; set; }
    public bool? DialogResult { get; }
    public event Action? RequestClose;
}