using System;
using System.Collections.Generic;
using System.Reflection;
using HanumanInstitute.MvvmDialogs;

namespace TSCutter.GUI.ViewModels;

public record LibraryDesc
{
    public string Url { get; init; }
    public string Name { get; init; }
}

public partial class AboutWindowViewModel : ViewModelBase, IModalDialogViewModel, ICloseable
{
    public string Title => "About";
    public string ProjectUrl => "https://github.com/nilaoda/TSCutter.GUI";
    public string ShortDesc => $"TSCutter.GUI v{Assembly.GetExecutingAssembly().GetName().Version}";
    public string LibrariesDesc => "This software use:";

    public List<LibraryDesc> AllLibraries =>
    [
        new LibraryDesc { Name = "Avalonia", Url = "https://github.com/AvaloniaUI/Avalonia" },
        new LibraryDesc { Name = "CommunityToolkit.Mvvm", Url = "https://github.com/CommunityToolkit" },
        new LibraryDesc { Name = "FluentAvalonia", Url = "https://github.com/amwx/FluentAvalonia" },
        new LibraryDesc { Name = "HanumanInstitute.MvvmDialogs", Url = "https://github.com/mysteryx93/HanumanInstitute.MvvmDialogs" },
        new LibraryDesc { Name = "Sdcb.FFmpeg", Url = "https://github.com/sdcb/Sdcb.FFmpeg" },
        new LibraryDesc { Name = "Splat", Url = "https://github.com/reactiveui/splat" }
    ];
    public bool? DialogResult { get; }
    public event EventHandler? RequestClose;
}