# Getting started

TSCutter.GUI is a cross-platform video editor with fast keyframe cutting, frame-accurate smart cutting, media muxing, and MPEG-TS analysis and repair tools. Available for **Windows, macOS and Linux**.

## Download

Download the latest Beta for your platform from [GitHub Releases](https://github.com/nilaoda/TSCutter.GUI/releases/latest).

Official packages include the required [FFmpeg runtime](https://github.com/nilaoda/FFmpegSharedLibraries); no separate FFmpeg installation is needed.

::: warning CPU requirement
On x64, the CPU must support SSE4.2 and POPCNT (x86-64-v2). Unsupported CPUs include Intel Core 2 and Atom models before 2013, and AMD Athlon 64, Phenom, Athlon II, A-series 3000 and E-series (Bobcat); on these the app exits right after launch, so use version 0.7.1 or earlier. Apple silicon and Arm64 are not affected.
:::

::: tip macOS
If quarantine blocks the app, run this in Terminal:

```sh
xattr -dr com.apple.quarantine TSCutter.GUI.app
```
:::

## Four steps

1. Open a video or drag it into the window.
2. Navigate to the desired frames and mark a clip's start and end. Enable **Smart cut** for frame-level boundaries, or **Remove** to mark unwanted ranges.
3. Save a clip, merge selected clips, or export the video without the selected ranges.
4. Save a `.tscut` project to continue later.

Recently opened videos, discs and projects can be reopened from **File → Open Recent**, which keeps up to 10 items; press a number key to pick one of the first nine. Reopening a disc preselects the title chosen last time. Files that can't be found are marked, and opening one asks whether to remove it. You can turn recording off or clear the list on the General page of **Tools → Settings**.

Open and save dialogs each remember the folder used last time, also after a restart. Opening videos, discs, projects and tool inputs starts in the last open folder. Saving clips, exports, screenshots and TS tool outputs starts in the last export folder, or the source file's folder when there is none yet. To start save dialogs in the source file's folder instead, change **Save and export dialogs open in** in **Tools → Settings → Export**. If a remembered folder is temporarily unavailable, for example on a disconnected external drive, the next choice is used, and the folder works again once the drive is back.

<Screenshot name="editor" alt="Main window with preview, navigation strip, timeline and clip list" />

## Two cutting modes

| Mode | How it works |
| --- | --- |
| **Keyframe cutting** (default) | Cut at compatible keyframes without re-encoding audio or video. |
| **Smart cut** | Choose any frame. Re-encode video near the cut points and copy the middle; audio and subtitles are not re-encoded. |

Turn on **Remove** to mark sections to discard, such as commercials or opening credits, then export the remaining video as one file. Remove mode works with either cutting mode.

## Supported formats

- Cut TS/M2TS, and remux MP4/MOV and MKV/WebM.
- Preview other video formats supported by FFmpeg.
- Open DVD-Video and unencrypted Blu-ray ISO images or disc folders.

## Auto-save and recovery

Unsaved clips, the export queue and project settings are written to a recovery file in the app data folder at regular intervals. Project files and source files are never touched. After a crash or power cut, the next launch lists the work that can be recovered, with the source file, project, clip and queue counts and the save time. Choose to recover it, delete it, or be asked again next time; a missing source file can be located during recovery. Recovered work is marked unsaved and the title bar shows "Recovered". The recovery file is deleted on a normal exit, after saving the project, or when you discard changes.

Turn off **Auto-save recovery data** or change the interval (30 seconds to 10 minutes, 1 minute by default) on the General page of **Tools → Settings**. Each running app instance keeps its own recovery file. Disc sources cannot be saved as projects and are not recovered.

## Task progress

The progress of long-running tasks such as exports, the export queue, TS checks and repairs, media mux and thumbnail sheets is shown on the Windows taskbar button or the macOS Dock icon. A queued export shows the progress of the whole queue, several tasks at once show their combined progress, and the bar turns red when a task fails. Turn it off with **Show progress on the taskbar or Dock icon** on the General page of **Tools → Settings**; nothing is shown on Linux.

## Feedback and community

- [Report an issue](https://github.com/nilaoda/TSCutter.GUI/issues)
- [Discord community](https://discord.gg/qG7kk9GQ44)
