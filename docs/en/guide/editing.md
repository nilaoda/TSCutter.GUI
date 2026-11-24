# Editing and exporting

## Main editor

1. Open a TS, M2TS, MP4 or MKV file, or drag it into the main window.
2. Navigate or zoom the timeline, add a clip, and mark its start and end points.
3. Save the current clip, add it to the export queue, or select multiple clips and merge them.
4. Use the File menu to save a `.tscut` project and reopen it later to restore clips and queued exports. A growing source is accepted when verification of its saved portion succeeds.

<Screenshot name="editor" alt="Main editor" />

Files with multiple programs, video tracks, or embedded subtitles show selectors above the preview. Subtitles are off by default and loaded in the background when selected; they can be changed or switched off at any time. After confirmation, switching the program or video track clears current clips and undo history; queued tasks retain their original program and track choices.

For multi-program TS, “Export current program only” keeps all tracks in the selected program and applies to saving, merging, and newly queued tasks.

::: info TS exports in keyframe mode
An ordinary single-clip TS/M2TS export copies the original bytes when all programs and tracks are retained, program tables are not patched, no audio delay is applied and the [extra processing](#start-at-time-zero-and-remove-null-packets) is off. Merged and remuxed exports adjust some structures or timestamps and are not byte-for-byte copies.
:::

## TS export settings

With a TS/M2TS file open, the area below the clip list shows how many tracks will be exported. Click **Export settings** to adjust the options below. They are saved with the project and the export queue, and apply to both keyframe cuts and smart cuts.

### Choose output tracks

Untick tracks you don't need (the current video track can't be removed). Removed tracks are dropped from the output along with their entries in the program tables; all other packets are copied as they are. A dot on the **Export settings** button shows that tracks were removed.

### Detect silent tracks

**Detect silent tracks** finds audio tracks without sound, such as empty placeholder language tracks. It decodes 12 evenly spaced 1.5-second samples across the file and unticks tracks whose peak stays below −60 dBFS; you can tick them again if needed. Dolby E tracks are checked per program group and unticked only when every group is silent. Detection runs only when you click it, so it adds no overhead otherwise. A track that has sound in only one part of the file may be misjudged. The same option is available in the MP4/MKV export settings.

### Dolby E transcoding

Broadcast contribution feeds often carry Dolby E, which ordinary players can't play. When a file contains Dolby E, a **Dolby E…** button appears next to the audio selector above the preview, where you choose what happens on export:

- **Leave the Dolby E track unchanged**: the track is copied as it is.
- **Transcode to regular audio tracks**: Dolby E packs several program groups into one track (for example a 5.1 main mix and a stereo mix); each group becomes its own track. Each group can produce one or more tracks, each with its own codec (AC3, E-AC3, AAC, FLAC or PCM), channel layout (original, or a stereo or mono downmix), bitrate and language.
- **Also keep the original Dolby E track**: when unticked, the transcoded tracks replace the original. The original takes about 2.3 Mbps, and some players play it as PCM noise.

FLAC can't be stored in TS, so FLAC output is saved as a separate `.flac` file named after the output file. PCM is written to TS as SMPTE 302M (24-bit) at a high bitrate. If the Dolby E configuration changes mid-file, missing program groups are filled with silence. During preview you can also pick a Dolby E program group in the audio selector and listen to it directly.

### Merge three stereo tracks into 5.1

Some broadcast feeds, such as sports world feeds, carry one discrete 5.1 mix as three stereo tracks (L/R, C/LFE, Ls/Rs). When the current program has three or more stereo tracks with the same sample rate, a **Merge 5.1…** button appears next to the audio selector:

1. Pick a track for each of the three slots, swapping left and right if needed.
2. The stream doesn't say how the tracks map to channels, so click **Analyze** to sample-decode them. It finds LFE tracks that contain only low frequencies, silent, duplicated or mono tracks, and suggests a mapping or warns that the tracks don't look like a split 5.1.
3. Choose the codec (AC3, E-AC3, AAC, FLAC or PCM) and whether to replace or keep the original tracks.

Once set, the audio selector gains a "Merged 5.1 (downmix preview)" entry: playback and the waveform use the merged result downmixed to stereo, so you can listen before exporting. The tracks are aligned by timestamp, and parts that start late or are missing are filled with silence. Newly opened files are not merged by default.

### Start at time zero and remove null packets

**Tools → Settings → Export** has two extra processing options for TS output, both off by default. They apply to TS/M2TS output from keyframe cuts and smart cuts, including M2TS exported from Blu-ray discs:

- **Start at time zero**: shifts the PCR, PTS and DTS of the output so the file starts at zero, which avoids wrong durations in some older players that use the first timestamp. Audio and video are not re-encoded and tracks keep their relative timing; programs with different clocks each start at zero, and the arrival timestamps in M2TS headers are unchanged.
- **Remove null packets**: drops the PID 0x1FFF stuffing packets. The file gets smaller and the bitrate becomes variable instead of constant, which devices or workflows that rely on a constant bitrate may not accept.

With either option on, the output is no longer byte-identical to the source, the export is no longer labelled a "TS byte copy", and the TS check during export covers the processed data.

### Export types

Export records and the result window label each export by what was actually done: output that is only copied is a "TS byte copy", and a single-clip export with any change is a "TS packet export", with tags listing changes such as current program only, patched program tables, selected tracks, audio delay, Dolby E transcoding, merged 5.1, start time zeroed and null packets removed.

## Disk space check

Before an export starts, the size of each output is estimated and compared with the free space on the destination disk. This covers saving a clip, export all, merging, remove-mode export and the export queue. Keyframe cuts are measured exactly from the source byte range; smart cuts, remuxes and disc titles are estimated with a margin; Dolby E transcoding and merged 5.1 add the size of the new audio tracks. Queue items writing to the same disk are added up.

A dialog appears only when space is clearly short. It lists each disk's required and available space, defaults to cancelling, and offers **Export Anyway**. When space is enough, or cannot be read (for example a network drive that does not answer), the export starts right away. If the disk still fills up during an export, a clear "destination disk is full" message is shown and the half-written file is removed; in the queue, the remaining items for that disk are marked as failed and items on other disks continue.

## Export results

After saving a clip, merging clips or exporting in remove mode, a result window shows the output location, size, elapsed time and average speed, the clip content, the smart cut re-encoding share and the TS check result, with buttons to open the file, show it in its folder or copy a summary.

- Failed exports show the error in the same window; cancelled exports show nothing and remove the unfinished file.
- After choosing “Don't show again” or turning the option off in Settings, successful exports no longer open the window, while failures still do.
- The export queue and other batch exports do not use this window; see Export records for their results.

## Clip labels

Click the **pencil icon** on a clip card to edit its name, notes, and color. Changes support undo/redo. Timeline bars use the assigned color, with names and notes in the hover tooltip. Queued clips keep the labels and boundaries they had when added.

## Cut point transfer

**File → Export cut points…** saves the current clip list as a `.tscutpoints` file, with names, notes, colors, boundaries and the keep/remove mode. After opening media, **File → Import cut points…** applies the marks. To save the full workspace, including export queue items and output settings, use a project file.

- **Exact restoration**: when the media content and video track match the file and the offset is zero, the saved PTS, byte positions and cut mode are restored, even for renamed or moved copies. If the saved smart cut mode is not available for the media, exact restoration is not offered and the dialog explains why.
- **Transfer by time**: for another recording, or with an offset, boundaries are located again in the target's current cut mode, in the background and with cancel. A positive offset moves points later and a negative one earlier: if the same picture is at `10:00.000` in the source and `10:02.400` in the target, enter `2.400` seconds. Or position the target at the picture of the first cut point and choose **Align first point to current picture**.

The dialog shows a reference thumbnail and each clip's target times. Out-of-range clips are skipped in full rather than shortened. Transferred clips are marked for boundary review; keyframe cuts follow the target's keyframe spacing, and a clip shorter than that spacing is extended to the next keyframe. Missing sections or timing drift between recordings need individual adjustments.

Clear **Replace the current clips** to append to the list. When the cut mode or keep/remove mode cannot be mixed, select Replace. A completed import can be undone in one step, including the cut mode and keep/remove mode it changed; export queue items are kept.

## Output filenames

In **Tools → Settings → Export**, set the filename template used by single-clip saves, individual batch exports, and newly queued clips. The default is `{source}_({start}-{end})`; merged outputs are named separately.

| Placeholder | Value |
| --- | --- |
| `{source}` | Source filename without extension |
| `{name}` | Clip name, or `clip001` when empty (using that clip's index) |
| `{index}` | Current position in the clip list, padded to 3 digits |
| `{start}`, `{end}` | Start/end time, such as `00.00.10.000`; an unset end uses the video duration |

For example, `{source}_{index}_{name}` produces `Recording_001_Opening.ts`. The selected TS/M2TS/MP4/MKV extension is added automatically. Unsafe filename characters are replaced; batch exports append `_2`, `_3`, etc. to avoid duplicate names and existing files. The template applies to future exports; queued output paths stay as chosen.
