import struct
import tempfile
import unittest
from pathlib import Path
from coff import normalize_archive, normalize_object


def fixture(big=False, machine=0x8664):
    header, stride = (56, 20) if big else (20, 18)
    symbols = header + 80 + 5
    data = bytearray(symbols)
    if big:
        struct.pack_into("<HHHH", data, 0, 0, 0xFFFF, 2, machine)
        struct.pack_into("<III", data, 44, 2, symbols, 5)
    else:
        struct.pack_into("<HHIIIHH", data, 0, machine, 2, 0, symbols, 5, 0, 0)
    for index, name in enumerate((b".text$f", b".xdata$f")):
        offset = header + index * 40
        data[offset:offset + 8] = name.ljust(8, b"\0")
        struct.pack_into("<I", data, offset + 36, 0x60101020 if index == 0 else 0x40301040)
    data[header + 80:] = b"\xc3\x01\x00\x00\x00"
    symbol = lambda name, section, storage, aux: struct.pack(
        "<8sIiHBB" if big else "<8sIhHBB", name.ljust(8, b"\0"), 0, section, 0, storage, aux)
    auxiliary = bytearray(stride)
    auxiliary[14] = 2  # IMAGE_COMDAT_SELECT_ANY
    data += symbol(b".text$f", 1, 3, 1) + auxiliary + symbol(b"f", 1, 2, 0)
    data += symbol(b".xdata$f", 2, 3, 1) + auxiliary + struct.pack("<I", 4)
    return data, symbols + 4 * stride, symbols


class NormalizeCoffTests(unittest.TestCase):
    def test_unwind_follows_code_in_regular_and_big_objects(self):
        for big in (False, True):
            for machine in (0x8664, 0xAA64):
                original, auxiliary, symbols = fixture(big, machine)
                result, count = normalize_object(original)
                self.assertEqual(1, count)
                self.assertEqual(5, result[auxiliary + 14])
                self.assertEqual(1, struct.unpack_from("<H", result, auxiliary + 12)[0])
                self.assertEqual(original[:symbols], result[:symbols])

    def test_resource_object_without_string_table_passes_through(self):
        # clang emits resource-compiler objects with only short names and no
        # string table; normalization must accept them without raising. The
        # table may be absent entirely or replaced by a zeroed placeholder.
        for machine in (0x8664, 0xAA64):
            original, _, symbols = fixture(machine=machine)
            stride = 18
            symbol_count = 5
            for trailer in (b"", b"\0\0\0\0"):
                truncated = bytearray(original[:symbols + symbol_count * stride]) + bytearray(trailer)
                result, count = normalize_object(truncated)
                self.assertEqual(1, count)  # the COMDAT unwind fix still applies
                self.assertEqual(len(truncated), len(result))

    def test_repeated_normalization_is_identical(self):
        for machine in (0x8664, 0xAA64):
            first, _, _ = fixture(machine=machine)
            normalized, _ = normalize_object(first)
            repeated, count = normalize_object(normalized)
            self.assertEqual((normalized, 0), (repeated, count))

    def test_guid_selectany_does_not_relax_unrelated_data(self):
        for name, expected in ((b".rdata$D3D11_DECODER_PROFILE_TEST", 2), (b".rdata$unrelated", 3)):
            for machine in (0x8664, 0xAA64):
                obj, auxiliary, symbols = fixture(machine=machine)
                obj[-4:] = struct.pack("<I", 4 + len(name) + 1)
                obj += name + b"\0"
                obj[60:68] = b"/4".ljust(8, b"\0")
                obj[symbols + 54:symbols + 62] = struct.pack("<II", 0, 4)
                struct.pack_into("<I", obj, auxiliary, 16)
                obj[auxiliary + 14] = 3
                normalized, _ = normalize_object(obj)
                self.assertEqual(expected, normalized[auxiliary + 14])

    def test_gnu_initializers_become_readonly_msvc_tables(self):
        for machine in (0x8664, 0xAA64):
            for name, expected in ((b".ctors", b".CRT$XCU"),
                                   (b".ctors.65535", b".CRT$XCT0000"),
                                   (b".ctors.65434", b".CRT$XCT0065"),
                                   (b".dtors", b".CRT$XPT"),
                                   (b".dtors.00000", b".CRT$XPU0000"),
                                   (b".dtors.65535", b".CRT$XPUFFFF")):
                obj, auxiliary, symbols = fixture(machine=machine)
                # Replace the second section; retain the original code bytes.
                section_name = 60
                symbol_name = symbols + 54
                if len(name) <= 8:
                    obj[section_name:section_name + 8] = name.ljust(8, b"\0")
                    obj[symbol_name:symbol_name + 8] = name.ljust(8, b"\0")
                    locations = (section_name, symbol_name)
                else:
                    obj[-4:] = struct.pack("<I", 4 + len(name) + 1)
                    obj += name + b"\0"
                    obj[section_name:section_name + 8] = b"/4".ljust(8, b"\0")
                    obj[symbol_name:symbol_name + 8] = struct.pack("<II", 0, 4)
                    locations = (symbols + 5 * 18 + 4,)
                struct.pack_into("<I", obj, section_name + 36, 0xC0400040)
                normalized, count = normalize_object(obj)
                self.assertEqual(1, count)
                self.assertEqual(len(obj), len(normalized))
                for offset in locations:
                    self.assertEqual(expected, normalized[offset:offset + len(expected)])
                self.assertEqual(0x40400040, struct.unpack_from("<I", normalized, section_name + 36)[0])
                repeated, count = normalize_object(normalized)
                self.assertEqual((normalized, 0), (repeated, count))

    def test_short_indirect_initializer_preserves_adjacent_strings(self):
        for name, expected in ((b".ctors", b".CRT$XCU"), (b".dtors", b".CRT$XPT")):
            obj, _, symbols = fixture()
            strings = name + b"\0neighbor\0"
            obj[-4:] = struct.pack("<I", 4 + len(strings))
            obj += strings
            obj[60:68] = b"/4".ljust(8, b"\0")
            symbol_name = symbols + 54
            obj[symbol_name:symbol_name + 8] = struct.pack("<II", 0, 4)
            normalized, count = normalize_object(obj)
            self.assertEqual(1, count)
            self.assertEqual(len(obj), len(normalized))
            self.assertEqual(strings, normalized[-len(strings):])
            self.assertEqual(expected, normalized[60:68])
            self.assertEqual(expected, normalized[symbol_name:symbol_name + 8])
            self.assertEqual((normalized, 0), normalize_object(normalized))

    def test_unsupported_machine_passes_through(self):
        original, _, _ = fixture(machine=0x01C0)  # ARM (32-bit), not part of the SDK set
        result, count = normalize_object(original)
        self.assertEqual(0, count)
        self.assertEqual(original, result)

    def test_archive_keeps_member_header_and_offsets(self):
        obj, _, _ = fixture()
        member = b"sample.o/".ljust(16) + b"0".ljust(12) + b"0".ljust(6) * 2 + b"644".ljust(8) + str(len(obj)).encode().ljust(10) + b"`\n"
        archive = b"!<arch>\n" + member + obj + (b"\n" if len(obj) % 2 else b"")
        with tempfile.TemporaryDirectory() as directory:
            source, target = Path(directory) / "in.a", Path(directory) / "out.a"
            source.write_bytes(archive)
            self.assertEqual(1, normalize_archive(source, target))
            self.assertEqual(archive[:68], target.read_bytes()[:68])
            self.assertEqual(len(archive), target.stat().st_size)


if __name__ == "__main__":
    unittest.main()
