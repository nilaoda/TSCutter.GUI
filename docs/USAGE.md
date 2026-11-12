# User guide

**English** | [简体中文](USAGE_CN.md) · [Back to README](../README.md)

## Main editor

1. Open a TS, M2TS, MP4 or MKV file, or drag it into the main window.
2. Navigate or zoom the timeline, add a clip, and mark its start and end points.
3. Save the current clip, add it to the export queue, or select multiple clips and merge them.
4. Use the File menu to save a `.tscut` project and reopen it later to restore clips and queued exports. A growing source is accepted when verification of its saved portion succeeds.

Files with multiple programs, video tracks, or embedded subtitles show selectors above the preview. Subtitles are off by default and loaded in the background when selected; they can be changed or switched off at any time. After confirmation, switching the program or video track clears current clips and undo history; queued tasks retain their original program and track choices.

For multi-program TS, “Export current program only” keeps all tracks in the selected program and applies to saving, merging, and newly queued tasks. Projects using newer editing features require a compatible app version.

In keyframe mode, an ordinary single-clip TS/M2TS export copies the original bytes when all programs are retained and program tables are not patched. Merged and remuxed exports adjust some structures or timestamps and are not byte-for-byte copies.

After saving a clip, merging clips or exporting in remove mode, a result window shows the output location, size, elapsed time and average speed, the clip content, the Smart cut re-encoding share and the TS check result, with buttons to open the file, show it in its folder or copy a summary. Failed exports show the error in the same window; cancelled exports show nothing and remove the unfinished file. After choosing “Don't show again” or turning the option off in Settings, successful exports no longer open the window, while failures still do. The export queue and other batch exports do not use this window; see Export records for their results.

## Remove mode

1. Turn on **Remove** in the toolbar, to the left of **Smart cut**. The setting is remembered across launches.
2. Mark ranges to remove as you would mark clips: the start is the first removed frame and the end is the frame where playback resumes, which is kept. A range without an end is removed up to the end of the file.
3. Select the ranges to remove and click **Export without ranges**; the rest is joined into one file.

Ranges to remove are shown in red on the timeline, the frame strip and the clip cards (the resume frame stays unmarked). Clip cards show the removed size (N/A for sources that are remuxed), and the summary shows the number of ranges, the remaining duration and the estimated size, replaced by the actual output size after export.

In keyframe cut mode both ends fall on keyframes, so an open-GOP source may lose a few leading pictures at a removal start; smart cut mode removes exact frames.

Clips cannot be saved, queued or exported separately in remove mode. The mode is saved with projects (project version 6, which older versions cannot open). Disc sources support keyframe cutting only.

## Smart cut mode

1. After opening a video, turn on the **Smart cut** switch on the right side of the toolbar.
2. Find frames with the timeline, the navigation strip, or the **,** / **.** keys, then mark the start and end. Scrolling the mouse wheel over the navigation strip moves to the previous or next keyframe per notch; Settings can make it step by frames or seconds instead (see Preview options).
3. Save, queue, or merge the clips; the export window shows re-encoding and copying progress.

The switch state is remembered and saved with projects, and queued exports keep the mode chosen when they were added. If any clip boundaries fall outside keyframes, the app lists those clips. Adjust their boundaries before switching back to keyframe cutting.

## Playback

Click the play button or press **Space** to play from the displayed frame; press again to pause. Right-click the play button and choose **Play selected clip** to play just that clip and pause at its end. Seeking through the timeline or thumbnails resumes playback if it was running, and stays paused otherwise. DVD and Blu-ray titles can also be played.

Choose an audio track above the preview. The first decodable track is selected by default. Unsupported tracks, such as AV3A, remain visible but disabled; video plays silently if no track can be decoded. The volume button and slider affect preview audio only, not exports.

Double-click the video, click the full-screen button, or choose **View → Full-screen Playback** to enter full screen. **F11** toggles full screen; macOS also supports **Control + Command + F**. Press **Esc** to return to the editor. Controls hide after three seconds of inactivity during playback and reappear when the pointer moves. They stay visible while paused.

In keyframe mode, marking a boundary while paused snaps to a compatible keyframe at or before the displayed frame and shows the aligned frame. Smart cut uses the displayed frame itself.

Playback uses the GPU for color conversion, scaling and HDR mapping when supported, with a CPU fallback. The status bar shows presentation FPS: green means decoding and display keep up without dropping frames, yellow indicates reduced smoothness, and red means the frame rate is below 70% of the source rate. Playback may skip frames to keep up when performance is insufficient.

### Playback performance test

Use **Playback performance test…** in the toolbar's Tools dropdown to test the current file, track, position and preview settings. **Tools → Analyze and Inspect → Playback performance test…** in the menu bar asks for another file.

The test shows separate FPS results for decoding, frame processing and playback, along with the CPU or GPU path used. Slow CPU frame conversion does not necessarily limit GPU playback; check the playback result to see whether it keeps up. Adjust the output size to compare performance at different window sizes. Tests run silently and pause main-window playback. Results apply to the tested section and settings.

## Preview options

In **Tools → Settings**, choose the default HDR to SDR mapping and hardware decoding preferences for newly opened files. The status bar temporarily overrides these preferences for the current file.

- The **SDR mapping** control appears only when an HDR frame is detected. The selected mapping is also used when opening keyframe overviews, thumbnail sheets, and frame searches.
- **H/W / S/W** shows the current decoding mode. Switching is available on Windows and macOS when the current video and device support hardware decoding; the control is disabled when hardware decoding is unavailable or has failed and fallen back to software.
- Mapping and decoder switches preserve the displayed frame and clip boundaries.
- **Audio waveform**: Below the navigation strip, a smoothed loudness curve shows the playback audio track for the strip's range, in a bar as tall as the program band. Each thumbnail covers the sound from its frame to the next one; the curve shows how loudness rises and falls rather than every detail. The full visible range is read in chunks of up to 30 seconds, prioritizing the current position. Updates pause during playback. No waveform is shown when the audio track can't be decoded. Turn it off in **View → Audio waveform** or **Settings → Preview** to stop reading waveform data.
- **Audio delay**: Next to the audio track selector above the preview, check **Delay** and enter milliseconds: positive values make the sound later, negative values earlier (within ±5 seconds; right-click to reset to 0). When unchecked, the delay has no effect and exports are copied unchanged; the value is kept. Preview playback and the navigation strip waveform shift by the delay so you can match it against the picture. The delay applies to every audio track of the current program, not only the one selected for preview; in a multi-program TS, other programs keep their audio unchanged. Whether it is enabled and its value are saved with projects, and queued exports keep the delay that was in effect when they were added. Exports only rewrite the audio timestamps: keyframe-mode TS clips are still copied packet by packet, keeping packet count, continuity counters, PCR and other PIDs unchanged and changing only the timestamps in audio PES headers; smart cut and MP4/MKV remux likewise shift only the audio. Clips are cut by picture, so a delay leaves one end of the clip without sound for the length of the delay. Large offsets may make some hardware players drop the first moments of sound.
- **Navigation strip thumbnail size**: in **Tools → Settings → Preview**, choose Small, Medium (default) or Large thumbnails for the navigation strip. Larger thumbnails make the strip taller and fit fewer frames across the same width; the change applies immediately.
- **Mouse wheel**: in **Tools → Settings → Preview**, choose the wheel step unit (keyframes, frames or seconds), how many units each notch moves, the step while holding Shift (10 by default, like Shift+arrow keys) and the scroll direction. Frames apply only in Smart cut mode; other modes move by keyframes. With seconds, Smart cut mode goes to the frame at the target time, and keyframe cut mode goes to a keyframe near it, as clicking the timeline does, always moving at least one keyframe. The wheel over the preview can navigate instead of zooming (hold Ctrl, or ⌘ on macOS, to zoom); over the timeline it can navigate or, when zoomed in, pan left and right. Ctrl/⌘ plus the wheel always zooms the timeline.
- **Tools → Settings → Shortcuts** lists every keyboard and mouse action for the current platform and can be searched by action or key; key bindings cannot be changed yet.

## Clip labels and output filenames

Click the **pencil icon** on a clip card to edit its name, notes, and color. Changes support undo/redo. Timeline bars use the assigned color, with names and notes in the hover tooltip. Queued clips keep the labels and boundaries they had when added.

In **Tools → Settings → Export**, set the filename template used by single-clip saves, individual batch exports, and newly queued clips. The default is `{source}_({start}-{end})`; merged outputs are named separately.

| Placeholder | Value |
| --- | --- |
| `{source}` | Source filename without extension |
| `{name}` | Clip name, or `clip001` when empty (using that clip's index) |
| `{index}` | Current position in the clip list, padded to 3 digits |
| `{start}`, `{end}` | Start/end time, such as `00.00.10.000`; an unset end uses the video duration |

For example, `{source}_{index}_{name}` produces `Recording_001_Opening.ts`. The selected TS/M2TS/MP4/MKV extension is added automatically. Unsafe filename characters are replaced; batch exports append `_2`, `_3`, etc. to avoid duplicate names and existing files. The template setting applies to future exports; queued output paths stay as chosen.

## Chapters and recorded programs (EPG)

When a file or disc title has chapters (MP4, MKV, DVD, Blu-ray), or a TV recording carries EPG (the DVB present/following table), a band above the timeline splits it by chapter or by the program on air, and the status bar shows the chapter or program count. Chapters take precedence when both are present. The band follows the timeline zoom and highlights the segment under the playhead.

- Hover to see a chapter's range and length, or a program's schedule (with date), range in the recording and description; click to go to the segment start.
- Right-click to go to the chapter (program) start or end, or **Add clip for chapter** (**Add clip for program**). The new clip is named after the chapter or program, or "Chapter N" when the chapter has no name, and its boundaries follow the current cut mode.
- EPG is read only near the start of the file; if none is found there, the file is treated as having no EPG and is not scanned further. After you jump elsewhere and stay for a moment, that position is checked once. EPG switches at scheduled times, so the actual broadcast is often a few minutes early or late. A clip added for a program shows "EPG times, check the start and end" until you mark the start or end again.
- The Chapters or EPG tab of the toolbar's **Info** window lists every chapter, or every program read from the file with its air date and time and whether it is in this recording; click a chapter or a program in the recording to jump to it.

## Independent TS tools

Open any utility directly from the **Tools** menu. Each tool opens in its own window and prompts for the required source file or files; no file needs to be loaded in the main editor first.

## Smart cut

Turn on the **Smart cut** switch on the right side of the toolbar to place clip boundaries on any frame instead of keyframes. On export, only a few pictures near the start and end are re-encoded; the compressed video in between is copied directly, and audio and subtitles are copied packet by packet without re-encoding. When a boundary falls exactly on a keyframe that can be joined, that side is not re-encoded at all.

- **Frame-by-frame navigation**: The navigation strip shows every frame, and **,** / **.** step one frame backward or forward. Marking after pausing playback no longer snaps to a keyframe.
- **Supported video**: H.264 (Baseline, Main, High and High 4:2:2; 8/10-bit, including interlaced), HEVC (Main, Main 10 and 4:2:2), MPEG-2 (Main and Simple, including open GOP and interlaced), AV1 (Main) and VP9 (Profile 0/2). Re-encoded pictures keep HDR10, HLG and single-layer Dolby Vision Profile 5/8.1.
- **Broadcast recordings**: Some H.264 and HEVC open-GOP recordings without IDR frames are supported. The app checks whether video can be copied at the join and re-encodes leading pictures where needed. If a safe join is not possible, the export stops with an explanation.
- **Output formats**: TS/M2TS sources export to the same format; MP4, MKV and other remuxable sources export to MP4 or MKV. AV1 and VP9 support MP4/MKV only.
- **DVD and Blu-ray titles**: Smart cut works on disc titles too, using the container chosen in the export options (DVD: VOB, MKV or MP4; Blu-ray: M2TS, MKV or MP4). Output is written by the muxer, so the packet-level preservation described under TS checks does not apply. DVD jumps rebuild timestamps, so each selected picture is remembered by a nearby keyframe and its offset, and the export reads forward from shortly before the first cut to find exactly that picture. DVD subtitles are kept in VOB and MKV output. Blu-ray PGS subtitles are kept in M2TS and MKV output: the subtitle on screen at a cut is restored by reading about 30 seconds back (if its start is not found there, the cut point is treated as having no subtitle), and the screen is cleared at each segment end.
- **TS checks**: Directly copied parts of TS/M2TS output retain source transport packets, so continuity counter gaps, TEI, PCR/PTS jumps and similar source problems may still appear in the output. Use TS Quick Check to inspect them. Multi-program TS outputs only the program being previewed.
- **Export progress**: The export window shows a plan bar with re-encoded (orange) and copied (green) parts, the current phase, and the share that needs no re-encoding.
- **Saving and merging**: Save single clips, use the batch export queue, or merge several clips from one file.

The switch is disabled for unsupported sources, and hovering over it explains why (for example 4:4:4, HDR10+ or soft pulldown). Each re-encoded part is limited to 60 seconds; if no joinable keyframe can be found, the export stops with a clear error before writing. Re-encoded pictures differ slightly from the original. The runtime bundled with official releases includes the required encoders. Implementation notes are in [the smart cut design document](智能剪辑实现说明.md) (Chinese).

## MP4 / MKV remux editing and merging

The following describes keyframe-mode exports. Smart cut re-encodes video near cut points as described above.

- Choose the output container and retained tracks in **MP4 / MKV export settings**. Track order, language, titles, and default/forced flags follow the source.
- Export individual clips or batches, or merge selected ranges from one source. Overlapping selected clips are combined once.
- Video cuts use keyframes and include the marked end frame; audio cuts use compressed packet boundaries. Compatible text subtitles, including MP4 mov_text and MKV ASS, and chapters are trimmed and retimed. MKV also supports PGS bitmap subtitles, restoring the picture active at each cut and clearing it at the end. MKV attachments and MP4 cover art can be retained.
- Copy compressed audio/video and codec configuration, preserving container-supported color, HDR static data, and Dolby Vision configuration. Dynamic metadata in the bitstream travels with the packets.
- Check codec and track compatibility, with clear errors when content cannot be safely cut or merged.

**Keyframe-mode MP4/MKV export does not re-encode audio/video, but is not byte-for-byte lossless.** Container structures, timestamps, and metadata are rewritten, so file hashes and sizes change. Metadata fields depend on the output container. MP4 exports retaining cover art use standard metadata fields; other MP4 exports can write custom metadata.

Fast MKV text subtitle seeking is enabled by default and reads from about 30 seconds before each cut. Earlier text subtitles spanning the cut may be omitted; disable this option in export settings to retain them. PGS subtitles separately restore their required display state.

Failed or cancelled exports preserve existing destinations and remove temporary output.

## Media muxing

Open **Tools → Merge and Mux → Media Muxer** to combine tracks from video, audio and subtitle files. Select tracks, drag them into output order, and edit their names, language codes, default/forced flags and time offsets. Output supports MKV, MP4, TS and M2TS without re-encoding audio or video.

HEVC MP4 outputs use hvc1, or dvh1 when Dolby Vision is signaled, while preserving in-band decoder parameters.

**Optimize for web playback** is available for MP4 and off by default; it moves the index to the beginning and requires an additional move of data within the output file when finishing.

Positive offsets delay a track; negative offsets advance it, like FFmpeg `-itsoffset`. The output container handles timestamps before zero: MP4 uses an edit list to skip leading content, while MKV and TS/M2TS may shift the timeline.

**Stop at the shortest track** is off by default; enable it to stop at the earliest audio/video end after offsets, and optionally include subtitles. Cover art and attachments are ignored. Cuts follow compressed packet boundaries; subtitle display durations are clipped to the end. Tracks without duration information require an initial packet read to determine their end.

Compatible subtitle tracks are copied. Other text subtitles are automatically converted to mov_text for MP4 or SRT for MKV, with the conversion and possible style loss shown before starting. Bitmap subtitles are not converted to text; unsupported tracks must be deselected or use another container. The official runtime includes the required subtitle encoders.

TS/M2TS does not preserve track names or default/forced flags.

Failed or cancelled operations retain an existing destination and remove the temporary output. Errors include the muxing stage and expandable FFmpeg diagnostics.

## DVD and Blu-ray

Open DVD-Video and unencrypted Blu-ray sources from ISO images or disc folders (`VIDEO_TS` / `BDMV`), choose a title or playlist, navigate across its files, and preview VOB or PGS subtitles. Cut without re-encoding, defaulting to VOB for DVD and M2TS for Blu-ray; MP4/MKV remain available for compatible tracks. DVD CSS protection is supported where the runtime can handle it.

Lossless export rejects playlist joins that enter a clip inside a GOP. Disc M2TS output is remuxed with timestamps rebuilt for the exported timeline.

Playlists whose stream layout changes between PlayItems have not been validated; track selection reflects the streams detected when opening the title. Disc menus, protected Blu-ray discs and saving disc sources in projects are not supported.

Standalone VOB does not carry the IFO subtitle palette; use MKV to preserve subtitle colors in one file.

DVD seeking is only VOBU-accurate, so preview times may be off by less than a second; export cut points use the keyframes displayed when marking.

## TS tools and packet layouts

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

See the [tool design documents](README.md) and [theme gallery](THEMES.md).
