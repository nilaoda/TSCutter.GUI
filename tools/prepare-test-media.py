#!/usr/bin/env python3
"""准备全部或指定组的合成测试素材；Windows、macOS、Linux 使用相同入口。"""
import argparse
from pathlib import Path
import shutil
import subprocess
import sys


GROUPS = {
    "keyframes": "generate-keyframe-test-samples.py",
    "remux": "generate-remux-export-samples.py",
    "subtitles": "generate-subtitle-preview-samples.py",
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--group", nargs="+", choices=GROUPS, default=list(GROUPS))
    parser.add_argument("--output-dir", type=Path,
                        default=Path(__file__).resolve().parents[1] / "artifacts/test-media")
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument("--ffprobe", default="ffprobe")
    parser.add_argument("--font", type=Path, help="可选：字幕附件测试使用的 TTF/OTF 字体")
    args = parser.parse_args()
    for executable in [args.ffmpeg] + ([args.ffprobe] if "keyframes" in args.group else []):
        if shutil.which(executable) is None:
            parser.error(f"找不到 {executable}；请将 FFmpeg CLI 加入 PATH，或使用 --ffmpeg/--ffprobe 指定程序。")
    encoders = subprocess.run([args.ffmpeg, "-hide_banner", "-encoders"], check=True,
                              capture_output=True, text=True, encoding="utf-8").stdout
    required = {"libx264"}
    if any(group in args.group for group in ("keyframes", "remux")):
        required.add("libx265")
    if "subtitles" in args.group:
        required.add("dvbsub")
    missing = sorted(name for name in required if name not in encoders)
    if missing:
        parser.error("FFmpeg CLI 缺少编码器：" + ", ".join(missing))
    scripts = Path(__file__).resolve().parent
    for group in dict.fromkeys(args.group):
        command = [sys.executable, str(scripts / GROUPS[group]), str(args.output_dir.resolve() / group),
                   "--ffmpeg", args.ffmpeg]
        if group == "keyframes":
            command += ["--ffprobe", args.ffprobe]
        if group == "subtitles" and args.font:
            command += ["--font", str(args.font.resolve())]
        print(f"Preparing {group}...", flush=True)
        # 参数逐项传递，不依赖 Bash、PowerShell、盘符或 shell 转义。
        subprocess.run(command, check=True)
    print("Ready. Run: dotnet test src/TSCutter.GUI.Tests -c Debug", flush=True)


if __name__ == "__main__":
    main()
