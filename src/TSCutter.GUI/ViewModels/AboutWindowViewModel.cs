using System.Collections.Generic;
using System.Reflection;
using HanumanInstitute.MvvmDialogs;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.ViewModels;

public record LibraryDesc
{
    public string Url { get; init; }
    public string Name { get; init; }
}

public partial class AboutWindowViewModel : ViewModelBase, IModalDialogViewModel
{
    public string Title => string.Format(LocalizationManager.Instance.String_About_Title, AppName);
    public string ProjectUrl => "https://github.com/nilaoda/TSCutter.GUI";
    public string AppName => "TSCutter.GUI";
    public string AppVersion => $"v{Assembly.GetExecutingAssembly().GetName().Version}";
    public string Copyright => "nilaoda";

    public List<LibraryDesc> AllLibraries =>
    [
        new LibraryDesc { Name = "Avalonia", Url = "https://github.com/AvaloniaUI/Avalonia" },
        new LibraryDesc { Name = "CommunityToolkit.Mvvm", Url = "https://github.com/CommunityToolkit" },
        new LibraryDesc { Name = "Classic.Avalonia", Url = "https://github.com/BAndysc/Classic.Avalonia" },
        new LibraryDesc { Name = "HanumanInstitute.MvvmDialogs", Url = "https://github.com/mysteryx93/HanumanInstitute.MvvmDialogs" },
        new LibraryDesc { Name = "Sdcb.FFmpeg", Url = "https://github.com/sdcb/Sdcb.FFmpeg" },
        new LibraryDesc { Name = "Splat", Url = "https://github.com/reactiveui/splat" },
        new LibraryDesc { Name = "Icons", Url = "https://win98icons.alexmeub.com/" }
    ];
    public bool? DialogResult { get; }
}