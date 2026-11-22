import hashlib
import json
from pathlib import Path
import shlex
import tempfile
import unittest
import xml.etree.ElementTree as ET
from prepare import LIBRARIES, prepare


class GraphicsSdkTests(unittest.TestCase):
    def make_sdk(self, root, rid):
        sdk = root / "sdk with spaces"
        (sdk / "lib").mkdir(parents=True)
        manifest = {"rid": rid, "components": {
            "skia": {"version": "2.88.9"}, "harfbuzz": {"binding_version": "8.3.1.1"},
            "avalonia": {"version": "11.3.23"}, "angle": {"version": "chromium/7151"}},
            "sha256": {}, "link": {"skia": {"libs": [], "frameworks": []}}}
        for name in LIBRARIES[rid]:
            data = b"!<arch>\n" + name.encode()
            (sdk / "lib" / name).write_bytes(data)
            manifest["sha256"][name] = hashlib.sha256(data).hexdigest()
        if rid == "osx-arm64":
            (sdk / "build").mkdir()
            manifest["support_sha256"] = {}
            for name in ("StaticSkia.targets", "StaticSkiaLoader.c"):
                data = name.encode()
                (sdk / "build" / name).write_bytes(data)
                manifest["support_sha256"]["build/" + name] = hashlib.sha256(data).hexdigest()
        return sdk, manifest

    def save(self, sdk, manifest):
        (sdk / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")

    def test_windows_emits_all_archives_and_system_libraries(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            sdk, manifest = self.make_sdk(root, "win-x64")
            manifest["link"]["skia"]["libs"] = ["user32.lib"]
            self.save(sdk, manifest)
            prepare(sdk, root / "output", "win-x64")
            items = ET.parse(root / "output/NativeLibraries.props").findall("ItemGroup/NativeLibrary")
            self.assertEqual(6, len(items))
            self.assertEqual("user32.lib", items[-1].attrib["Include"])

    def test_corrupt_archives_and_wrong_architectures_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            sdk, manifest = self.make_sdk(root, "linux-x64")
            self.save(sdk, manifest)
            with self.assertRaises(ValueError):
                prepare(sdk, root / "output", "osx-arm64")
            (sdk / "lib/libskia.a").write_bytes(b"corrupt")
            with self.assertRaises(ValueError):
                prepare(sdk, root / "output", "linux-x64")

    def test_linux_requires_bundled_fontconfig_instead_of_dynamic_linking(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            sdk, manifest = self.make_sdk(root, "linux-x64")
            manifest["link"]["skia"]["libs"] = ["fontconfig", "pthread", "dl"]
            self.save(sdk, manifest)
            with self.assertRaises(ValueError):
                prepare(sdk, root / "output", "linux-x64")
            ffmpeg = root / "ffmpeg"
            (ffmpeg / "lib").mkdir(parents=True)
            (ffmpeg / "lib/libfontconfig.a").touch()
            prepare(sdk, root / "output", "linux-x64", ffmpeg)
            flags = shlex.split((root / "output/graphics-link.rsp").read_text())
            self.assertEqual(["-lpthread", "-ldl"], flags)

    def test_macos_rejects_corrupted_static_metal_support(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            sdk, manifest = self.make_sdk(root, "osx-arm64")
            self.save(sdk, manifest)
            (sdk / "build/StaticSkiaLoader.c").write_bytes(b"corrupt")
            with self.assertRaises(ValueError):
                prepare(sdk, root / "output", "osx-arm64")

    def test_macos_frameworks_are_normalized_and_retention_goes_to_linker(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            sdk, manifest = self.make_sdk(root, "osx-arm64")
            manifest["link"]["skia"]["frameworks"] = ["AppKit.framework", "AppKit"]
            self.save(sdk, manifest)
            prepare(sdk, root / "output", "osx-arm64")
            self.assertEqual(["-framework", "AppKit", "-Wl,-ObjC"],
                             shlex.split((root / "output/graphics-link.rsp").read_text()))


if __name__ == "__main__":
    unittest.main()
