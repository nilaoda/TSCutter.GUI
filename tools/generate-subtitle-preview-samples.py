#!/usr/bin/env python3
"""生成字幕预览测试素材，默认输出到已被 Git 忽略的 artifacts/test-media/subtitles。"""
import argparse
import json
from pathlib import Path
import struct
import subprocess


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

    # 合成一幅白色 PGS 字幕及清屏事件，无需引入外部影视字幕素材。
    def segment(kind, data, time):
        return b"PG" + struct.pack(">IIBH", time * 90000, 0, kind, len(data)) + data

    def presentation(number, count, state):
        header = struct.pack(">HHBHBBBB", 320, 180, 0x10, number, state, 0, 0, count)
        return header + (struct.pack(">HBBHH", 0, 0, 0, 20, 40) if count else b"")

    rle = (b"\x01" * 32 + b"\0\0") * 16
    obj = struct.pack(">HBB", 0, 0, 0xC0) + (len(rle) + 4).to_bytes(3, "big") + struct.pack(">HH", 32, 16) + rle
    pgs = segment(0x16, presentation(0, 1, 0x80), 1)
    pgs += segment(0x17, struct.pack(">BBHHHH", 1, 0, 20, 40, 32, 16), 1)
    pgs += segment(0x14, bytes([0, 0, 0, 16, 128, 128, 0, 1, 235, 128, 128, 255]), 1)
    pgs += segment(0x15, obj, 1) + segment(0x80, b"", 1)
    pgs += segment(0x16, presentation(1, 0, 0), 3) + segment(0x80, b"", 3)
    (root / "bitmap.sup").write_bytes(pgs)
    ffmpeg("-copyts", "-i", root / "video.mp4", "-i", root / "bitmap.sup", "-map", "0:v", "-map", "1:s",
           "-c", "copy", root / "bitmap.mkv")
    ffmpeg("-fix_sub_duration", "-i", root / "bitmap.mkv", "-map", "0:v", "-map", "0:s", "-c:v", "copy", "-c:s", "dvbsub",
           "-f", "mpegts", root / "bitmap.ts")
    manifest.write_text(json.dumps({"formatVersion": 1}, indent=2) + "\n", encoding="utf-8")
    print(root)


if __name__ == "__main__":
    main()
