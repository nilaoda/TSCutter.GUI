#!/usr/bin/env python3
"""生成跨平台的关键帧集成测试素材，输出目录已被 Git 忽略。"""
import argparse
import json
from pathlib import Path
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", nargs="?", type=Path,
                        default=Path(__file__).resolve().parents[1] / "artifacts/test-media/keyframes")
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument("--ffprobe", default="ffprobe")
    args = parser.parse_args()
    root = args.directory.resolve()
    root.mkdir(parents=True, exist_ok=True)
    manifest = root / "manifest.json"
    manifest.unlink(missing_ok=True)

    def ffmpeg(*values):
        subprocess.run([args.ffmpeg, "-hide_banner", "-loglevel", "error", "-y", *map(str, values)], check=True)

    def h264(name, rate, duration, gop, *extra):
        ffmpeg("-f", "lavfi", "-i", f"testsrc2=size=128x72:rate={rate}:duration={duration}",
               "-an", "-c:v", "libx264", "-preset", "veryfast", "-crf", 35,
               "-g", gop, "-keyint_min", 1, "-sc_threshold", 0, "-bf", 3,
               "-pix_fmt", "yuv420p", *extra, root / name)

    h264("fixed.mp4", 12, 12, 12)
    ffmpeg("-i", root / "fixed.mp4", "-c", "copy", "-output_ts_offset", 20, root / "fixed.ts")
    # 使用隔行 H.264 覆盖前 10/20 的快速跳转，不需要生成真实的 1080 行画面。
    h264("interlaced.mp4", 25, 32, 25, "-flags", "+ildct+ilme", "-x264-params", "tff=1")
    ffmpeg("-i", root / "interlaced.mp4", "-c", "copy", "-output_ts_offset", 20, root / "interlaced.ts")
    h264("fractional.mp4", "30000/1001", 16, 30)
    # 保留很大的非零时间戳，覆盖广播 TS 中的 PTS 换算。
    ffmpeg("-i", root / "fractional.mp4", "-c", "copy", "-output_ts_offset", 41852, root / "fractional.ts")
    keyframes = [0, 1, 2, 3, 8] + [8 + index / 25 for index in range(1, 50)] + [10, 11]
    h264("variable.mp4", 25, 12, 1000, "-force_key_frames", ",".join(map(str, keyframes)))
    ffmpeg("-i", root / "fixed.mp4", "-c", "copy", "-frames:v", 1, root / "single.mp4")
    h264("short.mp4", 12, 0.25, 12)

    # 使用较短的 POC 周期和 CRA，覆盖跨回绕的连续扫描；不包含真实节目画面。
    ffmpeg("-f", "lavfi", "-i", "testsrc2=size=128x72:rate=50:duration=24",
           "-an", "-c:v", "libx265", "-preset", "ultrafast", "-crf", 38,
           "-x265-params", "keyint=32:min-keyint=32:scenecut=0:open-gop=1:log2-max-poc-lsb=6:repeat-headers=1:pools=1:frame-threads=1:log-level=error:colorprim=bt2020:transfer=arib-std-b67:colormatrix=bt2020nc",
           "-pix_fmt", "yuv420p10le", "-color_primaries", "bt2020", "-color_trc", "arib-std-b67",
           "-colorspace", "bt2020nc", root / "hevc.mp4")
    ffmpeg("-i", root / "hevc.mp4", "-c", "copy", "-output_ts_offset", 41852, root / "hevc-hlg.ts")

    for path in sorted(root.iterdir()):
        if path.suffix not in (".mp4", ".ts"):
            continue
        probe = subprocess.run([args.ffprobe, "-v", "error", "-select_streams", "v:0", "-show_frames",
                                "-show_entries", "frame=key_frame,best_effort_timestamp_time", "-of", "json", str(path)],
                               check=True, capture_output=True, text=True, encoding="utf-8")
        frames = json.loads(probe.stdout)["frames"]
        times = sorted(float(frame["best_effort_timestamp_time"]) for frame in frames if frame["key_frame"])
        if not times:
            raise RuntimeError(f"No decoded keyframes: {path}")
        origin = times[0]
        expected = [round(time - origin, 6) for time in times]
        Path(str(path) + ".json").write_text(json.dumps(expected, indent=2) + "\n", encoding="utf-8")
        print(f"{path.name}: {path.stat().st_size} bytes, {len(expected)} keyframes")

    manifest.write_text(json.dumps({"formatVersion": 1}, indent=2) + "\n", encoding="utf-8")

if __name__ == "__main__":
    main()
