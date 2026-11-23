import struct
import tempfile
import unittest
from pathlib import Path
from verify import reject_gnu_initializers


class WindowsInitializerTests(unittest.TestCase):
    def check_sections(self, names):
        pe, optional = 0x80, 0xF0
        start = pe + 24 + optional
        data = bytearray(start + len(names) * 40)
        data[:2] = b"MZ"
        struct.pack_into("<I", data, 60, pe)
        data[pe:pe + 4] = b"PE\0\0"
        struct.pack_into("<H", data, pe + 6, len(names))
        struct.pack_into("<H", data, pe + 20, optional)
        for index, name in enumerate(names):
            data[start + index * 40:start + index * 40 + 8] = name.ljust(8, b"\0")
        with tempfile.TemporaryDirectory() as directory:
            executable = Path(directory) / "probe.exe"
            executable.write_bytes(data)
            reject_gnu_initializers(executable)

    def test_msvc_tables_and_tls_are_allowed(self):
        self.check_sections([b".text", b".rdata", b".CRT", b".tls"])

    def test_unexecuted_gnu_tables_are_rejected(self):
        for name in (b".ctors", b".ctors.6", b".dtors", b".init_ar", b".fini_ar"):
            with self.subTest(name=name), self.assertRaisesRegex(RuntimeError, "GNU initializers"):
                self.check_sections([b".text", name])

    def test_malformed_executable_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            executable = Path(directory) / "broken.exe"
            executable.write_bytes(b"MZ")
            with self.assertRaisesRegex(RuntimeError, "Invalid Windows PE"):
                reject_gnu_initializers(executable)
