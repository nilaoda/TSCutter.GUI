using System.Linq;
using System.Threading.Tasks;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.FrameworkDialogs;
using CommunityToolkit.Mvvm.Input;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.ViewModels.Player;

namespace TSCutter.GUI.ViewModels;

public partial class MainWindowViewModel
{
    public bool CanUsePlaybackBenchmark => CanUseCurrentFile && IsVideoInitialized;
    public bool CanUseFileToolsMenu => CanUseCurrentFileTools || CanUsePlaybackBenchmark;

    [RelayCommand]
    private async Task PlaybackBenchmarkClickAsync(string? filePath)
    {
        var current = filePath is not null;
        if (current && !CanUsePlaybackBenchmark)
            return;
        if (!current)
        {
            var paths = await _dialogService.ShowOpenFilesDialogAsync(this, new OpenFileDialogSettings
            {
                Title = LocalizationManager.Instance.String_PlaybackBenchmark_Title,
                Filters = [new(LocalizationManager.Instance.String_VideoFiles, PreviewFileExtensions.Where(ext => ext != "iso").ToArray()),
                    new(LocalizationManager.Instance.String_AllFiles, "*")]
            });
            filePath = paths.FirstOrDefault()?.LocalPath;
        }
        if (filePath is null)
            return;
        var vm = _dialogService.CreateViewModel<PlaybackBenchmarkWindowViewModel>();
        var video = _videoInstance;
        VideoInstance? suspendedVideo = null;
        vm.BeforeTestAsync = async () =>
        {
            suspendedVideo = _videoInstance;
            StopPlaybackSession();
            BeginKeyFrameStripScrub();
            await playbackStopTask;
            await CompleteScrubPreviewAsync();
            await toneMappingRefreshTask;
            await (decodeIdleCompletion?.Task ?? Task.CompletedTask);
        };
        vm.AfterTest = () =>
        {
            if (ReferenceEquals(suspendedVideo, _videoInstance))
                EndKeyFrameStripScrub(CurrentTime);
        };
        _dialogService.Show(null, vm);
        await vm.InitializeAsync(filePath, current ? _discInput : null, current ? _activePreviewSelection : null,
            current ? CurrentTime : 0, PlaybackTargetSize, preferHardwareDecodingForPreview, EnableSdrToneMapping,
            current ? video?.TimelineOriginSeconds : null);
    }
}
