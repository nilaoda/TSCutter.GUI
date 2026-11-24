import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from generate_third_party_notices import generate_notices, SEPARATOR


class ThirdPartyNoticesTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.write("THIRD_PARTY_NOTICES.md", "\ufeffSummary / 声明\r\n")
        self.write("LICENSE", "Project GPL\n")
        self.write("THIRD_PARTY_LICENSES.txt", "\ufeffConsolidated licenses\r\n")
        self.output = self.root / "artifact/THIRD_PARTY_NOTICES.txt"

    def write(self, name, text):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(text.encode("utf-8"))
        return path

    def metadata(self, packs):
        return self.write("runtime-packs.json", "\ufeff" + json.dumps({"Items": {"ResolvedRuntimePack": packs}}))

    def test_consolidated_output_preserves_original_text_and_is_repeatable(self):
        self.write("design/reference/Original.cs.txt", "Excluded source")

        generate_notices(self.root, self.output)
        first = self.output.read_bytes()
        self.assertTrue(first.startswith("Summary / 声明\r\n".encode()))
        self.assertNotIn(b"\xef\xbb\xbf", first)
        self.assertIn(b"Consolidated licenses\r\n", first)
        self.assertIn(b"Project GPL\n", first)
        self.assertNotIn(b"Excluded", first)
        text = first.decode()
        titles = ["Third-party licenses", "TSCutter.GUI / FFmpegSharedLibraries — GPL v3"]
        positions = [text.index(f"{SEPARATOR}\n{title}\n{SEPARATOR}") for title in titles]
        self.assertEqual(positions, sorted(positions))
        generate_notices(self.root, self.output)
        self.assertEqual(first, self.output.read_bytes())

    def test_resolved_runtimes_are_appended_and_unknown_frameworks_are_ignored(self):
        self.write("runtime pack/LICENSE.TXT", "Actual runtime license\r\n")
        self.write("runtime pack/THIRD-PARTY-NOTICES.TXT", "Actual runtime notices\n")
        self.write("aspnet pack/LICENSE.txt", "ASP.NET Core license\n")
        self.write("aspnet pack/THIRD-PARTY-NOTICES.TXT", "ASP.NET Core notices\n")
        metadata = self.metadata([
            {"FrameworkName": "Custom.Framework", "PackageDirectory": "unused"},
            {"FrameworkName": "Microsoft.AspNetCore.App", "PackageDirectory": str(self.root / "aspnet pack")},
            {"FrameworkName": "Microsoft.NETCore.App", "PackageDirectory": str(self.root / "runtime pack")},
        ])
        def open_case_sensitive(path, *args, **kwargs):
            # Exercise Linux filename matching even on a case-insensitive host.
            if path.name not in {entry.name for entry in path.parent.iterdir()}:
                raise FileNotFoundError(path)
            return open(path, *args, **kwargs)

        with patch.object(Path, "open", open_case_sensitive):
            generate_notices(self.root, self.output, metadata)
        result = self.output.read_bytes()
        self.assertIn(b"Actual runtime license\r\n", result)
        self.assertIn(b"Actual runtime notices\n", result)
        self.assertIn(b"ASP.NET Core license\n", result)
        self.assertIn(b"ASP.NET Core notices\n", result)
        self.assertIn(b"Consolidated licenses\r\n", result)

    def test_incomplete_runtime_fails_without_overwriting_output(self):
        self.output.parent.mkdir()
        self.output.write_bytes(b"Existing output")
        missing_pack = {"FrameworkName": "Microsoft.NETCore.App", "PackageDirectory": str(self.root / "missing")}
        for packs, error in [([], ValueError), ([missing_pack, missing_pack], ValueError),
                             ([missing_pack], FileNotFoundError)]:
            with self.subTest(packs=packs):
                with self.assertRaises(error):
                    generate_notices(self.root, self.output, self.metadata(packs))
                self.assertEqual(b"Existing output", self.output.read_bytes())


if __name__ == "__main__":
    unittest.main()
