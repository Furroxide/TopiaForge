"""Exercise analyzer acquisition without fetching the upstream archive."""

import hashlib
import os
from pathlib import Path
import shutil
import stat
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import zipfile

import prepare_psscriptanalyzer as analyzer


class ArchiveTests(unittest.TestCase):
    def setUp(self):
        self.root = Path(self.enterContext(tempfile.TemporaryDirectory()))
        self.fixture = self.root / "fixture.zip"
        self.archive = self.root / "cache" / analyzer.ARCHIVE_NAME
        self.downloader = self.enterContext(patch.object(analyzer, "download"))
        self.downloader.side_effect = lambda destination: shutil.copyfile(self.fixture, destination)

    def fixture_archive(self, extra=None):
        with zipfile.ZipFile(self.fixture, "w") as package:
            package.writestr(analyzer.MANIFEST, "@{ ModuleVersion = '1.25.0' }")
            if extra is not None:
                package.writestr(extra, "unexpected")
        if extra == "dir!escape":
            # ZipFile normalizes backslashes on Windows when writing.
            self.fixture.write_bytes(self.fixture.read_bytes().replace(b"dir!escape", b"dir\\escape"))
        self.enterContext(patch.object(analyzer, "ARCHIVE_SIZE", self.fixture.stat().st_size))
        self.enterContext(patch.object(analyzer, "SHA256", hashlib.sha256(self.fixture.read_bytes()).hexdigest()))

    def test_cold_then_warm_verifies_and_extracts_into_distinct_directories(self):
        self.fixture_archive()
        first = analyzer.prepare(self.archive, self.root)
        second = analyzer.prepare(self.archive, self.root)
        self.assertNotEqual(first, second)
        self.assertEqual(first.read_bytes(), second.read_bytes())
        self.assertEqual(self.archive.read_bytes(), self.fixture.read_bytes())
        self.downloader.assert_called_once()

    def test_corrupt_cache_fails_without_download(self):
        self.fixture_archive()
        self.archive.parent.mkdir()
        content = self.fixture.read_bytes()
        self.archive.write_bytes(content[:-1] + bytes([content[-1] ^ 1]))
        with self.assertRaisesRegex(ValueError, "Cached .* SHA-256"):
            analyzer.prepare(self.archive, self.root)
        self.downloader.assert_not_called()

    def test_failed_download_does_not_publish_partial_archive(self):
        def timeout(destination):
            destination.write_bytes(b"partial")
            raise subprocess.TimeoutExpired("curl", 65)

        self.downloader.side_effect = timeout
        with self.assertRaises(subprocess.TimeoutExpired):
            analyzer.prepare(self.archive, self.root)
        self.assertEqual(list(self.archive.parent.iterdir()), [])

    def test_reported_cache_hit_without_archive_fails_without_download(self):
        with self.assertRaisesRegex(ValueError, "Cache hit .* did not restore"):
            analyzer.prepare(self.archive, self.root, expect_cache=True)
        self.downloader.assert_not_called()

    def test_rejects_unsafe_entries_before_extracting(self):
        link = zipfile.ZipInfo("link")
        link.create_system = 3
        link.external_attr = stat.S_IFLNK << 16
        for entry in ("../escape", "/absolute", "C:/drive", "dir!escape", link, analyzer.MANIFEST):
            with self.subTest(entry=entry), self.assertRaisesRegex(ValueError, "Unsafe .* entry"):
                if entry == analyzer.MANIFEST:
                    with self.assertWarns(UserWarning):
                        self.fixture_archive(entry)
                else:
                    self.fixture_archive(entry)
                analyzer.prepare(self.archive, self.root)
        self.assertFalse(self.archive.exists())
        self.assertFalse((self.root / "escape").exists())


@unittest.skipUnless("ANALYZER_MANIFEST" in os.environ, "requires an analyzer manifest")
class AnalyzerInvocationTests(unittest.TestCase):
    def test_clean_script_passes_and_finding_fails_with_the_expected_rule(self):
        script = Path(__file__).with_name("Invoke-RepositoryScriptAnalysis.ps1")
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            fixture = root / "fixture.ps1"
            competing_module = root / "PSScriptAnalyzer"
            competing_module.mkdir()
            (competing_module / "PSScriptAnalyzer.psm1").write_text(
                "function Invoke-ScriptAnalyzer { throw 'Competing module was invoked' }\n",
                encoding="utf-8",
            )
            environment = os.environ.copy()
            environment["PSModulePath"] = str(root) + os.pathsep + environment.get("PSModulePath", "")
            invocation = [
                "pwsh", "-NoProfile", "-NonInteractive", "-File", str(script),
                "-ExpectedVersion", environment["ANALYZER_VERSION"], "-Path", str(fixture),
            ]
            for content, exit_code in (("Write-Output 'clean'\n", 0), ("$unused = 1\n", 1)):
                with self.subTest(exit_code=exit_code):
                    fixture.write_text(content, encoding="utf-8")
                    result = subprocess.run(
                        [*invocation, "-Manifest", environment["ANALYZER_MANIFEST"]],
                        env=environment, capture_output=True, text=True, timeout=30,
                    )
                    self.assertEqual(result.returncode, exit_code, result.stdout + result.stderr)
                    self.assertEqual(result.stderr, "")
                    if exit_code:
                        self.assertIn("PSUseDeclaredVarsMoreThanAssignments", result.stdout)
            result = subprocess.run(
                [*invocation, "-Manifest", str(root / "missing.psd1")],
                env=environment, capture_output=True, text=True, timeout=30,
            )
            self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
            self.assertIn("missing.psd1", result.stderr)


if __name__ == "__main__":
    unittest.main()
