from pathlib import Path
import shlex
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET
from unix import prepare, resolve_flags
from verify import dependency_names, reject_sdk_shared_libraries


class UnixStaticLinkTests(unittest.TestCase):
    def test_linux_uses_exact_archives_and_rescans_circular_dependencies(self):
        with tempfile.TemporaryDirectory(prefix="sdk with spaces ") as directory:
            sdk = Path(directory)
            (sdk / "lib").mkdir()
            for name in ("avcodec", "ass"):
                (sdk / "lib" / f"lib{name}.a").touch()
            runtime = lambda name: sdk / "compiler" / f"lib{name}.a"
            flags, archives = resolve_flags(["-L/old/runner", "-lavcodec", "-lass", "-lm", "-lstdc++", "-lgcc_s", "-pthread"], sdk, "linux", runtime)
            self.assertEqual("-Wl,--start-group", flags[0])
            self.assertEqual("-Wl,--end-group", flags[-1])
            self.assertIn(str((sdk / "lib/libass.a").resolve()), flags)
            self.assertIn(sdk / "compiler/libgcc_eh.a", archives)
            self.assertIn(sdk / "compiler/libstdc++.a", archives)
            self.assertNotIn("-lass", flags)

    def test_macos_preserves_frameworks_and_system_cpp_runtime(self):
        with tempfile.TemporaryDirectory() as directory:
            flags, _ = resolve_flags(["-framework", "CoreVideo", "-lc++", "-lm"], Path(directory), "macos")
            self.assertEqual(["-framework", "CoreVideo", "-lc++", "-lm"], flags)
            with self.assertRaises(ValueError):
                resolve_flags(["-framework"], Path(directory), "macos")
            with self.assertRaises(FileNotFoundError):
                resolve_flags(["-lass"], Path(directory), "macos")

    def test_elf_and_macho_dependencies_detect_dynamic_sdk_libraries(self):
        elf = "0x0000000000000001 (NEEDED) Shared library: [libavcodec.so.63]"
        macho = "app:\n\t@rpath/libass.9.dylib (compatibility version 9.0.0)\n\t/usr/lib/libSystem.B.dylib (current version 1.0.0)"
        self.assertEqual(["libavcodec.so.63"], dependency_names(elf, "linux"))
        with tempfile.TemporaryDirectory() as directory:
            sdk = Path(directory)
            (sdk / "lib").mkdir()
            (sdk / "lib/libass.a").touch()
            with self.assertRaises(RuntimeError):
                reject_sdk_shared_libraries(dependency_names(macho, "macos"), sdk)
            reject_sdk_shared_libraries(["libHarfBuzzSharp.so", "/usr/lib/libSystem.B.dylib"], sdk)

    def test_response_file_quotes_paths_and_tracks_link_inputs(self):
        with tempfile.TemporaryDirectory(prefix="sdk with spaces ") as directory:
            sdk = Path(directory)
            (sdk / "lib").mkdir()
            archive = sdk / "lib/libavcodec.a"
            archive.touch()
            with patch("unix.subprocess.check_output", return_value="-lavcodec -framework CoreVideo -lc++"):
                prepare(sdk, sdk, "macos")
            response = sdk / "ffmpeg-link.rsp"
            self.assertEqual([str(archive.resolve()), "-framework", "CoreVideo", "-lc++"], shlex.split(response.read_text()))
            project = ET.parse(sdk / "NativeLibraries.props")
            self.assertEqual(str(response), project.findtext("PropertyGroup/FFmpegStaticResponseFile"))
            self.assertIn("$(FFmpegStaticResponseFile)", project.find("ItemGroup/NativeLibrary").attrib["Include"])


if __name__ == "__main__":
    unittest.main()
