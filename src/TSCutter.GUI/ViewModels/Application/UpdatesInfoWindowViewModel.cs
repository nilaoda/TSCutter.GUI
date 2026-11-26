using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.ViewModels.Application;

public partial class UpdatesInfoWindowViewModel : ViewModelBase, IModalDialogViewModel
{
    public GitHubVersionInfo GitHubVersionInfo { get; set; } = null!;
    public bool? DialogResult { get; }
    public event Action? RequestClose;

    public UpdateInfo? Update { get; private set; }

    [ObservableProperty] private string _versionText = "";
    [ObservableProperty] private string _notesText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canInstall;

    /// <summary>Fills the dialog. A null argument means a manual check found nothing.</summary>
    public void Prepare(UpdateInfo? info)
    {
        Update = info;
        var strings = LocalizationManager.Instance;
        if (info is null)
        {
            VersionText = strings.String_Update_UpToDate;
            NotesText = "";
            StatusText = "";
            Progress = 0;
            IsBusy = false;
            CanInstall = false;
            return;
        }

        VersionText = string.Format(strings.String_Update_VersionInfo, App.CurrentTag, info.Tag);
        NotesText = string.IsNullOrWhiteSpace(info.Notes) ? strings.String_Update_NoNotes : info.Notes;
        StatusText = "";
        Progress = 0;
        IsBusy = false;
        CanInstall = info.CanInstallInPlace;
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (Update is null || IsBusy || App.ConfigService?.CurrentConfig.EnableInPlaceUpdates == false)
            return;
        var strings = LocalizationManager.Instance;
        IsBusy = true;
        CanInstall = false;
        try
        {
            StatusText = strings.String_Update_Downloading;
            Progress = 0;
            var service = new UpdateService();
            var staged = await service.PrepareAsync(Update, new Progress<double>(value => Progress = value));
            UpdateService.EnsureInstallLocationIsWritable(staged.InstallRoot);
            StatusText = strings.String_Update_Installing;
            await service.ApplyAsync(staged, CurrentArguments());

            var config = App.ConfigService;
            if (config is not null && !string.IsNullOrEmpty(config.CurrentConfig.SkippedUpdateVersion))
            {
                config.CurrentConfig.SkippedUpdateVersion = "";
                config.Save();
            }
            RequestClose?.Invoke();
            App.ShutdownForUpdate();
        }
        catch (Exception ex)
        {
            StatusText = string.Format(strings.String_Update_Error, ex.Message);
            CanInstall = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenInBrowser()
    {
        // Keeps the original behavior: Help → Check update opens the releases page.
        App.MainWindow.CheckUpdateCommand.Execute(null);
    }

    [RelayCommand]
    private void SkipThisVersion()
    {
        var config = App.ConfigService;
        if (config is not null && Update is not null)
        {
            config.CurrentConfig.SkippedUpdateVersion = Update.Tag;
            config.Save();
        }
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Later() => RequestClose?.Invoke();

    private static IReadOnlyList<string> CurrentArguments() => Environment.GetCommandLineArgs()
        .Skip(1)
        .Where(argument => argument != "--apply-update")
        .ToArray();
}
