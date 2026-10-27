#!/usr/bin/env python3
"""生成 DVD / 蓝光 ISO 集成测试素材，默认输出到已被 Git 忽略的 artifacts/test-media/iso。

蓝光样本通过手写 BDMV 结构（INDEX/MovieObject/MPLS/CLPI，含真实 EP map）与
ffmpeg 编码的 M2TS 片段组成完整光盘结构，经 libbluray 读取；DVD 样本通过
spumux（VOB 图形字幕）+ dvdauthor（IFO/PGC/Cell）+ ISO9660 工具组成。

依赖：ffmpeg / ffprobe（需 libass 与 ac3/mp2/mpeg2video 编码器，蓝光另需 libx264）、
spumux 与 dvdauthor（dvdauthor 包），以及支持 -udf 的 mkisofs（cdrtools）或 genisoimage。
使用 mkisofs / genisoimage 生成经过验证的 UDF 布局；不使用 hdiutil 或 xorriso。
"""
import argparse
import json
import os
import shutil
import struct
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from pgs_sample import write_pgs_sample  # noqa: E402

BD_VIDEO_PID = 0x1011
BD_AUDIO_PID = 0x1100
BD_PG_PID = 0x1200
BD_MUXRATE = "1M"
TP_PACKET_SIZE = 192


class Bin:
    """顺序字节写入器，按位拼接 BDMV 字段（大端）。"""

    def __init__(self):
        self.parts = []
        self.bit_buffer = 0
        self.bit_count = 0

    def u8(self, value):
        self.flush_bits()
        self.parts.append((value & 0xFF).to_bytes(1, "big"))
        return self

    def u16(self, value):
        self.flush_bits()
        self.parts.append((value & 0xFFFF).to_bytes(2, "big"))
        return self

    def u32(self, value):
        self.flush_bits()
        self.parts.append((value & 0xFFFFFFFF).to_bytes(4, "big"))
        return self

    def raw(self, data):
        self.flush_bits()
        self.parts.append(data)
        return self

    def bits(self, value, width):
        for shift in range(width - 1, -1, -1):
            self.bit_buffer = (self.bit_buffer << 1) | ((value >> shift) & 1)
            self.bit_count += 1
            if self.bit_count == 8:
                self.parts.append(self.bit_buffer.to_bytes(1, "big"))
                self.bit_buffer = 0
                self.bit_count = 0
        return self

    def flush_bits(self):
        if self.bit_count:
            self.parts.append((self.bit_buffer << (8 - self.bit_count)).to_bytes(1, "big"))
            self.bit_buffer = 0
            self.bit_count = 0

    def bytes(self):
        self.flush_bits()
        return b"".join(self.parts)


def with_length(body):
    """前置 4 字节大端长度字段（长度不含自身，与 libdvdread/libbluray 的读法一致）。"""
    return len(body).to_bytes(4, "big") + body


# ---------------------------------------------------------------------------
# BDMV 结构（对齐 libbluray 的 indx/mobj/mpls/clpi 解析器）
# ---------------------------------------------------------------------------

def write_index_bdmv(directory, title_count):
    out = Bin()
    out.raw(b"INDX").raw(b"0200")
    indexes_start = 40 + 34
    out.u32(indexes_start)
    out.u32(0)                      # 无扩展数据
    out.raw(b"\0" * (40 - 16))      # 头部与 AppInfo 之间的保留区
    # AppInfoBDMV：length 34（含自身），内容保留。
    out.u32(34)
    out.raw(b"\0" * 30)

    index = Bin()
    # first_play / top_menu：HDMV 对象 0xffff 表示不存在。
    for _ in range(2):
        index.bits(1, 2).bits(0, 30)          # object_type = HDMV
        index.bits(1, 2).bits(0, 14).u16(0xFFFF).u32(0)
    index.u16(title_count)
    for _ in range(title_count):
        index.bits(1, 2).bits(1, 2).bits(0, 28)  # HDMV movie 类型
        index.bits(1, 2).bits(0, 14).u16(0).u32(0)
    body = index.bytes()
    out.u32(len(body)).raw(body)
    (directory / "index.bdmv").write_bytes(out.bytes())


def write_movie_object_bdmv(directory):
    out = Bin()
    out.raw(b"MOBJ").raw(b"0200").u32(0)
    objects = Bin()
    objects.u32(1)
    objects.bits(0, 3).bits(0, 13)  # resume/menu/title 掩码均为 0
    objects.u16(0)                  # 无导航命令：仅通过播放列表选择，无菜单需求。
    body = objects.bytes()
    out.u32(len(body)).raw(body)
    (directory / "MovieObject.bdmv").write_bytes(out.bytes())


def mpls_stream_entry(pid, coding_type, attr_bits, lang=None):
    """播放列表流入口：stream_type=1（主路径 PID），随后是编码属性段。"""
    entry = Bin()
    entry.u8(3)            # 描述长度：stream_type(1) + pid(2)
    entry.u8(1)            # stream_type = 1
    entry.u16(pid)
    attr = Bin()
    attr.u8(coding_type)
    attr.raw(attr_bits)
    if lang is not None:
        attr.raw(lang.encode()[:3].ljust(3, b" "))
    attr_body = attr.bytes()
    entry.u8(len(attr_body)).raw(attr_body)
    return entry


def video_attr_bits():
    return (3 << 4 | 2).to_bytes(1, "big")  # format=480p, rate=24fps


def audio_attr_bits():
    return (3 << 4 | 3).to_bytes(1, "big")  # format=48kHz, rate=256kbps


def write_mpls(path, play_items):
    """play_items: [{clip_id, in_time45k, out_time45k, streams: [(pid, kind, lang)]}]"""
    app_info = Bin()
    app_info.u8(0).u8(1).u16(0)  # reserved + playback_type=1
    app_info.raw(b"\0" * 8)      # UO mask
    app_info.bits(0, 1).bits(0, 1).bits(0, 1).bits(0, 1).bits(0, 1)
    app_info.bits(0, 11)
    app_info_body = with_length(app_info.bytes())

    playlist = Bin()
    playlist.u16(0)  # reserved
    playlist.u16(len(play_items))
    playlist.u16(0)  # 无子路径
    for item in play_items:
        entry = Bin()
        entry.raw(item["clip_id"].encode()).raw(b"M2TS")
        entry.bits(0, 11)                          # reserved
        entry.bits(0, 1)                           # is_multi_angle
        entry.bits(item.get("connection", 1), 4)   # 1 = 非无缝（入点经 EP map 生效）
        entry.u8(0)                                # stc_id
        entry.u32(item["in_time"])
        entry.u32(item["out_time"])
        entry.raw(b"\0" * 8)                       # UO mask
        entry.bits(0, 1).bits(0, 7)                # random_access + reserved
        entry.u8(0)                                # still_mode
        entry.u16(0)                               # still_time

        stn = Bin()
        stn.u16(0)  # reserved
        streams = item["streams"]
        stn.u8(sum(1 for s in streams if s[1] == "v"))
        stn.u8(sum(1 for s in streams if s[1] == "a"))
        stn.u8(sum(1 for s in streams if s[1] == "p"))
        stn.u8(0).u8(0).u8(0).u8(0).u8(0)          # ig/secondary/pip/dv
        stn.raw(b"\0" * 4)
        for pid, kind, lang in streams:
            if kind == "v":
                stn.raw(mpls_stream_entry(pid, 0x1B, video_attr_bits()).bytes())
            elif kind == "a":
                stn.raw(mpls_stream_entry(pid, 0x81, audio_attr_bits(), lang).bytes())
            else:
                stn.raw(mpls_stream_entry(pid, 0x90, b"", lang).bytes())
        entry.u16(len(stn.bytes())).raw(stn.bytes())
        # PlayItem 长度覆盖其全部内容（长度字段之后）。
        playlist.u16(len(entry.bytes())).raw(entry.bytes())
    playlist_body = playlist.bytes()

    # 每个播放项起点设为章节，覆盖非零入点、重复片段的标题相对章节时间。
    marks = Bin().u16(len(play_items))
    for index, item in enumerate(play_items):
        marks.u8(0).u8(1).u16(index).u32(item["in_time"]).u16(0xFFFF).u32(0)
    marks_body = marks.bytes()

    # 头部：sig(4)+ver(4)+三个地址(12)+保留(20) = 40 字节，AppInfoBDMV 紧随其后。
    header_size = 40
    app_info_offset = header_size
    playlist_offset = app_info_offset + len(app_info_body)
    marks_offset = playlist_offset + 4 + len(playlist_body)

    out = Bin()
    out.raw(b"MPLS").raw(b"0200")
    out.u32(playlist_offset)
    out.u32(marks_offset)
    out.u32(0)
    out.raw(b"\0" * 20)
    out.raw(app_info_body)
    out.u32(len(playlist_body)).raw(playlist_body)
    out.u32(len(marks_body)).raw(marks_body)
    path.write_bytes(out.bytes())


def clpi_stream_attr(coding_type, attr_bits, lang=None):
    """CLPI 流属性：coding + 属性位 (+ 语言)，后接 12 字节 ISRC（全 0）。"""
    attr = Bin()
    attr.u8(coding_type)
    attr.raw(attr_bits)
    if lang is not None:
        attr.raw(lang.encode()[:3].ljust(3, b" "))
    attr.raw(b"\0" * 12)  # ISRC
    body = attr.bytes()
    return len(body).to_bytes(1, "big") + body


def clpi_video_attr():
    # format=480p, rate=24fps, aspect=4:3（CLPI 解析器会读取 aspect 位）。
    writer = Bin()
    writer.bits(3, 4).bits(2, 4)   # format, rate
    writer.bits(0, 4)              # aspect 4:3
    writer.bits(0, 2).bits(0, 1)   # reserved + oc_flag
    writer.bits(0, 17)             # reserved
    return writer.bytes()


def clpi_audio_attr(lang):
    writer = Bin()
    writer.bits(3, 4).bits(3, 4)   # format=48kHz, rate=256kbps
    return writer.bytes()          # 语言紧随其后


def write_clpi(path, num_packets, first_pts45k, last_pts45k, ep_points, streams):
    """ep_points: [(pts45k, packet_index)]，视频 PID 的关键帧入点表。"""
    clip_info = Bin()
    clip_info.u16(0)                          # reserved
    clip_info.u8(1)                           # clip_stream_type=1（AV 流）
    clip_info.u8(1)                           # application_type=1（主时间轴）
    clip_info.bits(0, 31).bits(0, 1)          # reserved + is_atc_delta=0
    clip_info.u32(8000)                       # ts_recording_rate（kbps，仅元数据）
    clip_info.u32(num_packets)
    clip_info.raw(b"\0" * 128)                # reserved
    clip_info.u16(0)                          # TS type info 长度 0
    clip_info_body = with_length(clip_info.bytes())

    sequence = Bin()
    sequence.u8(0)                            # reserved
    sequence.u8(1)                            # num_ATC_seq
    sequence.u32(0)                           # SPN_ATC_start
    sequence.u8(1)                            # num_STC_seq
    sequence.u8(0)                            # offset_STC_id
    sequence.u16(BD_VIDEO_PID)                # PCR_PID
    sequence.u32(0)                           # SPN_STC_start
    sequence.u32(first_pts45k)
    sequence.u32(last_pts45k)
    sequence_body = with_length(sequence.bytes())

    program = Bin()
    program.u8(0)                             # reserved
    program.u8(1)                             # num_program
    program.u32(0)                            # spn_program_sequence_start
    program.u16(0x1000)                       # program_map_pid（PMT）
    program.u8(len(streams)).u8(0)            # num_streams, num_groups
    for pid, kind, lang in streams:
        program.u16(pid)
        if kind == "v":
            program.raw(clpi_stream_attr(0x1B, clpi_video_attr()))
        elif kind == "a":
            program.raw(clpi_stream_attr(0x81, clpi_audio_attr(lang), lang))
        else:
            program.raw(clpi_stream_attr(0x90, b"", lang))
    program_body = with_length(program.bytes())

    # CPI：单个 EP map（视频 PID），每个视频关键帧一组粗/细条目。
    # libbluray 以 ep_map_pos（长度字段之后的 type 字段末尾，即区段起点+6）为
    # ep_map_stream_start_addr 的基准；EP map 本体在条目表之后。
    ep_map = Bin()
    # fine 表地址相对 EP map 起点，需跳过 fine_start 字段自身与 coarse 表。
    ep_map.u32(4 + 8 * len(ep_points))
    # coarse 与 fine 一一对应：ref_ep_fine_id 是细表索引，不是包号。
    for index, (point_pts, packet) in enumerate(ep_points):
        ep_map.bits(index & 0x3FFFF, 18)      # ref_ep_fine_id
        ep_map.bits(((point_pts >> 19) << 1) & 0x3FFF, 14)  # PTS 高位（bit0 角度标志=0）
        ep_map.u32(packet & ~0x1FFFF)
    for point_pts, packet in ep_points:
        ep_map.bits(0, 1)                     # is_angle_change
        ep_map.bits(0, 3)                     # i_end_position_offset
        ep_map.bits((point_pts >> 8) & 0x7FF, 11)
        ep_map.bits(packet & 0x1FFFF, 17)
    ep_map_body = ep_map.bytes()

    cpi = Bin()
    cpi.bits(0, 12).bits(1, 4)                # reserved + EP map type=1
    cpi.u8(0)                                 # reserved
    cpi.u8(1)                                 # num_stream_pid
    # EP map 放在长度字段(4)+type(2)+保留(1)+数量(1)+条目表(12)之后，
    # 即区段起点+20；解析器以 ep_map_pos（区段起点+6）为基准，故地址 = 20-6 = 14。
    # PID(16)+reserved(10)+type(4)+coarse(16)+fine(18)+addr(32) 必须按位连续。
    cpi.u16(BD_VIDEO_PID)
    cpi.bits(0, 10).bits(1, 4)                # reserved + ep_stream_type=1
    cpi.bits(len(ep_points), 16)
    cpi.bits(len(ep_points), 18)
    cpi.bits(14, 32)
    cpi.raw(ep_map_body)
    cpi_body = with_length(cpi.bytes())

    sections = [
        (40, clip_info_body),
        (40 + len(clip_info_body), sequence_body),
        (40 + len(clip_info_body) + len(sequence_body), program_body),
        (40 + len(clip_info_body) + len(sequence_body) + len(program_body), cpi_body),
    ]
    # 头部：sig(4)+ver(4)+sequence/program/cpi/clip_mark/ext 五个地址(20)；
    # ClipInfo 固定在 40（libbluray 硬编码 seek 40，头部没有它的地址字段）。
    header = Bin()
    header.raw(b"HDMV").raw(b"0200")
    header.u32(sections[1][0])
    header.u32(sections[2][0])
    header.u32(sections[3][0])
    header.u32(0)   # clip mark
    header.u32(0)   # extension data
    data = bytearray(header.bytes())
    for offset, body in sections:
        if len(data) < offset:
            data.extend(b"\0" * (offset - len(data)))
        data.extend(body)
    path.write_bytes(data)


# ---------------------------------------------------------------------------
# 素材生成
# ---------------------------------------------------------------------------

def filter_path(path: Path) -> str:
    """lavfi filter 参数中的路径转义（Windows 盘符冒号）。"""
    text = str(path)
    return text.replace("\\", "/").replace(":", "\\:")


class Generator:
    def __init__(self, ffmpeg, ffprobe, root, require_h264=True):
        self.ffmpeg_path = ffmpeg
        self.ffprobe_path = ffprobe
        self.root = root
        self.work = root / "work"
        self._check_capabilities(require_h264)

    def _check_capabilities(self, require_h264=True):
        def fail(missing):
            raise SystemExit(
                "ffmpeg 缺少生成 ISO 样本所需的能力: " + ", ".join(missing) +
                "。需要启用 libass 的 ffmpeg 构建（ass 过滤器）"
                "以及 libx264 / ac3 / mpeg2video / mp2 编码器。")

        probe = self.run(self.ffmpeg_path, "-hide_banner", "-h", "filter=ass", check=False)
        if probe.returncode != 0:
            fail(["ass 过滤器（libass）"])
        encoders = self.run(self.ffmpeg_path, "-hide_banner", "-encoders", check=False).stdout
        required = ["ac3", "mpeg2video", "mp2"] + (["libx264"] if require_h264 else [])
        missing = [name for name in required if f" {name} " not in encoders]
        if missing:
            fail(missing)

    def run(self, *values, check=True):
        return subprocess.run([*map(str, values)], check=check,
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)

    def ffmpeg(self, *values, check=True):
        return self.run(self.ffmpeg_path, "-hide_banner", "-loglevel", "error", "-y", *values, check=check)

    def probe_packets(self, path, select=None):
        args = [self.ffprobe_path, "-v", "error", "-show_packets",
                "-show_entries", "packet=pts,pos,flags", "-of", "csv=p=0"]
        if select:
            args += ["-select_streams", select]
        args.append(path)
        result = self.run(*args)
        packets = []
        for line in result.stdout.splitlines():
            fields = line.strip().split(",")
            if len(fields) < 3:
                continue
            # csv=p=0 输出 pts,pos,flags[,...]；部分版本会附加 side-data 列。
            if fields[0] == "packet":
                fields = fields[1:]
            def number(value):
                return int(value) if value not in ("N/A", "") else None
            pts = number(fields[0])
            pos = number(fields[1])
            if pts is None or pos is None:
                continue
            flags = fields[2] if len(fields) > 2 else ""
            packets.append((pts, pos, flags))
        return packets

    def probe_first_last_pts(self, path):
        pts_values = [pts for pts, _, _ in self.probe_packets(path) if pts is not None]
        if not pts_values:
            raise RuntimeError(f"No timestamps found in {path}")
        return min(pts_values), max(pts_values)

    def bd_labels_ass(self, events):
        content = (
            "[Script Info]\nScriptType: v4.00+\nPlayResX: 320\nPlayResY: 180\n"
            "[V4+ Styles]\n"
            "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, "
            "BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, "
            "BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n"
            "Style: Big,Sans,28,&H0020E0FF,&H000000FF,&H00000000,&H80000000,1,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1\n"
            "[Events]\n"
            "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n")
        return content + events

    @staticmethod
    def ass_event(start, end, text, style="Big"):
        def stamp(seconds):
            hours = int(seconds // 3600)
            minutes = int(seconds % 3600 // 60)
            secs = int(seconds % 60)
            hundredths = int(round(seconds % 1 * 100)) % 100
            return f"{hours}:{minutes:02d}:{secs:02d}.{hundredths:02d}"
        return f"Dialogue: 0,{stamp(start)},{stamp(end)},{style},,0,0,0,,{text}\n"

    def build_bd(self):
        stream_dir = self.work / "bluray/BDMV/STREAM"
        clip_dir = self.work / "bluray/BDMV/CLIPINF"
        playlist_dir = self.work / "bluray/BDMV/PLAYLIST"
        for directory in (stream_dir, clip_dir, playlist_dir):
            directory.mkdir(parents=True, exist_ok=True)

        # 同时验证长标题和三分钟以下的重复片段标题；读取层不能受协议时长过滤限制。
        clips = [
            # (id, duration, color, tone frequency, pts offset seconds, label)
            ("00010", 185.0, "0x2060C0", 440, 0.0, "CLIP 10 BLUE"),
            ("00011", 90.0, "0x20A040", 550, 0.0, "CLIP 11 GREEN"),
            ("00012", 90.0, "0xC06020", 660, 300.0, "CLIP 12 ORANGE"),
        ]
        clip_files = {}
        for clip_id, duration, color, tone, pts_offset, label in clips:
            events = "".join(self.ass_event(start, min(start + 10, duration - 0.5), label)
                             for start in range(0, int(duration) - 1, 30))
            per_clip = self.work / f"labels_{clip_id}.ass"
            per_clip.write_text(self.bd_labels_ass(events), encoding="utf-8")
            target = stream_dir / f"{clip_id}.m2ts"
            self.ffmpeg(
                "-f", "lavfi", "-i", (f"testsrc2=s=320x180:r=24:d={duration}" if clip_id == "00012"
                                    else f"color=c={color}:s=320x180:r=24:d={duration}"),
                "-f", "lavfi", "-i", f"sine=frequency={tone}:sample_rate=48000:duration={duration}",
                "-vf", f"ass=filename={filter_path(per_clip)}",
                "-map", "0:v", "-map", "1:a",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "30",
                "-g", "48", "-keyint_min", "48", "-sc_threshold", "0", "-pix_fmt", "yuv420p",
                "-c:a", "ac3", "-b:a", "96k", "-ar", "48000",
                "-output_ts_offset", str(pts_offset),
                "-mpegts_m2ts_mode", "1", *([] if clip_id == "00012" else ["-muxrate", BD_MUXRATE]),
                "-streamid", f"0:0x{BD_VIDEO_PID:X}", "-streamid", f"1:0x{BD_AUDIO_PID:X}",
                "-f", "mpegts", target)
            clip_files[clip_id] = target

        # PGS 片段：视频 + 音频 + bitmap.sup（事件 1s 显示、3s 清屏、4-5s 再显示、7s 清屏）。
        sup = self.work / "pgs.sup"
        write_pgs_sample(sup)
        target = stream_dir / "00013.m2ts"
        pgs_duration = 185.0
        events = "".join(self.ass_event(start, min(start + 10, pgs_duration - 0.5), "CLIP 13 PURPLE")
                         for start in range(0, int(pgs_duration) - 1, 30))
        per_clip = self.work / "labels_00013.ass"
        per_clip.write_text(self.bd_labels_ass(events), encoding="utf-8")
        self.ffmpeg(
            "-f", "lavfi", "-i", f"color=c=0x8040A0:s=320x180:r=24:d={pgs_duration}",
            "-f", "lavfi", "-i", f"sine=frequency=770:sample_rate=48000:duration={pgs_duration}",
            "-i", sup,
            "-vf", f"ass=filename={filter_path(per_clip)}",
            "-map", "0:v", "-map", "1:a", "-map", "2:s",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "30",
            "-g", "48", "-keyint_min", "48", "-sc_threshold", "0", "-pix_fmt", "yuv420p",
            "-c:a", "ac3", "-b:a", "96k", "-ar", "48000", "-c:s", "copy",
            "-mpegts_m2ts_mode", "1", "-muxrate", BD_MUXRATE,
            "-streamid", f"0:0x{BD_VIDEO_PID:X}", "-streamid", f"1:0x{BD_AUDIO_PID:X}",
            "-streamid", f"2:0x{BD_PG_PID:X}",
            "-f", "mpegts", target)
        clip_files["00013"] = target

        clip_meta = {}
        for clip_id, path in clip_files.items():
            num_packets = path.stat().st_size // TP_PACKET_SIZE
            first_pts, last_pts = self.probe_first_last_pts(path)
            # EP map 只记录视频关键帧；音频包也会带 K 标记，必须按流过滤。
            keyframes = sorted({(pts // 2, pos // TP_PACKET_SIZE) for pts, pos, flags in
                                self.probe_packets(path, "v")
                                if pts is not None and "K" in flags})
            if not keyframes:
                raise RuntimeError(f"No keyframes found in {path}")
            streams = [(BD_VIDEO_PID, "v", "und"), (BD_AUDIO_PID, "a", "eng")]
            if clip_id == "00013":
                streams.append((BD_PG_PID, "p", "eng"))
            write_clpi(clip_dir / f"{clip_id}.clpi", num_packets,
                       first_pts // 2, (last_pts + 1) // 2, keyframes, streams)
            clip_meta[clip_id] = {"first_pts": first_pts, "last_pts": last_pts}

        def stamp45k(clip_id, seconds):
            # first_pts 是 90kHz 的流内 PTS；MPLS 播放项时间以 45kHz 记录。
            return int(clip_meta[clip_id]["first_pts"] // 2 + seconds * 45000)

        def full_range(clip_id):
            return (clip_meta[clip_id]["first_pts"] // 2, (clip_meta[clip_id]["last_pts"] + 1) // 2)

        def streams_for(clip_id):
            streams = [(BD_VIDEO_PID, "v", "und"), (BD_AUDIO_PID, "a", "eng")]
            if clip_id == "00013":
                streams.append((BD_PG_PID, "p", "eng"))
            return streams

        start10, end10 = full_range("00010")
        start13, end13 = full_range("00013")
        # 蓝光封装惯例：首个 PlayItem 的 in_time 即片段起始 PTS（libbluray 对
        # ref=0 的 PlayItem 从片段文件头读取）；后续 PlayItem 的入点经 EP map 生效。
        playlists = {
            # 标题 1：单片段完整标题（185s）。
            "00001": [{"clip_id": "00010", "in_time": start10, "out_time": end10,
                       "connection": 1, "streams": streams_for("00010")}],
            # 标题 2（正片，200s）：多片段、非零入点、截断出点、片段顺序与文件名
            # 不同、同一片段重复引用、跨片段 PTS 基准大幅跳变（00012 偏移 +300s）。
            "00002": [
                {"clip_id": "00011", "in_time": stamp45k("00011", 0.0),
                 "out_time": stamp45k("00011", 90.0), "connection": 1, "streams": streams_for("00011")},
                {"clip_id": "00012", "in_time": stamp45k("00012", 5.0),
                 "out_time": stamp45k("00012", 85.0), "connection": 1, "streams": streams_for("00012")},
                {"clip_id": "00011", "in_time": stamp45k("00011", 30.0),
                 "out_time": stamp45k("00011", 60.0), "connection": 1, "streams": streams_for("00011")},
            ],
            # 标题 3：携带 PGS 字幕的完整片段（185s）。
            "00003": [{"clip_id": "00013", "in_time": start13, "out_time": end13,
                       "connection": 1, "streams": streams_for("00013")}],
        }
        # 60 秒标题重复引用同一 PGS 片段三次，覆盖 PTS 碰撞、跨段字幕和有界回看。
        playlists["00004"] = [
            {"clip_id": "00013", "in_time": stamp45k("00013", 0),
             "out_time": stamp45k("00013", 20), "connection": 1, "streams": streams_for("00013")}
            for _ in range(3)
        ]
        # 在字幕显示期间截断，后接没有字幕的片段。
        playlists["00005"] = [
            {"clip_id": "00013", "in_time": stamp45k("00013", 0),
             "out_time": stamp45k("00013", 1.25), "connection": 1, "streams": streams_for("00013")},
            {"clip_id": "00011", "in_time": stamp45k("00011", 0),
             "out_time": stamp45k("00011", 5), "connection": 1, "streams": streams_for("00011")},
        ]
        # 保留一个入点落在 GOP 内的播放列表，用于明确拒绝无法直接无损拼接的边界。
        playlists["00006"] = [dict(item) for item in playlists["00002"]]
        # 正片的非零入点对齐到关键帧附近；时长仍为 90+80+30=200 秒。
        playlists["00002"][1]["in_time"] = stamp45k("00012", 6.0)
        playlists["00002"][1]["out_time"] = stamp45k("00012", 86.0)
        for name, items in playlists.items():
            write_mpls(playlist_dir / f"{name}.mpls", items)
        write_index_bdmv(self.work / "bluray/BDMV", len(playlists))
        write_movie_object_bdmv(self.work / "bluray/BDMV")
        return self.work / "bluray"

    def dvd_ass(self, text, duration):
        content = (
            "[Script Info]\nScriptType: v4.00+\nPlayResX: 352\nPlayResY: 240\n"
            "[V4+ Styles]\n"
            "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, "
            "BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, "
            "BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n"
            "Style: Big,Sans,26,&H0020E0FF,&H000000FF,&H00000000,&H80000000,1,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1\n"
            "[Events]\n"
            "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n")
        path = self.work / "dvd_src" / f"label_{text.lower().replace(' ', '_')}.ass"
        path.write_text(content + self.ass_event(0.2, duration - 0.2, text), encoding="utf-8")
        return path

    @staticmethod
    def _write_rgb_png(path: Path, width: int, height: int, rgb: bytes):
        """纯 stdlib 的最小 PNG 写出（RGB，非交错）。
        spumux 对带 alpha 的 PNG 会崩溃，因此输出 RGB 并用 transparent 指定透明色。"""
        import zlib

        def chunk(kind, payload):
            return (len(payload).to_bytes(4, "big") + kind + payload
                    + zlib.crc32(kind + payload).to_bytes(4, "big"))

        raw = b"".join(b"\x00" + rgb[y * width * 3:(y + 1) * width * 3] for y in range(height))
        path.write_bytes(
            b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 6))
            + chunk(b"IEND", b""))

    def _subtitle_png(self, path, text):
        """用 libass 渲染黑底白字字幕图，供 spumux 编码为 VOB SPU。
        spumux 对带 alpha 的 PNG 会崩溃，因此渲染为 RGB，黑底在 XML 中
        通过 transparent 声明为透明色；白字与灰阶描边保留为可见像素。"""
        ass_path = path.with_suffix(".ass")
        ass_path.write_text(
            "[Script Info]\nScriptType: v4.00+\nPlayResX: 240\nPlayResY: 52\n"
            "[V4+ Styles]\n"
            "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, "
            "BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, "
            "BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n"
            "Style: Sub,Sans,20,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,1,0,0,0,100,100,0,0,1,2,0,5,4,4,4,1\n"
            "[Events]\n"
            "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
            "Dialogue: 0,0:00:00.00,0:00:01.00,Sub,,0,0,0,," + text + "\n",
            encoding="utf-8")
        raw = path.with_suffix(".rgb")
        self.ffmpeg(
            "-f", "lavfi", "-i", "color=c=black:s=240x52:r=1:d=1",
            "-vf", f"ass=filename={filter_path(ass_path)}",
            "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", raw)
        rgb = raw.read_bytes()
        raw.unlink()
        if len(rgb) < 240 * 52 * 3:
            raise RuntimeError("字幕图渲染失败：RGB 帧不完整。")
        # DVD SPU 每个显示集合最多 4 种颜色（1 透明 + 文字 + 描边 + 高光），
        # 因此按亮度阈值量化到 4 色，去掉抗锯齿产生的上百种灰阶。
        pixels = bytearray(rgb[:240 * 52 * 3])
        for i in range(0, len(pixels), 3):
            luminance = (pixels[i] * 299 + pixels[i + 1] * 587 + pixels[i + 2] * 114) // 1000
            if luminance < 48:
                pixels[i] = pixels[i + 1] = pixels[i + 2] = 0          # 透明背景
            else:
                pixels[i] = pixels[i + 1] = pixels[i + 2] = 255        # 文字
        self._write_rgb_png(path, 240, 52, bytes(pixels))
        return path

    def encode_dvd_cell(self, name, duration, color, tone, label, spu=None, gop=15):
        video_dir = self.work / "dvd_src"
        plain = video_dir / f"{name}_plain.vob"
        self.ffmpeg(
            "-f", "lavfi", "-i", f"color=c={color}:s=352x240:r=30000/1001:d={duration}",
            "-f", "lavfi", "-i", f"sine=frequency={tone}:sample_rate=48000:duration={duration}",
            "-vf", f"ass=filename={filter_path(self.dvd_ass(label, duration))}",
            "-map", "0:v", "-map", "1:a",
            "-c:v", "mpeg2video", "-b:v", "500k", "-maxrate", "900k", "-bufsize", "800k",
            "-g", str(gop), "-keyint_min", str(gop), "-bf", "2", "-sc_threshold", "0", "-aspect", "4:3",
            "-c:a", "mp2", "-b:a", "128k", "-ar", "48000",
            # DVD 复用器写入标准的系统头与私有流结构，dvdauthor 才能定位 VOBU。
            "-muxrate", "10080k", "-f", "dvd", plain)
        if spu is None:
            return plain
        spu_png = self._subtitle_png(video_dir / f"{name}_sub.png", spu[0])
        sub_xml = video_dir / f"{name}_sub.xml"
        sub_xml.write_text(
            '<subpictures><stream>\n'
            f'<spu start="{spu[1]}" end="{spu[2]}" image="{spu_png}" '
            'xoffset="56" yoffset="176" transparent="black" force="yes" />\n'
            '</stream></subpictures>\n', encoding="utf-8")
        with open(plain, "rb") as source, open(video_dir / f"{name}.vob", "wb") as target:
            subprocess.run(["spumux", "-m", "dvd", "-s", "0", str(sub_xml)],
                           stdin=source, stdout=target, check=True)
        return video_dir / f"{name}.vob"

    def build_dvd(self):
        video_dir = self.work / "dvd_src"
        video_dir.mkdir(parents=True, exist_ok=True)
        # 352x240 是合法的 NTSC SIF 分辨率，样本体积小。
        title1 = self.encode_dvd_cell("title1", 10.0, "0x602020", 330, "TITLE 1",
                                      spu=("DVD SUB TITLE1", "00:00:01.50", "00:00:08.50"))
        # 三个 VOB 即三个 Cell；第二个 Cell 的字幕使用半透明强调色，覆盖透明度场景。
        cell2 = self.encode_dvd_cell("cell2", 8.0, "0x204080", 550, "CELL 2",
                                     spu=("DVD SUB CELL2", "00:00:01.50", "00:00:07.50"))
        cell3 = self.encode_dvd_cell("cell3", 8.0, "0xC0A020", 660, "CELL 3",
                                     spu=("DVD SUB CELL3", "00:00:01.50", "00:00:06.50"))
        # Cell 1 使用短 GOP + B 帧，覆盖下个 I 帧之后才输出中间 B 帧的 DVD 边界。
        cell1 = self.encode_dvd_cell("cell1", 8.0, "0x205020", 440, "CELL 1",
                                     spu=("DVD SUB CELL1", "00:00:01.50", "00:00:07.50"), gop=3)

        # PGC 调色板：条目 0 背景透明，1 白色，2 黑色描边，3 强调色。
        palette = video_dir / "palette.rgb"
        entries = ["000000"] * 16
        entries[1] = "ffffff"
        entries[2] = "000000"
        entries[3] = "40a0ff"
        palette.write_text("\n".join(entries) + "\n", encoding="utf-8")

        # 空流仅用于满足 vmgm 的 <vob> 需要一个文件名（不含实际内容）。
        self.ffmpeg(
            "-f", "lavfi", "-i", "color=c=black:s=352x240:r=30000/1001:d=0.2",
            "-f", "lavfi", "-i", "sine=frequency=330:sample_rate=48000:duration=0.2",
            "-c:v", "mpeg2video", "-aspect", "4:3", "-c:a", "mp2",
            "-muxrate", "10080k", "-f", "dvd", self.work / "dvd_src/empty.mpg")
        xml = ['<dvdauthor dest="' + str(self.work / "dvd") + '">']
        # 最小 VMG：dvdauthor 只有存在 vmgm 节时才会生成 VIDEO_TS.IFO。
        # 首播命令直接跳到标题 1，不涉及任何菜单交互。
        xml.append('  <vmgm>')
        xml.append('    <menus>')
        xml.append('      <pgc entry="title">')
        xml.append('        <pre> jump title 1; </pre>')
        xml.append('        <vob file="' + str(self.work / 'dvd_src/empty.mpg') + '" />')
        xml.append('      </pgc>')
        xml.append('    </menus>')
        xml.append('  </vmgm>')
        xml.append('  <titleset>')
        xml.append('    <titles>')
        xml.append('      <video format="ntsc" aspect="4:3" />')
        xml.append('      <audio format="mp2" channels="1" />')
        # SPU 在 VOB 内是 stream 0；<subpicture><stream id="0"/></subpicture>
        # 让 dvdauthor 把它写入 PGC 的 subp_control（否则轨道不可见）。
        xml.append('      <subpicture>')
        xml.append('        <stream id="0" mode="normal" />')
        xml.append('      </subpicture>')
        # SPU 在 VOB 内以 stream 0 存在；dvdauthor 需在 <vob> 内声明 subpicture
        # 才会写入 PGC 的 subp_control（否则轨道不可见）。
        xml.append(f'      <pgc palette="{palette}">')
        xml.append(f'        <vob file="{title1}" pause="0" />')
        xml.append('      </pgc>')
        xml.append(f'      <pgc palette="{palette}">')
        for vob in (cell1, cell2, cell3):
            xml.append(f'        <vob file="{vob}" pause="0" />')
        xml.append('      </pgc>')
        xml.append('    </titles>')
        xml.append('  </titleset>')
        xml.append('</dvdauthor>')
        author_xml = video_dir / "dvd.xml"
        author_xml.write_text("\n".join(xml) + "\n", encoding="utf-8")
        subprocess.run(["dvdauthor", "-x", str(author_xml)], check=True)
        # 保持 IFO 中逻辑扇区地址不变，把同一 VTS 的标题流分成两个实际 VOB 文件。
        # libdvdread 必须跨文件读取，不能仅凭多个 Cell 声称覆盖了跨 VOB。
        first_vob = self.work / "dvd/VIDEO_TS/VTS_01_1.VOB"
        data = first_vob.read_bytes()
        split = (len(data) // 2 // 2048) * 2048
        if split <= 0 or split >= len(data):
            raise RuntimeError("DVD fixture is too small to split")
        first_vob.write_bytes(data[:split])
        first_vob.with_name("VTS_01_2.VOB").write_bytes(data[split:])
        return self.work / "dvd"


def build_iso(source_dir, target):
    """把目录打包为 ISO9660+UDF 桥接镜像：libdvdread 的镜像模式只经 UDF 读文件，
    libbluray 的 udfread 亦然，因此两种样本都必须包含 UDF 结构。"""
    if target.exists():
        target.unlink()
    # 注意：libdvdread / udfread 依赖规范位置的 UDF AVDP 锚点。
    # hdiutil 的 UDF 布局未纳入此生成器验证，xorriso（libburnia）不支持写 UDF，
    # 因此使用 cdrtools 的 mkisofs 或 genisoimage（cdrkit）。
    tool = shutil.which("mkisofs") or shutil.which("genisoimage")
    if tool:
        subprocess.run([tool, "-iso-level", "3", "-volid", "TSCUTTER_TEST", "-udf",
                        "-o", str(target), str(source_dir)], check=True)
        return target
    raise SystemExit(
        "创建 ISO 镜像需要 cdrtools 的 mkisofs 或 genisoimage（支持 -udf）。"
        "macOS: brew install cdrtools；Debian/Ubuntu: apt install genisoimage；"
        "Windows: 安装 cdrtools（如 cygwin/MSYS2 提供）。"
        "此生成器仅验证 mkisofs / genisoimage 输出。")


def keep_disc_folder(source, target):
    """Keep the authored folder as a reusable input fixture alongside its ISO."""
    if target.is_dir():
        shutil.rmtree(target)
    source.rename(target)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", nargs="?", type=Path,
                        default=Path(__file__).resolve().parents[1] / "artifacts/test-media/iso")
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument("--ffprobe", default="ffprobe")
    parser.add_argument("--skip-bluray", action="store_true")
    parser.add_argument("--skip-dvd", action="store_true")
    args = parser.parse_args()

    root = args.directory.resolve()
    root.mkdir(parents=True, exist_ok=True)
    manifest = root / "manifest.json"
    manifest.unlink(missing_ok=True)
    # dvdauthor 会向已有 dest 追加新 VTS，重复生成会产生多余标题；
    # 每次重新生成前清空本工具自己的工作目录（只清理自己产出的目录）。
    work = root / "work"
    if work.is_dir():
        shutil.rmtree(work)

    if not args.skip_bluray:
        generator = Generator(args.ffmpeg, args.ffprobe, root)
        bluray_dir = generator.build_bd()
        build_iso(bluray_dir, root / "bluray.iso")
        keep_disc_folder(bluray_dir, root / "bluray")
        print(root / "bluray.iso")
    if not args.skip_dvd:
        generator = Generator(args.ffmpeg, args.ffprobe, root, require_h264=False)
        generator.work.mkdir(parents=True, exist_ok=True)
        dvd_dir = generator.build_dvd()
        build_iso(dvd_dir, root / "dvd.iso")
        keep_disc_folder(dvd_dir, root / "dvd")
        print(root / "dvd.iso")

    manifest.write_text(json.dumps({"formatVersion": 1}, indent=2) + "\n", encoding="utf-8")
    print(root)


if __name__ == "__main__":
    main()
