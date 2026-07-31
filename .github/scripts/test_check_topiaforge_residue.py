#!/usr/bin/env python3
"""Focused integration tests for generated-payload residue scanning."""

from __future__ import annotations

import importlib.util
import io
import subprocess
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path


AUDIT = Path(__file__).with_name("check_topiaforge_residue.py")
AUDIT_SPEC = importlib.util.spec_from_file_location("topiaforge_residue_audit", AUDIT)
assert AUDIT_SPEC is not None and AUDIT_SPEC.loader is not None
AUDIT_MODULE = importlib.util.module_from_spec(AUDIT_SPEC)
AUDIT_SPEC.loader.exec_module(AUDIT_MODULE)


def zip_bytes(entries: dict[str, bytes]) -> bytes:
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for name, data in entries.items():
            archive.writestr(name, data)
    return output.getvalue()


class GeneratedPayloadAuditTests(unittest.TestCase):
    def run_audit(self, include: Path) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [
                sys.executable,
                str(AUDIT),
                "--include-only",
                "--include",
                str(include),
            ],
            check=False,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )

    def scan_text(self, policy_path: str, text: str) -> list[str]:
        failures: list[str] = []
        AUDIT_MODULE.scan_content(
            policy_path,
            policy_path,
            text.encode("utf-8"),
            failures,
            force_text=True,
        )
        return failures

    def scan_path(self, policy_path: str) -> list[str]:
        failures: list[str] = []
        AUDIT_MODULE.check_path(policy_path, policy_path, failures)
        return failures

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

    def test_verified_target_game_source_ids_are_allowed(self) -> None:
        source_ids = (
            "robotopia.characters",
            "robotopia.items",
            "robotopia.ugc-props",
            "robotopia.vehicles",
        )
        with tempfile.TemporaryDirectory() as temporary_directory:
            source = Path(temporary_directory) / "SourceIds.cs"
            source.write_text(
                "\n".join(
                    f'const string SourceId = "{source_id}";'
                    for source_id in source_ids
                ),
                encoding="utf-8",
            )

            result = self.run_audit(source)

        self.assertEqual(0, result.returncode, result.stderr)

    def test_target_game_source_id_allowlist_rejects_unreviewed_id(self) -> None:
        source_id = "robo" + "topia.unreviewed"
        with tempfile.TemporaryDirectory() as temporary_directory:
            source = Path(temporary_directory) / "SourceIds.cs"
            source.write_text(
                f'const string SourceId = "{source_id}";', encoding="utf-8"
            )

            result = self.run_audit(source)

        self.assertEqual(1, result.returncode)
        self.assertIn("lowercase/unallowlisted Robotopia token", result.stderr)

    def test_target_game_source_id_allowlist_rejects_approved_id_suffix(self) -> None:
        source_id = "robo" + "topia.items.extra"
        with tempfile.TemporaryDirectory() as temporary_directory:
            source = Path(temporary_directory) / "SourceIds.cs"
            source.write_text(
                f'const string SourceId = "{source_id}";', encoding="utf-8"
            )

            result = self.run_audit(source)

        self.assertEqual(1, result.returncode)
        self.assertIn("lowercase/unallowlisted Robotopia token", result.stderr)

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

    def test_release_handoff_game_field_is_allowed_in_exact_integration_file(
        self,
    ) -> None:
        path = "apps/topiaforge_cli/lib/src/release_handoff_qa.dart"
        game_name = "robo" + "topia"
        failures = self.scan_text(
            path,
            "const evidence = {'" + game_name + "': result};",
        )

        self.assertEqual([], failures)

    def test_release_game_install_verifier_paths_are_exactly_allowed(self) -> None:
        game_name = "robo" + "topia"
        verifier_paths = (
            f"tools/release/verify-{game_name}-install.ps1",
            f"tools/release/test-verify-{game_name}-install.ps1",
        )

        for path in verifier_paths:
            with self.subTest(path=path):
                self.assertEqual([], self.scan_path(path))

    def test_exact_release_game_install_verifier_reference_is_allowed(self) -> None:
        game_name = "robo" + "topia"
        command = (
            "pwsh -NoProfile -File "
            f"tools/release/test-verify-{game_name}-install.ps1"
        )
        failures = self.scan_text(".github/workflows/ci.yml", command)

        self.assertEqual([], failures)

    def test_release_game_install_verifier_reference_rejects_path_prefix(
        self,
    ) -> None:
        game_name = "robo" + "topia"
        command = (
            "pwsh -NoProfile -File "
            f"payload/tools/release/test-verify-{game_name}-install.ps1"
        )
        failures = self.scan_text(".github/workflows/ci.yml", command)

        self.assertTrue(
            any(
                "lowercase/unallowlisted Robotopia token" in failure
                for failure in failures
            ),
            failures,
        )

    def test_release_game_install_verifier_path_allowlist_rejects_prefix(self) -> None:
        game_name = "robo" + "topia"
        path = f"payload/tools/release/verify-{game_name}-install.ps1"
        failures = self.scan_path(path)

        self.assertTrue(
            any(
                "retired " + "Robotopia" + " ecosystem name in path" in failure
                for failure in failures
            ),
            failures,
        )

    def test_retired_manager_still_fails_inside_allowed_integration_file(
        self,
    ) -> None:
        path = "apps/topiaforge_cli/lib/src/release_handoff_qa.dart"
        game_name = "robo" + "topia"
        retired_manager = "Robotopia" + "ModManager"
        failures = self.scan_text(
            path,
            "const evidence = {'" + game_name + "': result};\n" + retired_manager,
        )

        self.assertTrue(
            any("retired manager name" in failure for failure in failures),
            failures,
        )

    def test_retired_sdk_still_fails_inside_allowed_release_script(self) -> None:
        path = "tools/release-admin.ps1"
        game_name = "robo" + "topia"
        retired_interface = "I" + "Robotopia" + "Mod"
        failures = self.scan_text(
            path,
            "$" + game_name + " = @{}\n" + retired_interface,
        )

        self.assertTrue(
            any("retired SDK interface" in failure for failure in failures),
            failures,
        )

    def test_game_fact_token_allowlist_rejects_suffix_extension(self) -> None:
        unreviewed_token = "robo" + "topia-owner-extra"
        failures = self.scan_text("release/release-readiness.json", unreviewed_token)

        self.assertTrue(
            any(
                "lowercase/unallowlisted Robotopia token" in failure
                for failure in failures
            ),
            failures,
        )

    def test_target_game_tool_paths_are_allowed_in_extracted_package(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            package = Path(temporary_directory) / "TopiaForge-linux-x64"
            tools = package / "tools"
            tools.mkdir(parents=True)
            (tools / "restore-robotopia-managed-refs.ps1").write_bytes(b"")
            (tools / "test-restore-robotopia-managed-refs.ps1").write_bytes(b"")

            result = self.run_audit(package)

        self.assertEqual(0, result.returncode, result.stderr)

    def test_target_game_tool_paths_are_allowed_in_macos_app_archive(self) -> None:
        package_root = "TopiaForge.app/Contents/Resources/TopiaForge/tools"
        with tempfile.TemporaryDirectory() as temporary_directory:
            archive = Path(temporary_directory) / "TopiaForge-macos-universal.zip"
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

    def test_target_game_tool_allowlist_rejects_unrelated_prefix(self) -> None:
        member = "docs/tools/restore-robotopia-managed-refs.ps1"
        with tempfile.TemporaryDirectory() as temporary_directory:
            archive = Path(temporary_directory) / "TopiaForge-test.zip"
            archive.write_bytes(zip_bytes({member: b""}))

            result = self.run_audit(archive)

        self.assertEqual(1, result.returncode)
        self.assertIn(
            "retired " + "Robotopia" + " ecosystem name in path",
            result.stderr,
        )

    def test_target_game_tool_allowlist_rejects_nested_package_prefix(self) -> None:
        member = (
            "payload/TopiaForge-linux-x64/tools/"
            "restore-robotopia-managed-refs.ps1"
        )
        with tempfile.TemporaryDirectory() as temporary_directory:
            archive = Path(temporary_directory) / "TopiaForge-test.zip"
            archive.write_bytes(zip_bytes({member: b""}))

            result = self.run_audit(archive)

        self.assertEqual(1, result.returncode)
        self.assertIn(
            "retired " + "Robotopia" + " ecosystem name in path",
            result.stderr,
        )

    def test_exact_game_build_allowlist_is_not_a_package_suffix(self) -> None:
        member = "TopiaForge-linux-x64/.github/robotopia-game-build.json"
        with tempfile.TemporaryDirectory() as temporary_directory:
            archive = Path(temporary_directory) / "TopiaForge-test.zip"
            archive.write_bytes(zip_bytes({member: b"{}"}))

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
    def test_retired_sdk_interface_still_fails(self) -> None:
        retired_interface = "I" + "Robotopia" + "Mod"
        with tempfile.TemporaryDirectory() as temporary_directory:
            source = Path(temporary_directory) / "Legacy.cs"
            source.write_text(
                f"public sealed class Legacy : {retired_interface} {{}}",
                encoding="utf-8",
            )

            result = self.run_audit(source)

        self.assertEqual(1, result.returncode)
        self.assertIn("retired SDK interface", result.stderr)

    def test_retired_sdk_interface_in_binary_strings_still_fails(self) -> None:
        retired_interface = "I" + "Robotopia" + "Mod"
        with tempfile.TemporaryDirectory() as temporary_directory:
            assembly = Path(temporary_directory) / "Legacy.dll"
            assembly.write_bytes(b"\0managed\0" + retired_interface.encode() + b"\0")

            result = self.run_audit(assembly)

        self.assertEqual(1, result.returncode)
        self.assertIn("retired SDK interface", result.stderr)
        self.assertIn(":strings:", result.stderr)

    def test_missing_include_is_an_actionable_tool_error(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            missing = Path(temporary_directory) / "missing.zip"
            result = self.run_audit(missing)

        self.assertEqual(2, result.returncode)
        self.assertIn("requested --include path does not exist", result.stderr)


if __name__ == "__main__":
    unittest.main()
