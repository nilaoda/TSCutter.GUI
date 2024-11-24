# TSCutter.GUI
English | [中文版](./README_CN.md)

TSCutter.GUI is a lightweight tool designed to efficiently cut TS (Transport Stream) video files. It allows users to quickly navigate through keyframes and extract specific segments without modifying the original data.

> The software is still under development and has not been officially released, so it may contain **MANY BUGS**.  

## Features
- **Keyframe Navigation**: Jump to the next, previous, or any specific keyframe.
- **Keyframe-level precise cutting**: Extract video segments based on keyframes, ensuring no data loss or modification.
- **Multi-Platform Support**: Available for Windows, Linux, and macOS.
- **High Performance**: Leveraging direct binary data copying for maximum efficiency.

## Prerequisites
This software depends on the dynamic libraries of **FFmpeg 7.0**.
### Windows
Run `.exe` directly. Bundled libraries: [Sdcb.FFmpeg.runtime.windows-x64](https://www.nuget.org/packages/Sdcb.FFmpeg.runtime.windows-x64/7.0.0)
### Linux
Installing FFmpeg on Linux depends on the distribution you are using.

On Ubuntu 22.04:
```bash
sudo add-apt-repository ppa:ubuntuhandbook1/ffmpeg7
sudo apt update
sudo apt install ffmpeg
```
### macOS
```bash
brew install ffmpeg
```

<details>
<summary>more</summary>

If the program crashes, you may need to manually create a symbolic link to ensure the program works properly:

```
sudo mkdir /usr/local/lib

sudo ln -s /opt/homebrew/Cellar/ffmpeg/7.1_3/lib/libavcodec.61.19.100.dylib /usr/local/lib/libavcodec.61.dylib
sudo ln -s /opt/homebrew/Cellar/ffmpeg/7.1_3/lib/libavdevice.61.3.100.dylib /usr/local/lib/libavdevice.61.dylib
sudo ln -s /opt/homebrew/Cellar/ffmpeg/7.1_3/lib/libavfilter.10.4.100.dylib /usr/local/lib/libavfilter.10.dylib
sudo ln -s /opt/homebrew/Cellar/ffmpeg/7.1_3/lib/libavformat.61.7.100.dylib /usr/local/lib/libavformat.61.dylib
sudo ln -s /opt/homebrew/Cellar/ffmpeg/7.1_3/lib/libavutil.59.39.100.dylib /usr/local/lib/libavutil.59.dylib
sudo ln -s /opt/homebrew/Cellar/ffmpeg/7.1_3/lib/libpostproc.58.3.100.dylib /usr/local/lib/libpostproc.58.dylib
sudo ln -s /opt/homebrew/Cellar/ffmpeg/7.1_3/lib/libswresample.5.3.100.dylib /usr/local/lib/libswresample.5.dylib
sudo ln -s /opt/homebrew/Cellar/ffmpeg/7.1_3/lib/libswscale.8.3.100.dylib /usr/local/lib/libswscale.8.dylib

echo 'export DYLD_LIBRARY_PATH=/usr/local/lib:$DYLD_LIBRARY_PATH' >> ~/.zshrc
source ~/.zshr
```

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
This project is licensed under the LGPL-3.0 License.
