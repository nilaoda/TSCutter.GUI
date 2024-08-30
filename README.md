# TSCutter.GUI
Cross-Platform MPEG2-TS Cutter. Based on ffmpeg.

The software is still under development and has not been officially released, so it may contain **MANY BUGS**.  

# Screen
![img](img/SS1.png)

# Prerequisites
This software depends on the dynamic libraries of FFmpeg.
### Windows
Run `.exe` directly.
### Linux
```plain
sudo apt install ffmpeg         [On Debian, Ubuntu and Mint]
sudo yum install ffmpeg         [On RHEL/CentOS/Fedora and Rocky/AlmaLinux]
sudo emerge -a sys-apps/ffmpeg  [On Gentoo Linux]
sudo apk add ffmpeg             [On Alpine Linux]
sudo pacman -S ffmpeg           [On Arch Linux]
sudo zypper install ffmpeg      [On OpenSUSE]    
sudo pkg install ffmpeg         [On FreeBSD]
```
[ref.](https://www.tecmint.com/install-ffmpeg-in-linux/)
### macOS
```bash
brew install ffmpeg
```

# Thanks
This project is inspired by an excellent editing software called [VidePub](https://sourceforge.net/projects/videpub/).
