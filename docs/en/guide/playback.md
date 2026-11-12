# Playback and preview

## Playback

Click the play button or press <kbd>Space</kbd> to play from the displayed frame; press again to pause. Right-click the play button and choose **Play selected clip** to play just that clip and pause at its end. Seeking through the timeline or thumbnails resumes playback if it was running, and stays paused otherwise. DVD and Blu-ray titles can also be played.

Choose an audio track above the preview. The first decodable track is selected by default. Unsupported tracks, such as AV3A, remain visible but disabled; video plays silently if no track can be decoded. The volume button and slider affect preview audio only, not exports.

In keyframe mode, marking a boundary while paused snaps to a compatible keyframe at or before the displayed frame and shows the aligned frame. Smart cut uses the displayed frame itself.

### Full screen

Double-click the video, click the full-screen button, or choose **View → Full-screen Playback** to enter full screen. <kbd>F11</kbd> toggles full screen; macOS also supports <kbd>Control</kbd> + <kbd>Command</kbd> + <kbd>F</kbd>. Press <kbd>Esc</kbd> to return to the editor. Controls hide after three seconds of inactivity during playback and reappear when the pointer moves. They stay visible while paused.

### Rendering and frame rate

Playback uses the GPU for color conversion, scaling and HDR mapping when supported, with a CPU fallback. The status bar shows presentation FPS: green means decoding and display keep up without dropping frames, yellow indicates reduced smoothness, and red means the frame rate is below 70% of the source rate. Playback may skip frames to keep up when performance is insufficient.

### Playback performance test

Use **Playback performance test…** in the toolbar's Tools dropdown to test the current file, track, position and preview settings. **Tools → Analyze and Inspect → Playback performance test…** in the menu bar asks for another file.

The test shows separate FPS results for decoding, frame processing and playback, along with the CPU or GPU path used. Slow CPU frame conversion does not necessarily limit GPU playback; check the playback result to see whether it keeps up. Adjust the output size to compare performance at different window sizes. Tests run silently and pause main-window playback. Results apply to the tested section and settings.

## Preview options

In **Tools → Settings → Video**, choose the default HDR to SDR mapping and hardware decoding preferences for newly opened files. The status bar temporarily overrides these preferences for the current file.

- The **SDR mapping** control appears only when an HDR frame is detected. The selected mapping is also used when opening keyframe overviews, thumbnail sheets, and frame searches.
- **H/W / S/W** shows the current decoding mode. Switching is available on Windows and macOS when the current video and device support hardware decoding; the control is disabled when hardware decoding is unavailable or has failed and fallen back to software.
- Mapping and decoder switches preserve the displayed frame and clip boundaries.

### Decoding device

In **Tools → Settings → Video**, choose which graphics card hardware decoding uses (available when **Prefer hardware decoding** is checked). **Automatic** (default) lets the system choose. On Windows each graphics card is listed once (virtual displays and remote desktop adapters are hidden), which helps on computers with integrated and dedicated graphics where the default card cannot decode a codec.

A chosen card is used for preview, frame stepping and playback; if it can't decode the current video, software decoding is used; if it has been removed, automatic selection is used and the saved choice is kept until you change it. Playback on a card other than the one drawing the window copies frames through system memory. macOS always lets the system choose the GPU, and Linux builds don't use hardware decoding, so only **Automatic** is available there. The setting applies to videos opened and playback started afterwards.

### Audio waveform

Below the navigation strip, a smoothed loudness curve shows the playback audio track for the strip's range, in a bar as tall as the program band. Each thumbnail covers the sound from its frame to the next one; the curve shows how loudness rises and falls rather than every detail.

The full visible range is read in chunks of up to 30 seconds, prioritizing the current position. Updates pause during playback. No waveform is shown when the audio track can't be decoded. Turn it off in **View → Audio waveform** or **Settings → Preview** to stop reading waveform data.

### Audio delay

Next to the audio track selector above the preview, check **Delay** and enter milliseconds: positive values make the sound later, negative values earlier (within ±5 seconds; right-click to reset to 0). When unchecked, the delay has no effect and exports are copied unchanged; the value is kept.

- Preview playback and the navigation strip waveform shift by the delay so you can match it against the picture.
- The delay applies to every audio track of the current program, not only the one selected for preview; in a multi-program TS, other programs keep their audio unchanged.
- Whether it is enabled and its value are saved with projects, and queued exports keep the delay that was in effect when they were added.
- Exports only rewrite the audio timestamps: keyframe-mode TS clips are still copied packet by packet, keeping packet count, continuity counters, PCR and other PIDs unchanged and changing only the timestamps in audio PES headers; smart cut and MP4/MKV remux likewise shift only the audio.
- Clips are cut by picture, so a delay leaves one end of the clip without sound for the length of the delay. Large offsets may make some hardware players drop the first moments of sound.

### Navigation strip thumbnail size

In **Tools → Settings → Preview**, choose Small, Medium (default) or Large thumbnails for the navigation strip. Larger thumbnails make the strip taller and fit fewer frames across the same width; the change applies immediately.

### Mouse wheel

In **Tools → Settings → Preview**, choose the wheel step unit (keyframes, frames or seconds), how many units each notch moves, the step while holding <kbd>Shift</kbd> (10 by default, like <kbd>Shift</kbd>+arrow keys) and the scroll direction.

- Frames apply only in smart cut mode; other modes move by keyframes.
- With seconds, smart cut mode goes to the frame at the target time, and keyframe cut mode goes to a keyframe near it, as clicking the timeline does, always moving at least one keyframe.
- The wheel over the preview can navigate instead of zooming (hold <kbd>Ctrl</kbd>, or <kbd>⌘</kbd> on macOS, to zoom); over the timeline it can navigate or, when zoomed in, pan left and right. <kbd>Ctrl</kbd>/<kbd>⌘</kbd> plus the wheel always zooms the timeline.

### Shortcuts

**Tools → Settings → Shortcuts** lists every keyboard and mouse action for the current platform and can be searched by action or key; key bindings cannot be changed yet.
