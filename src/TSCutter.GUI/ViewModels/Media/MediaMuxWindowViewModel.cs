using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFmpeg.AutoGen.Abstractions;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.FrameworkDialogs;
using TSCutter.GUI.Models.Media;
using TSCutter.GUI.Services.Media;
using TSCutter.GUI.Utils;
using TSCutter.GUI.Utils.Files;
using TSCutter.GUI.Utils.Application;

namespace TSCutter.GUI.ViewModels.Media;

public sealed record MediaMuxLanguageChoice(string Code, string Label);

public partial class MediaMuxTrackItem : ObservableObject
{
    private static LocalizationManager Strings => LocalizationManager.Instance;
    public MediaMuxSource Source { get; }
    public MediaMuxStream Stream { get; }
    public string SourceName => Path.GetFileName(Source.Path);
    public string Description => $"#{Stream.Index} · {Stream.Codec.ToUpperInvariant()} {Stream.Parameters}".Trim();
    public string Kind => Stream.IsCover ? Strings.String_MediaMux_Cover : Stream.Type switch
    {
        AVMediaType.AVMEDIA_TYPE_VIDEO => Strings.String_MediaMux_Video,
        AVMediaType.AVMEDIA_TYPE_AUDIO => Strings.String_MediaMux_Audio,
        AVMediaType.AVMEDIA_TYPE_SUBTITLE => Strings.String_MediaMux_Subtitle,
        _ => Strings.String_MediaMux_Attachment
    };
    public bool CanShift => Stream.IsTimed;
    public bool IsSubtitle => Stream.Type == AVMediaType.AVMEDIA_TYPE_SUBTITLE;
    public string OffsetText => OffsetMilliseconds == 0 ? "—" : $"{OffsetMilliseconds:+0.###;-0.###;0} ms";
    private AVCodecID? _subtitleTarget;
    private string _container = "";
    public bool ConvertsSubtitle => IsCompatible && _subtitleTarget is not null;
    public string Status => !IsCompatible ? (_subtitleTarget is null ? Strings.String_MediaMux_Incompatible : Strings.String_MediaMux_EncoderMissing) :
        ConvertsSubtitle ? string.Format(Strings.String_MediaMux_ConvertTo,
            _subtitleTarget == AVCodecID.AV_CODEC_ID_MOV_TEXT ? "mov_text" : "SRT") : "";
    public string StatusHint => !IsCompatible && _subtitleTarget is not null
        ? Strings.String_MediaMux_EncoderMissingHint : !IsCompatible
            ? string.Format(Strings.String_MediaMux_UnsupportedCodec, _container.ToUpperInvariant(), Stream.Codec.ToUpperInvariant(), Stream.Index)
            : ConvertsSubtitle ? Strings.String_MediaMux_SubtitleConversionHint : Status;
    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private int _order;
    [ObservableProperty] private string _language;
    [ObservableProperty] private string _title;
    [ObservableProperty] private bool _isDefault;
    [ObservableProperty] private bool _isForced;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(OffsetText))] private decimal _offsetMilliseconds;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Status))] private bool _isCompatible;

    public MediaMuxTrackItem(MediaMuxSource source, MediaMuxStream stream)
    {
        Source = source;
        Stream = stream;
        _language = stream.Language.Trim().ToLowerInvariant();
        if (_language.Length == 2)
        {
            try { _language = CultureInfo.GetCultureInfo(_language).ThreeLetterISOLanguageName; }
            catch (CultureNotFoundException) { }
        }
        _title = stream.Title;
        _isDefault = (stream.Disposition & 1) != 0;
        _isForced = (stream.Disposition & 64) != 0;
    }

    public MediaMuxTrack Snapshot() => new(Source, Stream, Language.Trim().ToLowerInvariant(), Title.Trim(),
        OffsetMilliseconds, IsDefault, IsForced);

    public void Refresh(string container)
    {
        _container = container;
        _subtitleTarget = Stream.SubtitleConversions.TryGetValue(container, out var target) ? target : null;
        IsCompatible = Stream.Containers.Contains(container);
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusHint));
        OnPropertyChanged(nameof(ConvertsSubtitle));
    }
}

public partial class MediaMuxWindowViewModel : ViewModelBase, IModalDialogViewModel
{
    private static LocalizationManager Strings => LocalizationManager.Instance;
    private readonly IDialogService _dialogs;
    private readonly MediaMuxService _service = new();
    private CancellationTokenSource? _cancellation;
    private bool _closed;
    private bool _settingDefaults;
    private string? _defaultOutputPath;
    private Func<string>? _statusTextProvider = static () => Strings.String_MediaMux_Ready;
    private object[] _statusArguments = [];
    public MediaMuxWindowViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
        _statusText = Strings.String_MediaMux_Ready;
        App.LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    public string WindowTitle => Strings.String_MediaMux_Title;
    public bool? DialogResult => null;
    public Func<string, Task<bool>>? ConfirmOverwriteAsync { get; set; }
    public ObservableCollection<MediaMuxSource> Sources { get; } = [];
    public ObservableCollection<MediaMuxTrackItem> Tracks { get; } = [];
    public IReadOnlyList<string> Containers => MediaMuxService.Containers;
    public IReadOnlyList<MediaMuxLanguageChoice> Languages => new[]
    {
        ("", Strings.String_TsRemux_Language_None), ("und", Strings.String_TsRemux_Language_Undetermined),
        ("chi", Strings.String_TsRemux_Language_Chinese), ("eng", Strings.String_TsRemux_Language_English),
        ("jpn", Strings.String_TsRemux_Language_Japanese), ("kor", Strings.String_TsRemux_Language_Korean),
        ("fre", Strings.String_TsRemux_Language_French), ("ger", Strings.String_TsRemux_Language_German),
        ("spa", Strings.String_TsRemux_Language_Spanish), ("ita", Strings.String_TsRemux_Language_Italian)
    }.Select(item => new MediaMuxLanguageChoice(item.Item1,
        item.Item2 +
        (item.Item1.Length == 0 ? "" : " (" + item.Item1 + ")"))).ToArray();
    public bool IsEmpty => Sources.Count == 0;
    public bool CanEdit => !IsBusy && !_closed;
    public bool CanRemoveSource => CanEdit && SelectedSource is not null;
    public bool HasTrack => SelectedTrack is not null;
    public bool CanMoveUp => CanEdit && SelectedTrack is not null && Tracks.IndexOf(SelectedTrack) > 0;
    public bool CanMoveDown => CanEdit && SelectedTrack is not null && Tracks.IndexOf(SelectedTrack) < Tracks.Count - 1;
    public bool CanExport => CanEdit && ValidationText.Length == 0;
    public bool CanRevealOutput => !IsBusy && LastOutputPath is not null && File.Exists(LastOutputPath);
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanRevealOutput))]
    [NotifyCanExecuteChangedFor(nameof(RevealOutputCommand))]
    private string? _lastOutputPath;
    public string Summary => string.Format(Strings.String_MediaMux_Summary, Sources.Count, Tracks.Count(track => track.IsSelected));
    public string ContainerHint => Container is "ts" or "m2ts" ? Strings.String_MediaMux_TsHint : Strings.String_MediaMux_CopyHint;
    public string SubtitleConversionHint => Tracks.Any(track => track.IsSelected && track.ConvertsSubtitle)
        ? Strings.String_MediaMux_SubtitleConversionHint : "";
    public string ValidationText
    {
        get
        {
            var selected = Tracks.Where(track => track.IsSelected).ToArray();
            if (selected.Length == 0)
                return Strings.String_MediaMux_ChooseTracks;
            if (selected.FirstOrDefault(track => !track.IsCompatible) is { } incompatible)
                return incompatible.StatusHint;
            if (selected.Any(track => track.Language.Trim().Length != 0 &&
                (track.Language.Trim().Length != 3 || track.Language.Trim().Any(c => !char.IsAsciiLetter(c)))))
                return Strings.String_MediaMux_InvalidLanguage;
            if (selected.All(track => !track.CanShift))
                return Strings.String_MediaMux_Error_NoPackets;
            if (string.IsNullOrWhiteSpace(OutputPath))
                return Strings.String_MediaMux_ChooseOutput;
            try
            {
                if (!Path.GetExtension(OutputPath).Equals("." + Container, StringComparison.OrdinalIgnoreCase))
                    return Strings.String_MediaMux_ExtensionMismatch;
                if (Sources.Any(source => MediaFileUtil.PathsEqual(OutputPath, source.Path)))
                    return Strings.String_MediaMux_Error_SameFile;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
            {
                return Strings.String_MediaMux_ChooseOutput;
            }
            return "";
        }
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanRemoveSource))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSourceCommand))]
    private MediaMuxSource? _selectedSource;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasTrack), nameof(CanMoveUp), nameof(CanMoveDown))]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand), nameof(MoveDownCommand))]
    private MediaMuxTrackItem? _selectedTrack;
    [ObservableProperty] private string _container = "mp4";
    [ObservableProperty] private string _outputPath = "";
    [ObservableProperty] private bool _shortest;
    [ObservableProperty] private bool _shortestIncludesSubtitles;
    [ObservableProperty] private bool _fastStart;
    public bool CanUseFastStart => Container == "mp4";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private bool _isFailure;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasErrorDetails))] private string _errorDetails = "";
    public bool HasErrorDetails => ErrorDetails.Length > 0;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _statusText;
    [ObservableProperty] private string _progressText = "";

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanRevealOutput));
        RevealOutputCommand.NotifyCanExecuteChanged();
        RefreshCommands();
        AddFilesCommand.NotifyCanExecuteChanged();
        BrowseOutputCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
        SelectNoneCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    partial void OnOutputPathChanged(string value) => RefreshCommands();
    partial void OnContainerChanged(string value)
    {
        if (_defaultOutputPath is not null)
            _defaultOutputPath = Path.ChangeExtension(_defaultOutputPath, value);
        if (OutputPath.Length > 0)
            OutputPath = Path.ChangeExtension(OutputPath, value);
        foreach (var track in Tracks)
            track.Refresh(value);
        OnPropertyChanged(nameof(ContainerHint));
        OnPropertyChanged(nameof(CanUseFastStart));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(SubtitleConversionHint));
        OnPropertyChanged(nameof(ValidationText));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanRemoveSource));
        OnPropertyChanged(nameof(CanMoveUp));
        OnPropertyChanged(nameof(CanMoveDown));
        ExportCommand.NotifyCanExecuteChanged();
        RemoveSourceCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task AddFilesAsync()
    {
        var files = await _dialogs.ShowOpenFilesDialogAsync(this, new OpenFileDialogSettings
        {
            Title = Strings.String_MediaMux_AddFiles, AllowMultiple = true,
            Filters = [new(Strings.String_MediaMux_MediaFiles, ["mkv", "mp4", "mov", "m4v", "ts", "m2ts", "mts", "aac", "ac3", "eac3", "dts", "flac", "wav", "mp3", "m4a", "srt", "ass", "ssa", "sup", "mks"]),
                new(Strings.String_MediaMux_AllFiles, "*")]
        });
        await AddPathsAsync(files.Select(file => file.LocalPath));
    }

    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        if (!CanEdit)
            return;
        var cancellation = BeginOperation(false);
        var errors = new List<string>();
        try
        {
            foreach (var path in paths)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (Sources.Count >= 32 || Tracks.Count >= 256)
                {
                    errors.Add(Strings.String_MediaMux_Limit);
                    break;
                }
                try
                {
                    var fullPath = Path.GetFullPath(path);
                    if (Sources.Any(source => MediaFileUtil.PathsEqual(source.Path, fullPath)))
                        continue;
                    SetStatus(static () => Strings.String_MediaMux_Reading, Path.GetFileName(fullPath));
                    var source = await _service.ProbeAsync(fullPath, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (source.Streams.Count == 0 || source.Streams.Count + Tracks.Count > 256)
                    {
                        errors.Add($"{Path.GetFileName(path)}: {Strings.String_MediaMux_NoTracks}");
                        continue;
                    }
                    Sources.Add(source);
                    foreach (var stream in source.Streams)
                    {
                        var row = new MediaMuxTrackItem(source, stream);
                        if (Tracks.Any(item => item.Stream.Type == stream.Type && item.IsDefault))
                            row.IsDefault = false;
                        row.Refresh(Container);
                        Tracks.Add(row);
                        row.PropertyChanged += TrackChanged;
                    }
                    SelectedSource = source;
                    SelectedTrack ??= Tracks.FirstOrDefault();
                    RefreshCommands();
                    if (string.IsNullOrWhiteSpace(OutputPath))
                    {
                        _defaultOutputPath = Path.Combine(Path.GetDirectoryName(fullPath)!, Path.GetFileNameWithoutExtension(fullPath) + "_mux." + Container);
                        OutputPath = _defaultOutputPath;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    errors.Add($"{Path.GetFileName(path)}: {FormatError(exception)}");
                }
            }
            IsFailure = errors.Count > 0;
            if (IsFailure)
            {
                _statusTextProvider = null;
                StatusText = string.Join(Environment.NewLine, errors);
                ErrorDetails = StatusText;
            }
            else
                SetStatus(static () => Strings.String_MediaMux_Ready);
        }
        catch (OperationCanceledException) { SetStatus(static () => Strings.String_MediaMux_Cancelled); }
        finally
        {
            UpdateOrder();
            EndOperation(cancellation);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveSource))]
    private void RemoveSource()
    {
        foreach (var row in Tracks.Where(item => item.Source == SelectedSource).ToArray())
        {
            row.PropertyChanged -= TrackChanged;
            Tracks.Remove(row);
        }
        Sources.Remove(SelectedSource!);
        if (Sources.Count == 0)
        {
            // 默认名称跟随本轮输入；用户手动指定的输出路径继续保留。
            if (OutputPath == _defaultOutputPath)
                OutputPath = "";
            _defaultOutputPath = null;
        }
        SelectedSource = Sources.FirstOrDefault();
        if (SelectedTrack is not null && !Tracks.Contains(SelectedTrack))
            SelectedTrack = Tracks.FirstOrDefault();
        UpdateOrder();
    }

    public void MoveTrack(MediaMuxTrackItem source, MediaMuxTrackItem target)
    {
        if (!CanEdit || source == target || !Tracks.Contains(source) || !Tracks.Contains(target))
            return;
        Tracks.Move(Tracks.IndexOf(source), Tracks.IndexOf(target));
        SelectedTrack = source;
        UpdateOrder();
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => MoveTrack(SelectedTrack!, Tracks[Tracks.IndexOf(SelectedTrack!) - 1]);
    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => MoveTrack(SelectedTrack!, Tracks[Tracks.IndexOf(SelectedTrack!) + 1]);
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void SelectAll()
    {
        foreach (var row in Tracks)
            row.IsSelected = true;
    }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void SelectNone()
    {
        foreach (var row in Tracks)
            row.IsSelected = false;
    }

    private void UpdateOrder()
    {
        for (var index = 0; index < Tracks.Count; index++)
            Tracks[index].Order = index + 1;
        // DataGrid 移动或删除条目时可能清空选择，重新推送当前条目以保留属性编辑上下文。
        OnPropertyChanged(nameof(SelectedTrack));
        RefreshCommands();
    }

    private void TrackChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MediaMuxTrackItem.IsDefault) && sender is MediaMuxTrackItem { IsDefault: true } row && !_settingDefaults)
        {
            _settingDefaults = true;
            try
            {
                foreach (var other in Tracks.Where(item => item != row && item.Stream.Type == row.Stream.Type))
                    other.IsDefault = false;
            }
            finally { _settingDefaults = false; }
        }
        // 其余编辑只更新行自身；格式和语言切换在批量刷新结束后统一校验。
        if (args.PropertyName is nameof(MediaMuxTrackItem.IsSelected) or nameof(MediaMuxTrackItem.Language))
            RefreshCommands();
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task BrowseOutputAsync()
    {
        var file = await _dialogs.ShowSaveFileDialogAsync(this, new SaveFileDialogSettings
        {
            Title = Strings.String_MediaMux_ChooseOutput, SuggestedFileName = Path.GetFileName(OutputPath), DefaultExtension = Container,
            Filters = [new(Container.ToUpperInvariant(), [Container])]
        });
        if (file?.Path is not null)
            OutputPath = file.Path.LocalPath;
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        if (File.Exists(OutputPath) && (ConfirmOverwriteAsync is null || !await ConfirmOverwriteAsync(OutputPath)))
            return;
        if (!CanExport)
            return;
        var request = new MediaMuxRequest(OutputPath, Container, Tracks.Where(item => item.IsSelected).Select(item => item.Snapshot()).ToArray(),
            Shortest, ShortestIncludesSubtitles, FastStart);
        var cancellation = BeginOperation(true);
        SetStatus(static () => Strings.String_MediaMux_Running);
        try
        {
            var progress = new Progress<MediaMuxProgress>(value =>
            {
                if (_closed || !ReferenceEquals(_cancellation, cancellation))
                    return;
                Percent = Math.Max(Percent, value.Percent);
                ProgressText = $"{Percent:0.0}% · {CommonUtil.FormatFileSize(value.Bytes)} · {CommonUtil.FormatFileSize(value.BytesPerSecond)}/s";
            });
            await _service.ExportAsync(request, progress, cancellation.Token);
            Percent = 100;
            LastOutputPath = request.OutputPath;
            ProgressText = CommonUtil.FormatFileSize(new FileInfo(OutputPath).Length);
            SetStatus(static () => Strings.String_MediaMux_Completed);
        }
        catch (OperationCanceledException) { SetStatus(static () => Strings.String_MediaMux_Cancelled); }
        catch (Exception exception)
        {
            IsFailure = true;
            _statusTextProvider = null;
            StatusText = FormatError(exception);
            ErrorDetails = exception is MediaMuxFailureException failure
                ? string.Join(Environment.NewLine + Environment.NewLine,
                    new[] { StatusText, failure.Message, failure.Diagnostic }.Where(text => text.Length > 0))
                : StatusText;
        }
        finally { EndOperation(cancellation); }
    }

    private CancellationTokenSource BeginOperation(bool exporting)
    {
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IsFailure = false;
        ErrorDetails = "";
        IsExporting = exporting;
        Percent = 0;
        ProgressText = "";
        IsBusy = true;
        return cancellation;
    }

    private void EndOperation(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(_cancellation, cancellation))
            _cancellation = null;
        cancellation.Dispose();
        IsBusy = false;
        IsExporting = false;
    }

    [RelayCommand(CanExecute = nameof(CanRevealOutput))]
    private void RevealOutput() => CommonUtil.OpenFileLocation(LastOutputPath!);

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _cancellation?.Cancel();

    private static string FormatError(Exception exception) => exception switch
    {
        MediaMuxFailureException failure => string.Format(Strings.String_MediaMux_Error_FFmpegFailure,
            failure.Container.ToUpperInvariant(), (failure.Operation switch
            {
                MediaMuxOperation.OpenInput => Strings.String_MediaMux_Operation_OpenInput,
                MediaMuxOperation.PrepareTracks => Strings.String_MediaMux_Operation_PrepareTracks,
                MediaMuxOperation.CreateOutput => Strings.String_MediaMux_Operation_CreateOutput,
                MediaMuxOperation.WriteHeader => Strings.String_MediaMux_Operation_WriteHeader,
                MediaMuxOperation.WritePackets => Strings.String_MediaMux_Operation_WritePackets,
                MediaMuxOperation.ConvertSubtitle => Strings.String_MediaMux_Operation_ConvertSubtitle,
                MediaMuxOperation.FinalizeOutput => Strings.String_MediaMux_Operation_FinalizeOutput,
                _ => throw new ArgumentOutOfRangeException(nameof(failure.Operation))
            })) +
            (failure.Track.Length > 0 ? " · " + failure.Track : ""),
        MediaMuxException mux => (mux.Code switch
        {
            MediaMuxError.InvalidRequest => Strings.String_MediaMux_Error_InvalidRequest,
            MediaMuxError.SameFile => Strings.String_MediaMux_Error_SameFile,
            MediaMuxError.SourceChanged => Strings.String_MediaMux_Error_SourceChanged,
            MediaMuxError.UnsupportedTrack => Strings.String_MediaMux_Error_UnsupportedTrack,
            MediaMuxError.MissingTimestamp => Strings.String_MediaMux_Error_MissingTimestamp,
            MediaMuxError.NonMonotonicTimestamp => Strings.String_MediaMux_Error_NonMonotonicTimestamp,
            MediaMuxError.NoPackets => Strings.String_MediaMux_Error_NoPackets,
            _ => throw new ArgumentOutOfRangeException(nameof(mux.Code))
        }) + (mux.Message.Length > 0 ? " · " + mux.Message : ""),
        _ => exception.Message
    };

    private void SetStatus(Func<string> textProvider, params object[] arguments)
    {
        _statusTextProvider = textProvider;
        _statusArguments = arguments;
        StatusText = string.Format(textProvider(), arguments);
    }

    private void OnLanguageChanged()
    {
        if (_statusTextProvider is not null)
            SetStatus(_statusTextProvider, _statusArguments);
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(Languages));
        OnPropertyChanged(nameof(ContainerHint));
        foreach (var track in Tracks)
            track.Refresh(Container);
        RefreshCommands();
    }

    public void OnClosed()
    {
        if (_closed)
            return;
        _closed = true;
        _cancellation?.Cancel();
        App.LocalizationService.LanguageChanged -= OnLanguageChanged;
        foreach (var row in Tracks)
            row.PropertyChanged -= TrackChanged;
        ConfirmOverwriteAsync = null;
    }
}
