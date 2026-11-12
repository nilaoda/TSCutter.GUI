# Getting started

TSCutter.GUI is a cross-platform video editor with fast keyframe cutting, frame-accurate smart cutting, media muxing, and MPEG-TS analysis and repair tools. Available for **Windows, macOS and Linux**.

## Download

Download the latest Beta for your platform from [GitHub Releases](https://github.com/nilaoda/TSCutter.GUI/releases/latest).

Official packages include the required [FFmpeg runtime](https://github.com/nilaoda/FFmpegSharedLibraries); no separate FFmpeg installation is needed.

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

## Feedback and community

- [Report an issue](https://github.com/nilaoda/TSCutter.GUI/issues)
- [Discord community](https://discord.gg/qG7kk9GQ44)
