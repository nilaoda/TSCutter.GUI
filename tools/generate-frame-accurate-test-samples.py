#!/usr/bin/env python3
"""生成帧精确剪辑集成测试素材，输出目录已被 Git 忽略。

每帧顶部 16 行用 16 个亮/暗块编码帧序号，重新编码后仍可可靠识别。
"""
import argparse
import json
from pathlib import Path
import subprocess

# 亮块表示 1，暗块表示 0；低位在左。其余画面保留 testsrc2 的运动内容。
FRAME_NUMBER = ("geq=lum='if(lt(Y,16),16+219*mod(floor(N/pow(2,floor(X/16))),2),lum(X,Y))'"
                ":cb='cb(X,Y)':cr='cr(X,Y)'")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", nargs="?", type=Path,
                        default=Path(__file__).resolve().parents[1] / "artifacts/test-media/frameaccurate")
    parser.add_argument("--ffmpeg", default="ffmpeg")
    args = parser.parse_args()
    root = args.directory.resolve()
    root.mkdir(parents=True, exist_ok=True)
    manifest = root / "manifest.json"
    manifest.unlink(missing_ok=True)

    def ffmpeg(*values):
        subprocess.run([args.ffmpeg, "-hide_banner", "-loglevel", "error", "-y", *map(str, values)], check=True)

    def inputs(rate, duration, size="320x180"):
        return ["-f", "lavfi", "-i", f"testsrc2=size={size}:rate={rate}:duration={duration}",
                "-f", "lavfi", "-i", f"sine=frequency=440:sample_rate=48000:duration={duration}"]

    def encode(*video_options):
        return ["-vf", f"format=yuv420p,{FRAME_NUMBER}", "-c:v", "libx264", "-preset", "veryfast", "-crf", 23,
                "-pix_fmt", "yuv420p", *video_options, "-c:a", "aac", "-b:a", "96k"]

    # 闭合 GOP、3 个 B 帧、分数帧率；TS 带很大的非零 PTS，MP4 带文字字幕；M2TS/MKV 为同一码流。
    closed = ["-g", 30, "-keyint_min", 30, "-sc_threshold", 0, "-bf", 3]
    ffmpeg(*inputs("30000/1001", 12), *encode(*closed), "-output_ts_offset", 41852, root / "h264.ts")
    ffmpeg("-i", root / "h264.ts", "-map", 0, "-c", "copy", "-mpegts_m2ts_mode", 1, root / "h264.m2ts")
    ffmpeg("-i", root / "h264.ts", "-map", 0, "-c", "copy", "-output_ts_offset", 0, root / "h264.mkv")
    # 33 位 PTS 约在第 3.7 秒回绕。
    ffmpeg(*inputs(25, 8), *encode("-g", 25, "-keyint_min", 25, "-sc_threshold", 0, "-bf", 2),
           "-output_ts_offset", 95440, root / "wrap.ts")
    subtitles = root / "cues.srt"
    subtitles.write_text("1\n00:00:01,000 --> 00:00:03,000\nfirst\n\n"
                         "2\n00:00:05,500 --> 00:00:07,000\nsecond\n\n", encoding="utf-8")
    ffmpeg(*inputs("30000/1001", 12), "-i", subtitles, "-map", 0, "-map", 1, "-map", 2,
           *encode(*closed), "-c:s", "mov_text", root / "h264.mp4")
    subtitles.unlink()
    # HEVC 10 位：IDR_W_RADL 带 RADL 前导画面（解码在 IDR 后、显示在前）；TS 为 HLG，MP4 为 HDR10 静态元数据。
    hevc = ("keyint=25:min-keyint=25:scenecut=0:open-gop=0:bframes=3:radl=2:repeat-headers=1:"
            "pools=1:frame-threads=1:log-level=error")
    def x265(*extra):
        return ["-vf", f"format=yuv420p,{FRAME_NUMBER},format=yuv420p10le", "-c:v", "libx265", "-preset", "ultrafast",
                "-crf", 26, *extra, "-c:a", "aac", "-b:a", "96k"]
    ffmpeg(*inputs(25, 8), *x265("-x265-params", hevc + ":colorprim=bt2020:transfer=arib-std-b67:colormatrix=bt2020nc",
                                 "-color_primaries", "bt2020", "-color_trc", "arib-std-b67", "-colorspace", "bt2020nc"),
           "-output_ts_offset", 600, root / "hevc-hlg.ts")
    ffmpeg(*inputs(25, 8), *x265("-x265-params", hevc + ":colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:hdr10=1:"
                                 "master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,50):"
                                 "max-cll=1000,400",
                                 "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc",
                                 "-tag:v", "hvc1"), root / "hevc-hdr10.mp4")
    # HEVC 格式范围扩展（RExt）4:2:2：10 位 TS（带 RADL 前导画面）与 8 位 MP4（hvc1），均为 Main 4:2:2 10 档次。
    def x265_422(pix_fmt):
        return ["-vf", f"format=yuv420p,{FRAME_NUMBER},format={pix_fmt}", "-c:v", "libx265", "-preset", "ultrafast",
                "-crf", 26, "-profile:v", "main422-10", "-x265-params", hevc, "-c:a", "aac", "-b:a", "96k"]
    ffmpeg(*inputs(25, 8), *x265_422("yuv422p10le"), "-output_ts_offset", 700, root / "hevc-422.ts")
    ffmpeg(*inputs(25, 8), *x265_422("yuv422p"), "-tag:v", "hvc1", root / "hevc-422-8bit.mp4")
    # HEVC 开放 GOP（与常见 4K 广播一致）：除开头外没有 IDR，关键帧是 CRA，其后有参考上一 GOP 的 RASL 前导画面
    # （RASL_R 与 RASL_N）。medium 预设的自适应 B 帧才会把关键帧前的 B 帧放到关键帧之后解码；MP4 为同一码流（hvc1）。
    cra = ("keyint=25:min-keyint=25:scenecut=0:open-gop=1:bframes=3:b-adapt=2:radl=0:repeat-headers=1:"
           "pools=1:frame-threads=1:log-level=error")
    ffmpeg(*inputs(25, 8), "-vf", f"format=yuv420p,{FRAME_NUMBER},format=yuv420p10le", "-c:v", "libx265",
           "-preset", "medium", "-crf", 26, "-x265-params", cra, "-c:a", "aac", "-b:a", "96k",
           "-output_ts_offset", 800, root / "hevc-cra.ts")
    ffmpeg("-i", root / "hevc-cra.ts", "-map", 0, "-c", "copy", "-tag:v", "hvc1", root / "hevc-cra.mp4")
    # 从 IDR 处无重编码截取：文件以 IDR 开头，其 RADL 前导画面显示在它之前（与关键帧剪辑导出的广播片段一致）。
    ffmpeg("-ss", 2.1, "-i", root / "hevc-hlg.ts", "-map", 0, "-c", "copy", root / "hevc-leading-start.ts")
    # 开放 GOP（B 帧金字塔）：后续关键帧是普通 I 帧，前导画面中有作参考的 B 帧，衔接时丢弃并把其后画面重新编号。
    ffmpeg(*inputs(25, 8), *encode("-g", 25, "-keyint_min", 25, "-sc_threshold", 0, "-bf", 3,
                                   "-x264-params", "open-gop=1"), root / "open-gop.ts")
    # 与常见广播一致的开放 GOP：普通 I 帧（无 IDR）、前导 B 帧不作参考，衔接时 I 帧改写为 IDR。
    # MP4 为同一码流（长度前缀）；另有 CAVLC（切片数据不字节对齐）与隔行 MBAFF（上场优先）两种。
    flat = "open-gop=1:b-pyramid=none"
    ffmpeg(*inputs(25, 8), *encode("-g", 25, "-keyint_min", 25, "-sc_threshold", 0, "-bf", 3,
                                   "-x264-params", flat), root / "open-gop-flat.ts")
    ffmpeg("-i", root / "open-gop-flat.ts", "-map", 0, "-c", "copy", root / "open-gop-flat.mp4")
    ffmpeg(*inputs(25, 8), *encode("-g", 25, "-keyint_min", 25, "-sc_threshold", 0, "-bf", 3, "-coder", "cavlc",
                                   "-x264-params", flat), root / "open-gop-cavlc.ts")
    ffmpeg(*inputs(25, 8, "320x192"), *encode("-g", 12, "-keyint_min", 12, "-sc_threshold", 0, "-bf", 2,
                                              "-flags", "+ildct+ilme", "-x264-params", flat + ":tff=1"),
           root / "open-gop-mbaff.ts")
    ffmpeg(*inputs(25, 4, "320x192"), *encode("-g", 25, "-flags", "+ildct+ilme", "-x264-params", "tff=1"),
           "-output_ts_offset", 20, root / "interlaced.ts")
    # MPEG-2：开放 GOP（I 后的前导 B 帧参考上一 GOP）、隔行上场优先、序列头带 VBV 参数，与常见广播一致。
    ffmpeg(*inputs(25, 10, "352x288"), "-vf", f"format=yuv420p,{FRAME_NUMBER},setfield=tff", "-c:v", "mpeg2video",
           "-b:v", "4M", "-maxrate", "6M", "-bufsize", "1835k", "-g", 24, "-bf", 3, "-sc_threshold", 1000000000,
           "-flags", "+ilme+ildct", "-c:a", "mp2", "-b:a", "192k", "-output_ts_offset", 30, root / "mpeg2.ts")
    # MPEG-2 4:2:2 档次（广播制作与交换常用）：隔行下场优先的 Main 级别（标清尺寸），以及逐行的 High 级别（高清尺寸）。
    ffmpeg(*inputs(25, 8, "352x288"), "-vf", f"format=yuv422p,{FRAME_NUMBER},setfield=bff", "-c:v", "mpeg2video",
           "-b:v", "8M", "-maxrate", "12M", "-bufsize", "3670k", "-g", 15, "-bf", 2, "-sc_threshold", 1000000000,
           "-flags", "+ilme+ildct", "-c:a", "mp2", "-b:a", "192k", "-output_ts_offset", 40, root / "mpeg2-422.ts")
    ffmpeg(*inputs(25, 4, "1280x720"), "-vf", f"format=yuv422p,{FRAME_NUMBER}", "-c:v", "mpeg2video",
           "-b:v", "12M", "-maxrate", "18M", "-bufsize", "7340k", "-g", 12, "-bf", 2, "-sc_threshold", 1000000000,
           "-c:a", "mp2", "-b:a", "192k", root / "mpeg2-422-hl.ts")
    # High 4:4:4 Predictive 档次：用于验证不支持的格式在写文件前被拒绝。
    ffmpeg(*inputs(25, 3), *encode("-g", 25, "-pix_fmt", "yuv444p"), root / "h264-444.mp4")
    # High 4:2:2 档次（与常见广播交换素材一致）：8 位逐行 MP4（闭合 GOP）；
    # 10 位 50p 逐行 TS：开放 GOP、B 帧金字塔（I 帧之后紧跟作参考的前导 B 帧），且 SPS 不声明重排序深度；
    # 8 位隔行 TS：MBAFF 上场优先、开放 GOP、B 帧金字塔。两个 TS 除开头外都没有 IDR。
    ffmpeg(*inputs(25, 3), *encode("-g", 25, "-pix_fmt", "yuv422p"), root / "h264-422.mp4")
    def encode422(pix_fmt, *video_options):
        return ["-vf", f"format=yuv420p,{FRAME_NUMBER},format={pix_fmt}", "-c:v", "libx264", "-preset", "veryfast",
                "-crf", 23, "-profile:v", "high422", *video_options, "-c:a", "aac", "-b:a", "96k"]
    ffmpeg(*inputs(50, 8), *encode422("yuv422p10le", "-g", 50, "-keyint_min", 50, "-sc_threshold", 0, "-bf", 3,
                                      "-x264-params", "open-gop=1:b-pyramid=normal"),
           "-output_ts_offset", 300, root / "h264-422-10bit.ts")
    strip_reorder_depth(root / "h264-422-10bit.ts")
    ffmpeg(*inputs(25, 12, "320x192"), *encode422("yuv422p", "-g", 25, "-keyint_min", 25, "-sc_threshold", 0, "-bf", 3,
                                                  "-flags", "+ildct+ilme",
                                                  "-x264-params", "tff=1:open-gop=1:b-pyramid=normal"),
           "-output_ts_offset", 600, root / "h264-422-interlaced.ts")
    signal_loss(root / "h264-422-interlaced.ts", root / "h264-422-loss.ts")
    # 双节目 TS：节目 1 带帧序号（开放 GOP、只有普通 I 帧），节目 2 为另一组音视频；用于按节目输出。
    ffmpeg(*inputs(25, 8), "-f", "lavfi", "-i", "testsrc=size=320x180:rate=25:duration=8",
           "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=8",
           "-filter_complex", f"[0:v]format=yuv420p,{FRAME_NUMBER}[first];[2:v]format=yuv420p[second]",
           "-map", "[first]", "-map", "1", "-map", "[second]", "-map", "3", "-c:v", "libx264", "-preset", "veryfast",
           "-crf", 23, "-g", 25, "-keyint_min", 25, "-sc_threshold", 0, "-bf", 2, "-x264-params", "open-gop=1:b-pyramid=none",
           "-c:a", "aac", "-b:a", "96k", "-program", "title=First:st=0:st=1", "-program", "title=Second:st=2:st=3",
           "-output_ts_offset", 50, root / "two-programs.ts")

    # AV1（与在线视频站常见格式一致）：每个关键帧带序列头、有隐藏帧（show_existing_frame），时间单元没有重排序。
    # 8 位 MP4（Opus 音频）与同一码流的 MKV；10 位 MKV 由 libaom 编码。
    ffmpeg(*inputs(25, 8), "-vf", f"format=yuv420p,{FRAME_NUMBER}", "-c:v", "libsvtav1", "-preset", 8, "-crf", 30,
           "-g", 25, "-svtav1-params", "scd=0", "-c:a", "libopus", "-b:a", "96k", root / "av1.mp4")
    ffmpeg("-i", root / "av1.mp4", "-map", 0, "-c", "copy", root / "av1.mkv")
    ffmpeg(*inputs(25, 6), "-vf", f"format=yuv420p,{FRAME_NUMBER},format=yuv420p10le", "-c:v", "libaom-av1",
           "-cpu-used", 8, "-crf", 30, "-b:v", 0, "-g", 30, "-keyint_min", 30, "-c:a", "libopus", "-b:a", "96k",
           root / "av1-10bit.mkv")
    # VP9（与在线视频站常见格式一致）：两遍编码，带隐藏的替代参考帧（与下一画面合成超帧），关键帧每秒一个。
    # 8 位 MP4（vp09，Opus 音频）与同一码流的 MKV；10 位（Profile 2）MKV。
    def vp9(duration, pix_fmt, target):
        video = ["-vf", f"format=yuv420p,{FRAME_NUMBER},format={pix_fmt}", "-c:v", "libvpx-vp9", "-deadline", "good",
                 "-cpu-used", 4, "-b:v", "400k", "-g", 25, "-keyint_min", 25, "-passlogfile", root / "vp9-pass"]
        ffmpeg(*inputs(25, duration), *video, "-pass", 1, "-an", "-f", "null", "-")
        ffmpeg(*inputs(25, duration), *video, "-pass", 2, "-auto-alt-ref", 1, "-lag-in-frames", 16,
               "-c:a", "libopus", "-b:a", "96k", target)
        for log in root.glob("vp9-pass*"):
            log.unlink()
    vp9(8, "yuv420p", root / "vp9.mp4")
    ffmpeg("-i", root / "vp9.mp4", "-map", 0, "-c", "copy", root / "vp9.mkv")
    vp9(6, "yuv420p10le", root / "vp9-10bit.mkv")

    # 相机 MOV：PCM 音频不带 chan 原子，声道布局未指定（MP4 的 PCM 必须声明布局）。
    ffmpeg(*inputs(25, 4), *encode("-g", 25), "-c:a", "pcm_s24le", "-ac", 2, root / "pcm-layout.mov")
    without_channel_layout(root / "pcm-layout.mov", root / "pcm-no-layout.mov")

    gradual_refresh(ffmpeg, root)
    # 隔行（MBAFF、上场优先）逐行刷新：POC 类型 0、带 pic_struct，CAVLC、不用 8x8 变换，覆盖头段改写的另一组语法。
    gradual_refresh(ffmpeg, root, "gradual-refresh-mbaff", "320x192", ["setfield=tff"],
                    ["-flags", "+ildct+ilme"], ":tff=1:cabac=0:8x8dct=0")
    mpeg2_refresh(ffmpeg, root)
    mpeg2_refresh_numbered(ffmpeg, root)

    for path in sorted(root.iterdir()):
        print(f"{path.name}: {path.stat().st_size} bytes")
    manifest.write_text(json.dumps({"formatVersion": 1}, indent=2) + "\n", encoding="utf-8")


class BitReader:
    def __init__(self, data):
        self.data, self.position = data, 0

    def bit(self):
        value = (self.data[self.position >> 3] >> (7 - (self.position & 7))) & 1
        self.position += 1
        return value

    def bits(self, count):
        value = 0
        for _ in range(count):
            value = value << 1 | self.bit()
        return value

    def ue(self):
        zeros = 0
        while not self.bit():
            zeros += 1
        return (1 << zeros) - 1 + self.bits(zeros)

    def se(self):
        value = self.ue()
        return (value + 1) // 2 if value & 1 else -(value // 2)


def unescape(nal):
    output, zeros = bytearray(), 0
    for value in nal:
        if zeros >= 2 and value == 3:
            zeros = 0
            continue
        output.append(value)
        zeros = zeros + 1 if value == 0 else 0
    return bytes(output)


def escape(rbsp):
    output, zeros = bytearray(), 0
    for value in rbsp:
        if zeros >= 2 and value <= 3:
            output.append(3)
            zeros = 0
        output.append(value)
        zeros = zeros + 1 if value == 0 else 0
    return bytes(output)


def without_bitstream_restriction(sps):
    """去掉 SPS 的 VUI 码流限制信息（含 max_num_reorder_frames），返回新的 SPS NAL；没有时返回 None。"""
    rbsp = unescape(sps[1:])
    r = BitReader(rbsp)
    profile = r.bits(8)
    r.bits(16)
    r.ue()
    if profile in (100, 110, 122, 244, 44, 83, 86, 118, 128, 138, 139, 134, 135):
        chroma = r.ue()
        if chroma == 3:
            r.bit()
        r.ue(), r.ue(), r.bit()
        if r.bit():
            for index in range(8 if chroma != 3 else 12):
                if r.bit():
                    last = following = 8
                    for _ in range(16 if index < 6 else 64):
                        if following:
                            following = (last + r.se() + 256) % 256
                        last = following or last
    r.ue()
    poc = r.ue()
    if poc == 0:
        r.ue()
    elif poc == 1:
        r.bit(), r.se(), r.se()
        for _ in range(r.ue()):
            r.se()
    r.ue(), r.bit(), r.ue(), r.ue()
    if not r.bit():
        r.bit()
    r.bit()
    if r.bit():
        for _ in range(4):
            r.ue()
    if not r.bit():
        return None
    if r.bit() and r.bits(8) == 255:
        r.bits(32)
    if r.bit():
        r.bit()
    if r.bit():
        r.bits(4)
        if r.bit():
            r.bits(24)
    if r.bit():
        r.ue(), r.ue()
    if r.bit():
        r.bits(32), r.bits(32), r.bit()
    hrd = False
    for _ in range(2):
        if r.bit():
            hrd = True
            count = r.ue() + 1
            r.bits(8)
            for _ in range(count):
                r.ue(), r.ue(), r.bit()
            r.bits(20)
    if hrd:
        r.bit()
    r.bit()
    flag = r.position
    if not (rbsp[flag >> 3] >> (7 - (flag & 7))) & 1:
        return None
    # 码流限制标志置 0，其后直接是 rbsp_trailing_bits。
    bits = "".join(f"{value:08b}" for value in rbsp)[:flag] + "01"
    bits += "0" * (-len(bits) % 8)
    return sps[:1] + escape(bytes(int(bits[i:i + 8], 2) for i in range(0, len(bits), 8)))


def strip_reorder_depth(path):
    """在 TS 中原位改写各 SPS，去掉重排序深度声明（与部分广播编码器一致）。

    缩短的部分以 NAL 之后的 trailing_zero_8bits 补齐，PES 与 TS 包结构不变。
    """
    data = bytearray(path.read_bytes())
    offsets, payload = [], bytearray()
    video_pid = None
    for start in range(0, len(data) - 187, 188):
        packet = data[start:start + 188]
        pid = (packet[1] & 0x1F) << 8 | packet[2]
        control = packet[3] >> 4 & 3
        offset = 4 + (1 + packet[4] if control & 2 else 0)
        if not control & 1 or offset >= 188:
            continue
        if video_pid is None and packet[1] & 0x40 and packet[offset:offset + 3] == b"\x00\x00\x01" and \
                0xE0 <= packet[offset + 3] <= 0xEF:
            video_pid = pid
        if pid != video_pid:
            continue
        offsets.extend(range(start + offset, start + 188))
        payload += packet[offset:]
    changed = 0
    index = payload.find(b"\x00\x00\x01")
    while index >= 0:
        following = payload.find(b"\x00\x00\x01", index + 3)
        if payload[index + 3] & 0x1F == 7:
            end = following if following >= 0 else len(payload)
            while payload[end - 1] == 0:
                end -= 1
            sps = bytes(payload[index + 3:end])
            stripped = without_bitstream_restriction(sps)
            if stripped is not None:
                replacement = stripped + bytes(len(sps) - len(stripped))
                for position, value in enumerate(replacement):
                    data[offsets[index + 3 + position]] = value
                changed += 1
        index = following
    if changed == 0:
        raise RuntimeError(f"{path.name}: no SPS with bitstream restriction")
    path.write_bytes(bytes(data))


def without_channel_layout(source, target):
    """把 MOV 音频的 chan 原子改为 free，声道布局成为未指定（只有声道数）。"""
    data = source.read_bytes()
    if data.count(b"chan") != 1:
        raise RuntimeError("expected one chan atom")
    target.write_bytes(data.replace(b"chan", b"free"))
    source.unlink()


def signal_loss(source, target, outage=90):
    """模拟接收中断：删去 TS 中间约三成的传输包（不按 PES/GOP 对齐，两端都落在 GOP 中间），
    并把中断之后的时间戳（PES 的 PTS/DTS 与 PCR）整体推后 outage 秒，相当于信号中断了更长时间。

    与实际录制的信号丢失一致：中断前最后一个 PES 不完整（解复用器标记损坏），恢复后的画面时间戳向前跳变一分多钟，
    且从 GOP 中间开始，参考帧缺失，到下一个 IDR 前的画面无法完整解码。
    """
    data = source.read_bytes()
    packets = len(data) // 188
    first, last = packets * 2 // 5, packets * 7 // 10
    shift = outage * 90000
    tail = bytearray(data[last * 188:packets * 188])
    for start in range(0, len(tail), 188):
        packet = memoryview(tail)[start:start + 188]
        control = packet[3] >> 4 & 3
        offset = 4
        if control & 2:
            length = packet[4]
            if length > 0 and packet[5] & 0x10:
                # PCR：33 位基值（90 kHz）位于适配字段标志之后。
                base = int.from_bytes(packet[6:11], "big") >> 7
                base = (base + shift) % (1 << 33)
                packet[6:11] = ((base << 7) | (packet[10] & 0x7F)).to_bytes(5, "big")
            offset = 5 + length
        if not control & 1 or not packet[1] & 0x40 or offset + 14 > 188 or packet[offset:offset + 3] != b"\x00\x00\x01":
            continue
        flags = packet[offset + 7] >> 6
        for index, present in enumerate((flags & 2, flags == 3)):
            if not present:
                continue
            at = offset + 9 + 5 * index
            field = packet[at:at + 5]
            value = (field[0] >> 1 & 7) << 30 | field[1] << 22 | (field[2] >> 1) << 15 | field[3] << 7 | field[4] >> 1
            value = (value + shift) % (1 << 33)
            packet[at] = (field[0] & 0xF1) | (value >> 30 & 7) << 1
            packet[at + 1] = value >> 22 & 0xFF
            packet[at + 2] = (value >> 15 & 0x7F) << 1 | 1
            packet[at + 3] = value >> 7 & 0xFF
            packet[at + 4] = (value & 0x7F) << 1 | 1
    # 节目表在文件开头，删去的范围不含 PAT/PMT 的首次出现。
    target.write_bytes(data[:first * 188] + bytes(tail))


def split_nals(data):
    """按 Annex B 起始码切分 NAL，返回不含起始码的 NAL 列表。"""
    starts = []
    index = data.find(b"\x00\x00\x01")
    while index >= 0:
        starts.append(index + 3)
        index = data.find(b"\x00\x00\x01", index + 3)
    nals = []
    for number, start in enumerate(starts):
        end = starts[number + 1] - 3 if number + 1 < len(starts) else len(data)
        while end > start and data[end - 1] == 0:
            end -= 1
        nals.append(data[start:end])
    return nals


def gradual_refresh(ffmpeg, root, name="gradual-refresh", size="320x180", filters=(), options=(), params=""):
    """逐行刷新（周期帧内刷新）H.264：只有 P 条带的恢复点，没有 IDR 和 I 帧。

    去掉开头的 IDR，并把恢复点 SEI 改为声称立即恢复（recovery_frame_cnt=0），与部分卫星转播源相同：
    解复用器把恢复点标为关键帧，单独从这里解码要等刷新扫过整幅画面才完整。
    """
    raw = root / f"{name}.h264"
    ffmpeg("-f", "lavfi", "-i", f"testsrc2=size={size}:rate=25:duration=30",
           "-vf", ",".join(["format=yuv420p", FRAME_NUMBER, *filters]), "-c:v", "libx264", "-preset", "veryfast",
           "-crf", 23, "-bf", 0, *options, "-x264-params", "intra-refresh=1:keyint=25:aud=1" + params, "-f", "h264", raw)
    nals = split_nals(raw.read_bytes())
    raw.unlink()
    parameter_sets = [nal for nal in nals if nal[0] & 31 in (7, 8)][:2]
    units, current = [], []
    for nal in nals:
        if nal[0] & 31 == 9 and current:
            units.append(current)
            current = []
        current.append(nal)
    units.append(current)
    # 恢复点 SEI：payloadType=6、payloadSize=1，cnt=0、exact_match=1、broken_link=0、changing_slice_group_idc=0。
    recovery = bytes([0x06, 0x06, 0x01, 0xA4, 0x80])
    is_recovery = lambda unit: any(nal[0] & 31 == 6 and nal[1] == 6 for nal in unit)
    starts = [number for number, unit in enumerate(units) if is_recovery(unit) and number > 0]
    output = bytearray()
    # 与转播源一样，每个恢复点前重复参数集，从任一恢复点起都能开始解码。
    for unit in units[starts[2]:]:
        recovery_unit = is_recovery(unit)
        for nal in unit:
            if nal[0] & 31 == 6 and nal[1] == 6:
                nal = recovery
            elif nal[0] & 31 in (7, 8):
                continue
            output += b"\x00\x00\x00\x01" + nal
            if recovery_unit and nal[0] & 31 == 9:
                for parameter_set in parameter_sets:
                    output += b"\x00\x00\x00\x01" + parameter_set
    raw.write_bytes(bytes(output))
    ffmpeg("-f", "h264", "-framerate", 25, "-i", raw, "-c", "copy", "-output_ts_offset", 10, root / f"{name}.ts")
    raw.unlink()


def mpeg2_refresh(ffmpeg, root):
    """MPEG-2 帧内刷新：全程没有 I 帧，只在部分 P 帧前重复序列头和 GOP 头，附 SMPTE 302M 音轨。

    与杜比 E 回传源的结构相同：4:2:2 视频、MP2 与 302M（BSSD 注册描述符）音轨，解复用器一个关键包都不标。
    去掉编码器开头的 I 帧，每 25 帧在 P 帧前插入原序列头、序列扩展与 GOP 头。
    FFmpeg 的 MPEG-2 编码器不支持帧内刷新：画面用逐帧独立的随机噪声，并关闭场景切换检测，
    帧间预测毫无收益，编码器把 P 帧的宏块都编成帧内块，从任一入口解码几帧后即与连续解码一致。
    逐行画面，播放去隔行后仍可与预览比对；302M 码率高，只保留开头 4 秒。
    """
    raw = root / "mpeg2-refresh.m2v"
    ffmpeg("-f", "lavfi", "-i", "nullsrc=size=160x90:rate=25:duration=20",
           "-vf", "format=yuv422p,geq=lum='random(1)*255':cb='random(2)*255':cr='random(3)*255'",
           "-c:v", "mpeg2video", "-q:v", 31, "-g", 900, "-bf", 0, "-sc_threshold", 1000000000,
           "-f", "mpeg2video", raw)
    data = raw.read_bytes()
    raw.write_bytes(refresh_units(data, 25))
    # 裸流没有时间戳，也没有关键帧：生成时间戳，并保留首个关键帧之前的包。
    ffmpeg("-fflags", "+genpts", "-f", "mpegvideo", "-framerate", 25, "-i", raw,
           "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=20",
           "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=4",
           "-map", 0, "-map", 1, "-map", 2, "-c:v", "copy", "-copyinkf",
           "-c:a:0", "mp2", "-b:a:0", "192k", "-c:a:1", "s302m", "-ac", 2, "-strict", "-2",
           "-output_ts_offset", 10, root / "mpeg2-refresh.ts")
    raw.unlink()


def refresh_units(data, interval):
    """MPEG-2 编码器输出改为逐行刷新的结构：去掉开头的 I 帧，每 interval 个 P 帧前插入原序列头、扩展与 GOP 头。"""
    starts = []
    index = data.find(b"\x00\x00\x01")
    while index >= 0:
        starts.append(index)
        index = data.find(b"\x00\x00\x01", index + 3)
    pictures = [start for start in starts if data[start + 3] == 0]
    # 开头的序列头、扩展与 GOP 头整体复用；其余图像之间不应再有序列头。
    header = data[:pictures[0]]
    assert header.startswith(b"\x00\x00\x01\xb3") and b"\x00\x00\x01\xb8" in header
    assert not any(data[start + 3] in (0xB3, 0xB8) for start in starts if start > pictures[0])
    end = data.rfind(b"\x00\x00\x01\xb7")
    bounds = pictures + [end if end > pictures[-1] else len(data)]
    units = [data[bounds[number]:bounds[number + 1]] for number in range(len(pictures))]
    # picture_coding_type：首帧为 I，其余必须都是 P。
    assert (units[0][5] >> 3) & 7 == 1 and all((unit[5] >> 3) & 7 == 2 for unit in units[1:])
    output = bytearray()
    for number, unit in enumerate(units[1:]):
        if number % interval == 0:
            output += header
        output += unit
    output += b"\x00\x00\x01\xb7"
    return bytes(output)


def mpeg2_refresh_numbered(ffmpeg, root):
    """带帧序号的 MPEG-2 逐行刷新素材，用于智能剪辑：结构同 mpeg2_refresh，序列扩展声明 low_delay（与杜比 E 回传源相同）。

    噪声画面的宏块都编成帧内块，顶部的帧序号块大多帧间预测：衔接后它们参考头段重新编码的画面，仍可识别。
    23 秒（编码器的 GOP 上限为 600 帧），覆盖 12 秒的解码预滚与之后的复制区。
    """
    raw = root / "mpeg2-refresh-numbered.m2v"
    ffmpeg("-f", "lavfi", "-i", "nullsrc=size=256x144:rate=25:duration=23",
           "-vf", f"format=yuv420p,geq=lum='random(1)*255':cb='random(2)*255':cr='random(3)*255',{FRAME_NUMBER}",
           "-c:v", "mpeg2video", "-q:v", 31, "-g", 600, "-bf", 0, "-sc_threshold", 1000000000, "-flags", "+low_delay",
           "-f", "mpeg2video", raw)
    raw.write_bytes(refresh_units(raw.read_bytes(), 25))
    ffmpeg("-fflags", "+genpts", "-f", "mpegvideo", "-framerate", 25, "-i", raw,
           "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=23",
           "-map", 0, "-map", 1, "-c:v", "copy", "-copyinkf", "-c:a", "mp2", "-b:a", "192k",
           "-output_ts_offset", 20, root / "mpeg2-refresh-numbered.ts")
    raw.unlink()


if __name__ == "__main__":
    main()
