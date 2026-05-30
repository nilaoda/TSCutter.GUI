# TSCutter.GUI
English | [中文版](./README_CN.md)

TSCutter.GUI is a lightweight tool designed to efficiently cut TS (Transport Stream) video files. It allows users to quickly navigate through keyframes and extract specific segments without modifying the original data.

> The software is still under development and has not been officially released, so it may contain **MANY BUGS**.  

## Features
- **Keyframe Navigation**: Jump to the next, previous, or any specific keyframe.
- **Keyframe-level precise cutting**: Extract video segments based on keyframes, ensuring no data loss or modification.
- **Multi-Platform Support**: Available for Windows, Linux, and macOS.
- **High Performance**: Leveraging direct binary data copying for maximum efficiency.

## FFmpeg Runtime
Official release packages bundle the required **FFmpeg 7.1.3** shared libraries. End users do not need to install FFmpeg manually.

Bundled runtime source: [nilaoda/FFmpegSharedLibraries](https://github.com/nilaoda/FFmpegSharedLibraries/releases/latest).

> **macOS**: If the app is blocked by quarantine, run `xattr -dr com.apple.quarantine TSCutter.GUI.app`.

<details>
<summary>Building from source</summary>

If you are building from source without the bundled runtimes, a compatible FFmpeg 7 installation is required.

- **macOS**: `brew install ffmpeg@7`
- **Linux (Ubuntu 22.04)**: `sudo add-apt-repository ppa:ubuntuhandbook1/ffmpeg7 && sudo apt update && sudo apt install ffmpeg`

On macOS, the app automatically probes common Homebrew locations. If your FFmpeg 7 lives elsewhere, set `FFmpegRootPath` in `~/Library/Application Support/TSCutter.GUI/config.json` to the FFmpeg root directory or its `lib` directory.

</details>

## Screen
![img](img/SS1.png)

## Usage

1. Launch the application.
2. Load a TS file, or Drop.
3. Use the keyframe navigation buttons to find your desired start and end points.
4. Click "Save" to extract the selected segment.

## Thanks
This project is inspired by an excellent editing software called [VidePub](https://sourceforge.net/projects/videpub/).

## License
This project is licensed under the GPL-3.0 License.
