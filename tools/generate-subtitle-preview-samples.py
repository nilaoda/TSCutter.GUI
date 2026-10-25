#!/usr/bin/env python3
"""生成字幕预览测试素材，默认输出到已被 Git 忽略的 artifacts/test-media/subtitles。"""
import argparse
import json
from pathlib import Path
import subprocess
from pgs_sample import write_pgs_sample


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", nargs="?", type=Path,
                        default=Path(__file__).resolve().parents[1] / "artifacts/test-media/subtitles")
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument("--font", type=Path)
    args = parser.parse_args()
    root = args.directory.resolve()
    root.mkdir(parents=True, exist_ok=True)
    manifest = root / "manifest.json"
    manifest.unlink(missing_ok=True)

    def ffmpeg(*values):
        subprocess.run([args.ffmpeg, "-hide_banner", "-loglevel", "error", "-y", *map(str, values)], check=True)

    (root / "events.srt").write_text(
        "1\n00:00:01,000 --> 00:00:05,000\nLong preview subtitle\n\n"
        "2\n00:00:05,500 --> 00:00:07,500\nSecond subtitle\n", encoding="utf-8")
    (root / "chinese.srt").write_text(
        "1\n00:00:01,000 --> 00:00:05,000\n中文字幕预览\n", encoding="utf-8")
    (root / "animated.ass").write_text(
        "[Script Info]\nScriptType: v4.00+\nPlayResX: 320\nPlayResY: 180\n"
        "[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, "
        "Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, "
        "Alignment, MarginL, MarginR, MarginV, Encoding\n"
        "Style: Default,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,1,0,4,0,0,0,1\n"
        "[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
        "Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,{\\move(30,60,130,60,0,3000)}Animated\n", encoding="utf-8")
    ffmpeg("-f", "lavfi", "-i", "color=c=black:s=320x180:r=10:d=8", "-c:v", "libx264", "-g", 10,
           "-pix_fmt", "yuv420p", root / "video.mp4")
    ffmpeg("-i", root / "video.mp4", "-i", root / "events.srt", "-i", root / "chinese.srt",
           "-map", "0:v", "-map", "1:s", "-map", "2:s", "-c:v", "copy", "-c:s", "mov_text",
           "-metadata:s:s:0", "language=eng", "-metadata:s:s:0", "handler_name=English preview",
           "-metadata:s:s:1", "language=zho", "-metadata:s:s:1", "handler_name=简体中文",
           root / "text.mp4")
    ffmpeg("-i", root / "video.mp4", "-map", "0:v", "-map", "0:v", "-c", "copy", root / "multi-video.mp4")
    attach = []
    if args.font:
        attach = ["-attach", args.font, "-metadata:s:t", "mimetype=application/x-truetype-font",
                  "-metadata:s:t", "filename=preview-font.ttf"]
    ffmpeg("-i", root / "video.mp4", "-i", root / "animated.ass", "-map", "0:v", "-map", "1:s",
           "-c", "copy", *attach, "-metadata:s:s", "title=Animated ASS", root / "animated.mkv")

    # 合成 PGS 字幕、缓存复用和清屏事件，无需外部影视素材。
    write_pgs_sample(root / "bitmap.sup")
    ffmpeg("-copyts", "-i", root / "video.mp4", "-i", root / "bitmap.sup", "-map", "0:v", "-map", "1:s",
           "-c", "copy", root / "bitmap.mkv")
    ffmpeg("-fix_sub_duration", "-i", root / "bitmap.mkv", "-map", "0:v", "-map", "0:s", "-c:v", "copy", "-c:s", "dvbsub",
           "-f", "mpegts", root / "bitmap.ts")
    # 长视频中包含稀疏及空字幕，验证局部加载不会读到文件末尾。
    header = (root / "animated.ass").read_text(encoding="utf-8").split("Dialogue:")[0]
    (root / "windowed.ass").write_text(header +
        "Dialogue: 0,0:00:20.00,0:00:24.00,Default,,0,0,0,,Backward preview\n" +
        "Dialogue: 0,0:00:29.00,0:00:35.00,Default,,0,0,0,,Cache edge preview\n" +
        "Dialogue: 0,0:01:00.00,0:01:04.00,Default,,0,0,0,,Middle preview\n" +
        "Dialogue: 0,0:02:00.00,0:02:04.00,Default,,0,0,0,,Later preview\n", encoding="utf-8")
    (root / "empty.ass").write_text(header, encoding="utf-8")
    ffmpeg("-f", "lavfi", "-i", "color=c=black:s=128x72:r=5:d=150",
           "-i", root / "windowed.ass", "-i", root / "empty.ass", "-i", root / "bitmap.sup",
           "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=10",
           "-map", "0:v", "-map", "1:s", "-map", "2:s", "-map", "3:s", "-map", "4:a",
           "-c:v", "libx264", "-g", 10, "-pix_fmt", "yuv420p", "-c:s", "copy", "-c:a", "aac",
           root / "windowed.mkv")
    manifest.write_text(json.dumps({"formatVersion": 1}, indent=2) + "\n", encoding="utf-8")
    print(root)


if __name__ == "__main__":
    main()
