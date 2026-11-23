#!/usr/bin/env python3
"""Normalize GNU section metadata for Microsoft's COFF linker.

Only section-definition auxiliary records, section names and initializer flags change.
Code, relocations, archive indexes, symbol indexes and byte offsets remain unchanged. See PE/COFF's
IMAGE_COMDAT_SELECT_ASSOCIATIVE definition.
"""
from pathlib import Path
import struct


def normalize_object(data):
    data = bytearray(data)
    if len(data) < 20:
        return data, 0
    machine, count = struct.unpack_from("<HH", data)
    big = machine == 0 and count == 0xFFFF and len(data) >= 56
    if big:
        if struct.unpack_from("<H", data, 4)[0] < 2:
            return data, 0  # Short import-library member, not a bigobj.
        count, symbols, symbol_count = struct.unpack_from("<III", data, 44)
        header, stride = 56, 20
    elif machine in (0x8664, 0xAA64):
        # x86_64 and AArch64 objects share the classic COFF layout; the MinGW
        # aarch64 SDK needs the same COMDAT/section-name normalization as x64.
        symbols, symbol_count = struct.unpack_from("<II", data, 8)
        header, stride = 20 + struct.unpack_from("<H", data, 16)[0], 18
    else:
        return data, 0
    if not symbols or not symbol_count:
        return data, 0
    strings = symbols + symbol_count * stride
    if header + count * 40 > len(data):
        raise ValueError("Invalid COFF table bounds")
    # The string table is optional: clang omits it (or leaves a zeroed
    # placeholder) when an object only uses short section/symbol names, e.g.
    # resource-compiler objects. A real table is at least 4 bytes long.
    if strings + 4 <= len(data):
        declared = struct.unpack_from("<I", data, strings)[0]
        string_size = declared if declared >= 4 and strings + declared <= len(data) else 4
    else:
        string_size = 4

    def string(offset):
        start = strings + offset
        if start < strings + 4 or start >= strings + string_size:
            raise ValueError("Invalid COFF string offset")
        return bytes(data[start:data.index(0, start, strings + string_size)]).decode("ascii")

    sections = {}
    name_locations = []
    for index in range(1, count + 1):
        offset = header + (index - 1) * 40
        raw = bytes(data[offset:offset + 8]).rstrip(b"\0")
        name = string(int(raw[1:])) if raw.startswith(b"/") else raw.decode("ascii")
        name_locations.append((name, offset, strings + int(raw[1:]) if raw.startswith(b"/") else offset))
        sections[index] = (name, struct.unpack_from("<I", data, offset + 36)[0])
    by_name = {name: index for index, (name, _) in sections.items()}
    definitions, externals = {}, set()
    index = 0
    while index < symbol_count:
        offset = symbols + index * stride
        raw = bytes(data[offset:offset + 8])
        name = string(struct.unpack_from("<I", raw, 4)[0]) if raw[:4] == b"\0" * 4 else raw.rstrip(b"\0").decode("ascii")
        value = struct.unpack_from("<I", data, offset + 8)[0]
        section = struct.unpack_from("<i" if big else "<h", data, offset + 12)[0]
        symbol_type, storage, auxiliaries = struct.unpack_from("<HBB", data, offset + (16 if big else 14))
        if index + 1 + auxiliaries > symbol_count:
            raise ValueError("Invalid COFF auxiliary record count")
        if storage == 2 and section > 0:
            externals.add(section)
        if storage == 3 and auxiliaries == 1 and value == 0 and symbol_type == 0 and section in sections and name == sections[section][0]:
            definitions[section] = offset + stride
            name_locations.append((name, offset, strings + struct.unpack_from("<I", raw, 4)[0] if raw[:4] == b"\0" * 4 else offset))
        index += 1 + auxiliaries

    # GNU startup walks .ctors backwards; MSVC only executes .CRT$XC*
    # entries between XCA/XCZ. In particular, winpthreads' priority-0
    # constructor initializes the clock API pointers used by nanosleep.
    # Keep every replacement within its existing name field/string so archive
    # indexes and relocation offsets remain valid. Hex priorities retain the
    # GNU ordering while fitting the 12 bytes in .ctors.NNNNN.
    initializer_names = {}
    changed = 0
    for section, (name, flags) in sections.items():
        if name == ".ctors":
            initializer_names[name] = b".CRT$XCU"
        elif name == ".dtors":
            initializer_names[name] = b".CRT$XPT"
        elif name.startswith((".ctors.", ".dtors.")):
            suffix = name[len(".ctors."):]
            if len(suffix) != 5 or not suffix.isdigit() or int(suffix) > 65535:
                raise ValueError(f"Unsupported GNU initializer section: {name}")
            # GNU destructors run after atexit handlers in MSVC's pre-terminator
            # table, in the opposite priority order to the constructors.
            replacement = (f".CRT$XCT{65535 - int(suffix):04X}" if name.startswith(".ctors.")
                           else f".CRT$XPU{int(suffix):04X}")
            initializer_names[name] = replacement.encode("ascii")
        if name in initializer_names:
            # Match MSVC's read-only initializer sections; a writable .CRT
            # would be emitted separately and fall outside XCA/XCZ.
            offset = header + (section - 1) * 40 + 36
            struct.pack_into("<I", data, offset, (flags | 0x40000040) & ~0x80000000)
            changed += 1
        # MinGW's DEFINE_GUID uses SAME_SIZE, while the Windows SDK uses
        # selectany. Microsoft link rejects the differing selection modes.
        # Restrict this normalization to 16-byte GUID constants.
        auxiliary = definitions.get(section)
        constant = name.removeprefix(".rdata$")
        guid = constant.startswith(("IID_", "CLSID_", "GUID_", "FMTID_", "D3D11_DECODER_PROFILE_", "DXGI_DEBUG_", "WKPDID_")) or constant.endswith("_GUID")
        if flags & 0x1000 and auxiliary is not None and name.startswith(".rdata$") and guid:
            if data[auxiliary + 14] == 3 and struct.unpack_from("<I", data, auxiliary)[0] == 16:
                data[auxiliary + 14] = 2
                changed += 1
        if not flags & 0x1000 or section in externals or not name.startswith((".pdata$", ".xdata$")):
            continue
        auxiliary = definitions.get(section)
        parent = by_name.get(".text$" + name.split("$", 1)[1])
        if auxiliary is None or parent is None or not sections[parent][1] & 0x1000:
            raise ValueError(f"Cannot associate unwind section {name}")
        selection = data[auxiliary + 14]
        if selection == 5:
            continue
        if selection != 2:
            raise ValueError(f"Unsupported unwind COMDAT selection: {selection}")
        struct.pack_into("<H", data, auxiliary + 12, parent & 0xFFFF)
        struct.pack_into("<H", data, auxiliary + 16, parent >> 16 if big else 0)
        data[auxiliary + 14] = 5
        changed += 1
    # GNU uses dot-separated hot/cold function sections. MSVC groups sections
    # at '$', and otherwise emits one output section per full GNU name.
    # Replacing just the separator preserves all string-table byte offsets.
    for name, field_offset, offset in name_locations:
        if name in initializer_names:
            replacement = initializer_names[name]
            if len(replacement) <= 8:
                # Short names may also live in the string table. Use the
                # inline field so a longer replacement cannot overwrite the
                # following string or its terminator.
                data[field_offset:field_offset + 8] = replacement.ljust(8, b"\0")
            else:
                data[offset:offset + len(replacement)] = replacement
            continue
        for prefix in (".text.", ".data.", ".bss.", ".rdata."):
            if name.startswith(prefix):
                data[offset + len(prefix) - 1] = ord("$")
                break
    return data, changed


def normalize_archive(source, destination):
    data = bytearray(Path(source).read_bytes())
    if data[:8] != b"!<arch>\n":
        raise ValueError(f"Not a regular COFF archive: {source}")
    cursor, changed = 8, 0
    while cursor < len(data):
        if data[cursor + 58:cursor + 60] != b"`\n":
            raise ValueError("Invalid archive member header")
        size = int(data[cursor + 48:cursor + 58])
        start = cursor + 60
        if size < 0 or start + size > len(data):
            raise ValueError("Invalid archive member size")
        name = bytes(data[cursor:cursor + 16]).rstrip()
        if name not in (b"/", b"//", b"/SYM64/"):
            normalized, count = normalize_object(data[start:start + size])
            data[start:start + size] = normalized
            changed += count
        cursor = start + size + size % 2
    Path(destination).parent.mkdir(parents=True, exist_ok=True)
    Path(destination).write_bytes(data)
    return changed
