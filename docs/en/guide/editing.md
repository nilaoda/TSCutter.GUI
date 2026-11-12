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
An ordinary single-clip TS/M2TS export copies the original bytes when all programs are retained and program tables are not patched. Merged and remuxed exports adjust some structures or timestamps and are not byte-for-byte copies.
:::

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
