#!/usr/bin/env python3
"""生成跨平台的关键帧集成测试素材，输出目录已被 Git 忽略。"""
import argparse
import json
from pathlib import Path
import subprocess
import tempfile


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

    # 每 2 秒一个 IDR，段内场景切换处为 CRA；时间分层的分级 B 帧使重排序深度达到 4。
    # 只送关键包解码时，解码器若不在关键帧之间重置，会积压多帧才输出，CRA 还会引用没有送入的参考帧。
    # 各段单独编码使每段以 IDR 开始，再按时间戳拼接；画面在几种测试图之间切换，制造段内的场景切换。
    with tempfile.TemporaryDirectory() as temporary:
        segments = []
        for index in range(8):
            segment = Path(temporary) / f"segment{index}.mp4"
            ffmpeg("-f", "lavfi", "-i", "testsrc2=size=640x360:rate=50:duration=1.3",
                   "-f", "lavfi", "-i", "color=c=0x2060c0:size=640x360:rate=50:duration=0.9",
                   "-f", "lavfi", "-i", "smptehdbars=size=640x360:rate=50:duration=1.1",
                   "-f", "lavfi", "-i", "rgbtestsrc=size=640x360:rate=50:duration=0.7",
                   "-filter_complex", "[0][1][2][3]concat=n=4:v=1:a=0,format=yuv420p,"
                   "loop=loop=-1:size=200:start=0,setpts=N/50/TB,"
                   f"trim=start_frame={index * 100}:end_frame={index * 100 + 100},setpts=PTS-STARTPTS[v]",
                   "-map", "[v]", "-an", "-c:v", "libx265", "-preset", "medium", "-crf", 32,
                   "-x265-params", "keyint=1000:min-keyint=1:scenecut=40:open-gop=1:temporal-layers=5:bframes=15:"
                   "b-adapt=0:rc-lookahead=20:repeat-headers=1:pools=1:frame-threads=1:log-level=error",
                   segment)
            segments.append(segment)
        listing = Path(temporary) / "segments.txt"
        listing.write_text("".join(f"file '{segment.as_posix()}'\n" for segment in segments), encoding="utf-8")
        ffmpeg("-f", "concat", "-safe", 0, "-i", listing, "-c", "copy", "-bsf:v", "hevc_mp4toannexb",
               root / "hevc-mixed-irap.ts")

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
