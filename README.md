<div align="center">
  <img src="src/TSCutter.GUI/Assets/logo.png" alt="TSCutter.GUI" width="112">
  <h1>TSCutter.GUI</h1>
  <p><strong>English</strong> | <a href="README_CN.md">中文版</a></p>
</div>

TSCutter.GUI is a cross-platform video editing and MPEG-TS diagnostic toolkit. It supports keyframe-based TS/M2TS cutting and MP4/MKV remux exports, along with stream inspection, extraction, filtering, structural editing, repair, merging, and packet analysis, without re-encoding the original audio or video.

> TSCutter.GUI is in public **Beta**. Download the [latest release](https://github.com/nilaoda/TSCutter.GUI/releases/latest) and report problems through [GitHub Issues](https://github.com/nilaoda/TSCutter.GUI/issues). The software is still under development and may contain bugs.

## Features

### Editing workflow

- **Keyframe-accurate cutting**: Preview and navigate nearby keyframes or jump to a specific time before marking clip boundaries.
- **Keyframe overview and frame search**: Browse keyframe thumbnails, manage saved frame templates, and search the video for similar images.
- **Zoomable timeline**: Continuously zoom, pan, or return to a full-file overview for accurate navigation in long recordings.
- **Multiple clip management**: Create and edit several clip ranges, view their positions on the timeline, and compare duration and estimated size.
- **Clip labels and output names**: Give clips names, notes, and colors; retain them in projects and queued exports. Configure a filename template with a live preview.
- **Editing projects**: Save the current source, clip ranges, timeline view, and cross-file export queue in a `.tscut` file for later editing. Projects can reopen recordings that have grown since saving.
- **Program and video track selection**: Switch previews in files with multiple programs or video tracks. Keyframe overviews, thumbnail sheets, and frame searches follow the selected track. Multi-program TS exports can preserve all programs or keep only the current program. The selection area also appears when embedded subtitles are available.
- **Embedded subtitle preview**: Select text or bitmap subtitles in MP4, MKV and TS. ASS styles, animations and MKV font attachments are supported. Subtitles follow the displayed frame when seeking, zooming or panning; export tracks remain controlled by export settings.
- **HDR to SDR preview mapping**: Use CPU/SIMD processing for HDR10/PQ, HLG and Dolby Vision Profile 5. Temporarily toggle mapping and hardware/software decoding in the status bar; Settings stores defaults. Mapping applies to previews and captured images; audio and video are not re-encoded on export.
- **Flexible output**: Save one clip immediately, add clips to a batch export queue, or merge multiple selected clips into one file.
- **Export without transcoding**: Copy the original compressed audio and video packets.
- **Frame capture and media information**: Save or copy the current preview frame and inspect the opened file's stream information.
- **Image navigator**: Locate the visible region of a zoomed image with a floating overview in the main preview and thumbnail sheets. Click or drag to navigate; disable it in Settings.
- **High-resolution thumbnail sheets**: Generate configurable N×M contact sheets up to 12,000 pixels wide, preview them with zoom and pan, and export PNG or JPEG images with timestamps and labeled video, audio, and subtitle metadata, including language and HDR, HLG, or Dolby Vision indicators.
- **General video preview**: Open MP4, MKV, MOV, WebM, and other FFmpeg-supported video files for preview, seeking, screenshots, and media information. MP4/MOV and MKV/WebM inputs can be exported to MP4 or MKV; other non-TS formats are preview-only.

### MP4 / MKV remux editing and merging

- Choose the output container and retained tracks in **MP4 / MKV export settings**. Track order, language, titles, and default/forced flags follow the source.
- Export individual clips or batches, or merge selected ranges from one source. Overlapping selected clips are combined once.
- Video cuts use keyframes and audio cuts use compressed packet boundaries. Compatible text subtitles, including MP4 mov_text and MKV ASS, and chapters are trimmed and retimed. MKV attachments and MP4 cover art can be retained.
- Copy compressed audio/video and codec configuration, preserving container-supported color, HDR static data, and Dolby Vision configuration. Dynamic metadata in the bitstream travels with the packets.
- Check codec and track compatibility, with clear errors when content cannot be safely cut or merged.

**MP4/MKV export does not re-encode audio/video, but is not byte-for-byte lossless.** Container structures, timestamps, and metadata are rewritten, so file hashes and sizes change. Metadata fields depend on the output container. MP4 exports retaining cover art use standard metadata fields; other MP4 exports can write custom metadata. Fast MKV subtitle seeking is enabled by default and reads from about 30 seconds before each cut. Earlier subtitles spanning the cut may be omitted; disable this option in export settings to retain them. Failed or cancelled exports preserve existing destinations and remove temporary output.

### TS tools

- **TS Raw Stream Cutter**: Extract a byte or packet range directly from a TS file.
- **TS Quick Check**: Scan for synchronization loss, TEI/continuity/PES errors, PCR/PTS/DTS issues, A/V drift, bitrate changes, and export a text report.
- **TS Packet Viewer**: Inspect 188-byte TS or 192-byte M2TS packets, navigate by packet number, offset, or PID, and link parsed fields to highlighted Hex bytes. M2TS arrival headers and timestamps are also shown.
- **TS Elementary Stream Extractor**: Select one or more tracks and export their raw video, audio, subtitle, or data payload after removing TS and PES encapsulation.
- **TS Stream Filter**: Keep selected PIDs or split selected services while rebuilding the required program and service tables.
- **TS Stream Editor**: Remove tracks or services, remap identifiers and PIDs, and edit service or language metadata while preserving the original encoded media.
- **TS Timeline Repair**: Analyze and safely correct supported PCR and timestamp discontinuities without hiding transport or packet-loss errors.
- **TS Multi-source Repair**: Compare compatible recordings of the same feed and time period, then use healthy packet, PES, or elementary-stream data to repair damaged regions and long gaps where safe.
- **TS Binary Merge**: Append ordered TS segments, remove byte-identical overlaps, or merge overlapping recordings from the same source when only TS continuity counters differ.

Lossless clipping supports 188-byte TS and 192-byte M2TS based on the actual packet layout. M2TS defaults to `.m2ts` output and retains arrival headers. Quick checks (including batch checks), raw cutting, packet viewing, service splitting, and ES extraction also support M2TS. Timeline repair, stream filtering, stream editing, multi-source repair, and TS binary merge currently require 188-byte TS; unsupported packet sizes are reported explicitly.

### General

- **Multi-platform support**: Available for Windows, Linux, and macOS.
- **Multilingual interface**: English, Simplified Chinese, and Traditional Chinese.
- **Desktop themes**: Windows Classic, XP, 7, 8, 10, and 11 styles, with independent light and dark palettes and automatic switching.
- **Independent tool windows**: Open multiple tools or scans at the same time for comparison.
- **Bounded resource usage**: Large-file tools use streaming or on-demand reads instead of loading complete media files into memory.

## FFmpeg Runtime
Official release packages bundle the required **FFmpeg 9.0.2** shared libraries. End users do not need to install FFmpeg manually.

Bundled runtime source: [nilaoda/FFmpegSharedLibraries](https://github.com/nilaoda/FFmpegSharedLibraries).

> **macOS**: If the app is blocked by quarantine, run `xattr -dr com.apple.quarantine TSCutter.GUI.app`.

## Preview

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="img/dark_en.png">
  <source media="(prefers-color-scheme: light)" srcset="img/light_en.png">
  <img alt="TSCutter.GUI preview" src="img/light_en.png">
</picture>

### Desktop themes

Six Windows-inspired theme families are available on Windows, Linux, and macOS, with light and dark palettes.

<a href="docs/THEMES.md">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="img/themes/overview-dark.png">
    <img alt="Settings previews for Windows Classic, XP, 7, 8, 10, and 11 themes" src="img/themes/overview-light.png">
  </picture>
</a>

Use **View → Theme** to switch families. In **Tools → Settings**, choose light and dark palettes independently and select **Auto detect** to follow the system appearance. XP offers Luna Blue, Silver, and Olive Green; Classic includes additional color schemes.

[Browse the theme gallery →](docs/THEMES.md)

## Usage

### Main editor

1. Launch the application.
2. Open a TS, M2TS, MP4 or MKV file, or drag it into the main window.
3. Navigate or zoom the timeline, add a clip, and mark its start and end points.
4. Save the current clip, add it to the export queue, or select multiple clips and merge them.
5. Use the File menu to save a `.tscut` project and reopen it later to restore clips and queued exports. A growing source is accepted when verification of its saved portion succeeds.

Files with multiple programs, video tracks, or embedded subtitles show selectors above the preview. Subtitles are off by default and loaded in the background when selected; they can be changed or switched off at any time. After confirmation, switching the program or video track clears current clips and undo history; queued tasks retain their original program and track choices. For multi-program TS, “Export current program only” keeps all tracks in the selected program and applies to saving, merging, and newly queued tasks. New projects use version 4 and can read version 1, 2 and 3 projects; older app versions cannot open new projects.

### Preview options

In **Tools → Settings**, choose the default HDR to SDR mapping and hardware decoding preferences for newly opened files. The status bar temporarily overrides these preferences for the current file.

- The **SDR mapping** control appears only when an HDR frame is detected. The selected mapping is also used when opening keyframe overviews, thumbnail sheets, and frame searches.
- **H/W / S/W** shows the current decoding mode. Switching is available on Windows and macOS when the current video and device support hardware decoding; the control is disabled when hardware decoding is unavailable or has failed and fallen back to software.
- Mapping and decoder switches preserve the displayed frame and clip boundaries.

### Clip labels and output filenames

Click the **pencil icon** on a clip card to edit its name, notes, and color. Changes support undo/redo. Timeline bars use the assigned color, with names and notes in the hover tooltip. Adding a clip to the queue takes a snapshot of its labels and boundaries.

In **Tools → Settings → Export**, set the filename template used by single-clip saves, individual batch exports, and newly queued clips. The default is `{source}_({start}-{end})`; merged output keeps its separate filename suggestion.

| Placeholder | Value |
| --- | --- |
| `{source}` | Source filename without extension |
| `{name}` | Clip name, or `clip001` when empty (using that clip's index) |
| `{index}` | Current position in the clip list, padded to 3 digits |
| `{start}`, `{end}` | Start/end time, such as `00.00.10.000`; an unset end uses the video duration |

For example, `{source}_{index}_{name}` produces `Recording_001_Opening.ts`. The selected TS/M2TS/MP4/MKV extension is added automatically. Unsafe filename characters are replaced; batch exports append `_2`, `_3`, etc. to avoid duplicate names and existing files. The template setting applies to future exports; queued output paths stay as chosen.

### Independent TS tools

Open any utility directly from the **Tools** menu. Each tool opens in its own window and prompts for the required source file or files; no file needs to be loaded in the main editor first.

## Documentation

Design notes for the TS tools are available in [docs](docs/README.md). Source layout and namespace conventions are described in [src/README.md](src/README.md).

## Thanks
This project is inspired by an excellent editing software called [VidePub](https://sourceforge.net/projects/videpub/).

## License
This project is licensed under the GPL-3.0 License.
