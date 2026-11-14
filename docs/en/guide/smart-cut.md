# Smart cut

Turn on the **Smart cut** switch on the right side of the toolbar to place clip boundaries on any frame instead of keyframes. On export, only a few pictures near the start and end are re-encoded; the compressed video in between is copied directly, and audio and subtitles are copied packet by packet without re-encoding. When a boundary falls exactly on a keyframe that can be joined, that side is not re-encoded at all.

## Steps

1. After opening a video, turn on the **Smart cut** switch on the right side of the toolbar.
2. Find frames with the timeline, the navigation strip, or the <kbd>,</kbd> / <kbd>.</kbd> keys, then mark the start and end. Scrolling the mouse wheel over the navigation strip moves to the previous or next keyframe per notch; Settings can make it step by frames or seconds instead (see [Playback and preview](./playback#mouse-wheel)).
3. Save, queue, or merge the clips; the export window shows re-encoding and copying progress.

The switch state is remembered and saved with projects, and queued exports keep the mode chosen when they were added. If any clip boundaries fall outside keyframes, the app lists those clips. Adjust their boundaries before switching back to keyframe cutting.

<Screenshot name="smart-cut" alt="Smart cut: the navigation strip shows every frame" />

## Features

- **Frame-by-frame navigation**: The navigation strip shows every frame, and <kbd>,</kbd> / <kbd>.</kbd> step one frame backward or forward. Marking after pausing playback no longer snaps to a keyframe.
- **Broadcast recordings**: Some H.264 and HEVC open-GOP recordings without IDR frames are supported. The app checks whether video can be copied at the join and re-encodes leading pictures where needed. If a safe join is not possible, the export stops with an explanation.
- **Gradual refresh recordings**: H.264 and MPEG-2 with no complete keyframes, where the picture is rebuilt only by a rolling intra refresh (common in satellite contribution feeds, such as feeds carrying Dolby E), can also be smart cut. The copied part starts at a refresh entry point; the first few seconds differ slightly from the source and match it exactly after one refresh cycle, usually under 5 seconds.
- **Export progress**: The export window shows a plan bar with re-encoded (orange) and copied (green) parts, the current phase, and the share that needs no re-encoding.
- **Saving and merging**: Save single clips, use the batch export queue, or merge several clips from one file.

## Supported video

| Codec | Supported |
| --- | --- |
| H.264 | Baseline, Main and High (8-bit) and High 4:2:2 (8/10-bit), including interlaced |
| HEVC | Main, Main 10 and 4:2:2 |
| MPEG-2 | Main, Simple and 4:2:2, including open GOP and interlaced |
| AV1 | Main (MP4/MKV only) |
| VP9 | Profile 0/2 (MP4/MKV only) |

Re-encoded pictures keep HDR10, HLG and single-layer Dolby Vision Profile 5/8.1.

## Output formats

- TS/M2TS sources export to the same format; MP4, MKV and other remuxable sources export to MP4 or MKV.
- **TS checks**: Directly copied parts of TS/M2TS output retain source transport packets, so continuity counter gaps, TEI, PCR/PTS jumps and similar source problems may still appear in the output. Use [TS Quick Check](./ts-tools) to inspect them. Multi-program TS outputs only the program being previewed.
- **DVD and Blu-ray titles**: Output uses the container chosen in the export options (DVD: VOB, MKV or MP4; Blu-ray: M2TS, MKV or MP4) and is written by the muxer. See [DVD and Blu-ray](./discs#smart-cut).

## Limitations

The switch is disabled for unsupported sources, and hovering over it explains why (for example 4:4:4, HDR10+ or soft pulldown). Each re-encoded part is limited to 60 seconds; if no joinable keyframe can be found, the export stops with a clear error before writing. Re-encoded pictures differ slightly from the original. The runtime bundled with official releases includes the required encoders.
