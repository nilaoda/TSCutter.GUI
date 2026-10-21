#!/usr/bin/env python3
"""使用 ffmpeg 为 RemuxExportServiceTests 生成小型合成测试素材。

用法：python3 tools/generate-remux-export-samples.py /tmp/tscutter-media-fixtures
运行 dotnet test 时，将 TSCUTTER_REMUX_TEST_SAMPLES 设置为上述目录。
"""
import argparse
from pathlib import Path
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--ffmpeg", default="ffmpeg")
    args = parser.parse_args()
    root = args.directory.resolve()
    root.mkdir(parents=True, exist_ok=True)
    (root / "events.srt").write_text(
        "1\n00:00:01,000 --> 00:00:05,000\nSpanning subtitle\n\n"
        "2\n00:00:05,500 --> 00:00:07,500\nSecond subtitle\n", encoding="utf-8")
    (root / "chapters.txt").write_text(
        ";FFMETADATA1\ntitle=Export regression\nartist=Test artist\n"
        "[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=3000\ntitle=First chapter\n"
        "[CHAPTER]\nTIMEBASE=1/1000\nSTART=3000\nEND=8000\ntitle=Second chapter\n", encoding="utf-8")

    def ffmpeg(*values):
        subprocess.run([args.ffmpeg, "-hide_banner", "-loglevel", "error", "-y", *map(str, values)], check=True)

    ffmpeg("-f", "lavfi", "-i", "testsrc2=size=128x72:rate=25:duration=8",
           "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=8",
           "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=8",
           "-i", root / "events.srt", "-f", "ffmetadata", "-i", root / "chapters.txt",
           "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:s", "-map_metadata", "4", "-map_chapters", "4",
           "-c:v", "libx264", "-g", "50", "-keyint_min", "50", "-sc_threshold", "0", "-bf", "2",
           "-c:a", "aac", "-c:s", "mov_text", "-metadata:s:a:0", "language=eng", "-metadata:s:a:0", "title=English",
           "-metadata:s:a:1", "language=jpn", "-metadata:s:a:1", "title=Japanese", "-metadata:s:s:0", "language=zho", "-metadata:s:s:0", "title=简体中文字幕",
           "-disposition:a:0", "default", "-disposition:a:1", "0", "-disposition:s:0", "forced", root / "multi.mp4")
    ffmpeg("-i", root / "multi.mp4", "-map", "0:v", "-map", "0:a", "-map", "0:s", "-c", "copy", "-c:s", "ass",
           "-attach", root / "events.srt", "-metadata:s:t", "mimetype=text/plain", "-metadata:s:t", "filename=events.srt",
           root / "multi.mkv")
    ffmpeg("-i", root / "multi.mp4", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=10",
           "-map", "0:v:0", "-map", "1:a", "-map_metadata", "0", "-map_chapters", "0",
           "-c:v", "copy", "-c:a", "aac", "-metadata:s:a:0", "language=eng",
           "-metadata:s:a:0", "title=English", root / "long-audio.mp4")
    (root / "animated.ass").write_text(
        "[Script Info]\nScriptType: v4.00+\nPlayResX: 128\nPlayResY: 72\n"
        "[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, "
        "Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n"
        "Style: Default,Arial,12,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,1,0,2,5,5,5,1\n"
        "[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
        "Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,{\\fad(500,500)}Animated subtitle\n", encoding="utf-8")
    ffmpeg("-i", root / "multi.mp4", "-i", root / "animated.ass", "-map", "0:v:0", "-map", "0:a:0", "-map", "1:s:0",
           "-c", "copy", root / "animated.mkv")
    ffmpeg("-f", "lavfi", "-i", "testsrc2=size=128x72:rate=25:duration=4", "-pix_fmt", "yuv420p10le",
           "-c:v", "libx265", "-preset", "ultrafast", "-color_primaries", "bt2020", "-color_trc", "smpte2084",
           "-colorspace", "bt2020nc", "-x265-params",
           "pools=1:log-level=error:keyint=50:min-keyint=50:scenecut=0:open-gop=0:"
           "colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1):max-cll=1000,400",
           "-tag:v", "hvc1", root / "hdr.mp4")
    ffmpeg("-f", "lavfi", "-i", "testsrc2=size=128x72:rate=25:duration=6", "-c:v", "libx265",
           "-preset", "ultrafast", "-x265-params",
           "pools=1:log-level=error:keyint=50:min-keyint=50:scenecut=0:open-gop=1:bframes=4",
           root / "open-gop.mp4")
    ffmpeg("-f", "lavfi", "-i", "color=c=blue:size=64x64", "-frames:v", "1", root / "cover.jpg")
    ffmpeg("-i", root / "multi.mp4", "-i", root / "cover.jpg", "-map", "0:v:0", "-map", "0:a", "-map", "0:s", "-map", "1:v",
           "-c", "copy", "-disposition:v:1", "attached_pic",
           "-metadata:s:a:0", "title=English", "-metadata:s:a:1", "title=Japanese",
           "-metadata:s:s:0", "title=简体中文字幕", root / "cover.mp4")
    print(root)


if __name__ == "__main__":
    main()
