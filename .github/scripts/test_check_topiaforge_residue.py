#!/usr/bin/env python3
"""Focused integration tests for generated-payload residue scanning."""

from __future__ import annotations

import io
import subprocess
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path


AUDIT = Path(__file__).with_name("check_topiaforge_residue.py")


def zip_bytes(entries: dict[str, bytes]) -> bytes:
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for name, data in entries.items():
            archive.writestr(name, data)
    return output.getvalue()


class GeneratedPayloadAuditTests(unittest.TestCase):
    def run_audit(self, include: Path) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [sys.executable, str(AUDIT), "--include", str(include)],
            check=False,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )

    def test_clean_nested_package_passes(self) -> None:
        package = zip_bytes(
            {
                "topiaforge.mod.json": b'{"schemaVersion":3,"id":"example.clean"}',
                "lib/TopiaForge.Example.dll": b"\0TopiaForge.Example\0",
            }
        )
        with tempfile.TemporaryDirectory() as temporary_directory:
            archive = Path(temporary_directory) / "TopiaForge-test.zip"
            archive.write_bytes(zip_bytes({"dist/example.topiaforgemod": package}))

            result = self.run_audit(archive)

        self.assertEqual(0, result.returncode, result.stderr)

    def test_retired_brand_in_nested_package_fails_with_member_chain(self) -> None:
        retired_brand = "Quantum" + "Works"
        manifest = f'{{"schemaVersion":3,"publisher":"{retired_brand}"}}'.encode()
        package = zip_bytes({"topiaforge.mod.json": manifest})
        with tempfile.TemporaryDirectory() as temporary_directory:
            archive = Path(temporary_directory) / "TopiaForge-test.zip"
            archive.write_bytes(zip_bytes({"dist/example.topiaforgemod": package}))

            result = self.run_audit(archive)

        self.assertEqual(1, result.returncode)
        self.assertIn(
            "TopiaForge-test.zip!dist/example.topiaforgemod!topiaforge.mod.json",
            result.stderr,
        )
        self.assertIn("retired " + retired_brand + " brand", result.stderr)

    def test_retired_archive_member_path_fails(self) -> None:
        retired_member = "mods/" + "Robotopia" + ".Example/readme.txt"
        with tempfile.TemporaryDirectory() as temporary_directory:
            archive = Path(temporary_directory) / "TopiaForge-test.zip"
            archive.write_bytes(zip_bytes({retired_member: b"example"}))

            result = self.run_audit(archive)

        self.assertEqual(1, result.returncode)
        self.assertIn(retired_member, result.stderr)
        self.assertIn(
            "retired " + "Robotopia" + " ecosystem name in path",
            result.stderr,
        )

    def test_bare_retired_abbreviation_fails_for_text(self) -> None:
        retired_abbreviation = "Q" + "w"
        with tempfile.TemporaryDirectory() as temporary_directory:
            generated = Path(temporary_directory) / "readme.txt"
            generated.write_text(f"Use {retired_abbreviation} for the UI.", encoding="utf-8")

            result = self.run_audit(generated)

        self.assertEqual(1, result.returncode)
        self.assertIn(
            "retired " + retired_abbreviation + " abbreviation",
            result.stderr,
        )

    def test_target_game_keyword_is_allowed_in_unity_package_manifest(self) -> None:
        target_game = "robo" + "topia"
        with tempfile.TemporaryDirectory() as temporary_directory:
            package_manifest = Path(temporary_directory) / "package.json"
            package_manifest.write_text(
                '{"keywords":["' + target_game + '"]}', encoding="utf-8"
            )

            result = self.run_audit(package_manifest)

        self.assertEqual(0, result.returncode, result.stderr)

    def test_target_game_keyword_is_allowed_in_generated_vpm_index(self) -> None:
        target_game = "robo" + "topia"
        with tempfile.TemporaryDirectory() as temporary_directory:
            index = Path(temporary_directory) / "vpm" / "index.json"
            index.parent.mkdir()
            index.write_text(
                '{"packages":{"example":{"versions":{"1.0.0":'
                '{"keywords":["'
                + target_game
                + '"]}}}}}',
                encoding="utf-8",
            )

            result = self.run_audit(index.parent.parent)

        self.assertEqual(0, result.returncode, result.stderr)

    def test_target_game_tool_paths_are_allowed_under_package_root(self) -> None:
        package_root = "TopiaForge-linux-x64/tools"
        with tempfile.TemporaryDirectory() as temporary_directory:
            archive = Path(temporary_directory) / "TopiaForge-test.zip"
            archive.write_bytes(
                zip_bytes(
                    {
                        f"{package_root}/restore-robotopia-managed-refs.ps1": b"",
                        f"{package_root}/test-restore-robotopia-managed-refs.ps1": b"",
                    }
                )
            )

            result = self.run_audit(archive)

        self.assertEqual(0, result.returncode, result.stderr)

    def test_target_game_tool_allowlist_does_not_hide_retired_prefix(self) -> None:
        retired_prefix = "Legacy" + "Robotopia"
        member = f"{retired_prefix}/tools/restore-robotopia-managed-refs.ps1"
        with tempfile.TemporaryDirectory() as temporary_directory:
            archive = Path(temporary_directory) / "TopiaForge-test.zip"
            archive.write_bytes(zip_bytes({member: b""}))

            result = self.run_audit(archive)

        self.assertEqual(1, result.returncode)
        self.assertIn(
            "retired " + "Robotopia" + " ecosystem name in path",
            result.stderr,
        )

    def test_native_executable_qw_byte_collision_is_not_an_identifier(self) -> None:
        native_collisions = (b"Q" + b"wYw", b"Q" + b"wYw6")
        with tempfile.TemporaryDirectory() as temporary_directory:
            archive = Path(temporary_directory) / "TopiaForge-test.zip"
            archive.write_bytes(
                zip_bytes(
                    {
                        "TopiaForge-linux-x64/TopiaForge.GameCompat.Extractor": (
                            b"\0" + native_collisions[0] + b"\0"
                        ),
                        "TopiaForge-macos-universal/TopiaForge.GameCompat.Extractor": (
                            b"\0" + native_collisions[1] + b"\0"
                        ),
                    }
                )
            )

            result = self.run_audit(archive)

        self.assertEqual(0, result.returncode, result.stderr)

    def test_real_qw_identifier_in_binary_still_fails(self) -> None:
        retired_identifier = b"Q" + b"wGap"
        with tempfile.TemporaryDirectory() as temporary_directory:
            archive = Path(temporary_directory) / "TopiaForge-test.zip"
            archive.write_bytes(
                zip_bytes({"lib/Legacy.dll": b"\0" + retired_identifier + b"\0"})
            )

            result = self.run_audit(archive)

        self.assertEqual(1, result.returncode)
        self.assertIn("retired " + "Q" + "w-prefixed identifier", result.stderr)

    def test_missing_include_is_an_actionable_tool_error(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            missing = Path(temporary_directory) / "missing.zip"
            result = self.run_audit(missing)

        self.assertEqual(2, result.returncode)
        self.assertIn("requested --include path does not exist", result.stderr)


if __name__ == "__main__":
    unittest.main()
