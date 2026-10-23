# Source layout

English | [简体中文](README_CN.md)

`TSCutter.GUI` is the desktop application, `TSCutter.GUI.Generators` contains the localization source generator, and `TSCutter.GUI.Tests` contains the tests.

## Application layers

| Directory | Responsibility |
| --- | --- |
| `Models` | Configuration, projects, media state, and data models for the tools |
| `Services` | File scanning, export, repair, and other workflows, plus configuration and localization services |
| `ViewModels` | Window state, commands, and interaction coordination |
| `Views` | Window XAML and event handlers |
| `Utils` | Reusable file, media, timestamp, and reporting helpers |
| `FFmpeg` | Native API declarations and codec wrappers |
| `Rendering` | Video frame presentation and thumbnail sheet composition |
| `Controls`, `Converters` | Shared controls and binding converters |
| `Themes`, `Lang`, `Assets` | Themes, translations, and static resources |

## Feature groups

Larger layer directories are organized by feature:

| Group | Contents |
| --- | --- |
| `Application` | Configuration, languages, themes, About, and updates |
| `Editing` | Main editor, clips, projects, export queue, cutting, and merging |
| `Preview` | Video and subtitle preview, keyframe overview, thumbnail sheets, screenshots, and media information |
| `FrameSearch` | Frame searching, matching, and template management |
| `TransportStream/Analysis` | TS inspection, program tables, packet viewing, and reports |
| `TransportStream/Processing` | Binary TS merging, ES extraction, stream filtering, and editing |
| `TransportStream/Repair` | TS timeline repair, repair from multiple sources, and result inspection |

`Utils/Files` contains file path, natural sorting, and drag-and-drop helpers. `Utils/Media` handles runtime loading and general media reading. `Utils/TransportStream/Packets` contains lower-level TS packet and timestamp helpers. Collections and basic utilities shared across features remain in the `Utils` root.

## Directories and namespaces

C# namespaces follow the directory structure within each project. For example:

```text
Services/Editing/RemuxExportService.cs
  → TSCutter.GUI.Services.Editing
ViewModels/TransportStream/Repair/TsTimelineRepairWindowViewModel.cs
  → TSCutter.GUI.ViewModels.TransportStream.Repair
Views/TransportStream/Repair/TsTimelineRepairWindow.axaml.cs
  → TSCutter.GUI.Views.TransportStream.Repair
```

A window's View and ViewModel use the same feature directory. `ViewLocatorBase` locates windows by replacing `.ViewModels.` with `.Views.` in the namespace. XAML `x:Class` declarations, type namespaces, and binding types follow the corresponding C# declarations.

`LocalizationManager` lives in `Utils/Application`. The localization generator outputs to `TSCutter.GUI.Utils.Application`, matching the handwritten partial class. Native APIs remain centralized in `FFmpeg`; new features reuse that layer.

## Tests

Tests are grouped by feature. `FFmpeg` contains native interop tests, while `UI` contains theme, control layout, and window placement tests.

```sh
dotnet test src/TSCutter.GUI.Tests -c Debug
```

Ordinary logic and headless UI tests run by default. Run `python tools/prepare-test-media.py` to prepare synthetic media for keyframe, remux export, and subtitle tests without sample environment variables. Generated videos stay in a Git-ignored directory. See the [test setup guide](TSCutter.GUI.Tests/README.md) for Windows, macOS, and Linux instructions, runtime lookup, and optional real media. Integration tests report an explicit skip reason when a prerequisite is missing.
