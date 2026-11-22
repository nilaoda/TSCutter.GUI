import struct
import tempfile
import unittest
from pathlib import Path
from coff import normalize_archive, normalize_object


def fixture(big=False):
    header, stride = (56, 20) if big else (20, 18)
    symbols = header + 80 + 5
    data = bytearray(symbols)
    if big:
        struct.pack_into("<HHHH", data, 0, 0, 0xFFFF, 2, 0x8664)
        struct.pack_into("<III", data, 44, 2, symbols, 5)
    else:
        struct.pack_into("<HHIIIHH", data, 0, 0x8664, 2, 0, symbols, 5, 0, 0)
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
            original, auxiliary, symbols = fixture(big)
            result, count = normalize_object(original)
            self.assertEqual(1, count)
            self.assertEqual(5, result[auxiliary + 14])
            self.assertEqual(1, struct.unpack_from("<H", result, auxiliary + 12)[0])
            self.assertEqual(original[:symbols], result[:symbols])
            self.assertEqual(len(original), len(result))

    def test_repeated_normalization_is_identical(self):
        first, _, _ = fixture()
        normalized, _ = normalize_object(first)
        repeated, count = normalize_object(normalized)
        self.assertEqual((normalized, 0), (repeated, count))

    def test_guid_selectany_does_not_relax_unrelated_data(self):
        for name, expected in ((b".rdata$D3D11_DECODER_PROFILE_TEST", 2), (b".rdata$unrelated", 3)):
            obj, auxiliary, symbols = fixture()
            obj[-4:] = struct.pack("<I", 4 + len(name) + 1)
            obj += name + b"\0"
            obj[60:68] = b"/4".ljust(8, b"\0")
            obj[symbols + 54:symbols + 62] = struct.pack("<II", 0, 4)
            struct.pack_into("<I", obj, auxiliary, 16)
            obj[auxiliary + 14] = 3
            normalized, _ = normalize_object(obj)
            self.assertEqual(expected, normalized[auxiliary + 14])

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
