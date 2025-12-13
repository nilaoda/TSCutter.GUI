using System;
using HanumanInstitute.MvvmDialogs;
using TSCutter.GUI.Models;

namespace TSCutter.GUI.ViewModels;

public class UpdatesInfoWindowViewModel : ViewModelBase, IModalDialogViewModel
{
    public GitHubVersionInfo GitHubVersionInfo { get; set; }
    public bool? DialogResult { get; }
    public event Action? RequestClose;
}