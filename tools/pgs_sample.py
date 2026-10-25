"""Shared synthetic PGS fixture with cached objects, palette updates, and clears."""
from pathlib import Path
import struct


def write_pgs_sample(path: Path):
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
    # 同一个 RLE 对象拆成首片和续片，覆盖大字幕对象的分段显示集合。
    first = obj[:3] + bytes([0x80]) + obj[4:283]
    last = obj[:3] + bytes([0x40]) + obj[283:]
    pgs += segment(0x15, first, 1) + segment(0x15, last, 1) + segment(0x80, b"", 1)
    pgs += segment(0x16, presentation(1, 0, 0), 3) + segment(0x80, b"", 3)
    # 普通显示集合复用首个 epoch 的对象，随后只更新一个调色板条目。
    pgs += segment(0x16, presentation(2, 1, 0), 4) + segment(0x80, b"", 4)
    pgs += segment(0x16, presentation(3, 1, 0), 5)
    pgs += segment(0x14, bytes([0, 1, 1, 81, 128, 128, 255]), 5) + segment(0x80, b"", 5)
    pgs += segment(0x16, presentation(4, 0, 0), 7) + segment(0x80, b"", 7)
    path.write_bytes(pgs)
