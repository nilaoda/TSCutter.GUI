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
An ordinary single-clip TS/M2TS export copies the original bytes when all programs and tracks are retained, program tables are not patched and no audio delay is applied. Merged and remuxed exports adjust some structures or timestamps and are not byte-for-byte copies.
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

### Export types

Export records and the result window label each export by what was actually done: output that is only copied is a "TS byte copy", and a single-clip export with any change is a "TS packet export", with tags listing changes such as current program only, patched program tables, selected tracks, audio delay, Dolby E transcoding and merged 5.1.

## Export results

After saving a clip, merging clips or exporting in remove mode, a result window shows the output location, size, elapsed time and average speed, the clip content, the smart cut re-encoding share and the TS check result, with buttons to open the file, show it in its folder or copy a summary.

- Failed exports show the error in the same window; cancelled exports show nothing and remove the unfinished file.
- After choosing “Don't show again” or turning the option off in Settings, successful exports no longer open the window, while failures still do.
- The export queue and other batch exports do not use this window; see Export records for their results.

## Clip labels

Click the **pencil icon** on a clip card to edit its name, notes, and color. Changes support undo/redo. Timeline bars use the assigned color, with names and notes in the hover tooltip. Queued clips keep the labels and boundaries they had when added.

## Output filenames

In **Tools → Settings → Export**, set the filename template used by single-clip saves, individual batch exports, and newly queued clips. The default is `{source}_({start}-{end})`; merged outputs are named separately.

| Placeholder | Value |
| --- | --- |
| `{source}` | Source filename without extension |
| `{name}` | Clip name, or `clip001` when empty (using that clip's index) |
| `{index}` | Current position in the clip list, padded to 3 digits |
| `{start}`, `{end}` | Start/end time, such as `00.00.10.000`; an unset end uses the video duration |

For example, `{source}_{index}_{name}` produces `Recording_001_Opening.ts`. The selected TS/M2TS/MP4/MKV extension is added automatically. Unsafe filename characters are replaced; batch exports append `_2`, `_3`, etc. to avoid duplicate names and existing files. The template applies to future exports; queued output paths stay as chosen.
