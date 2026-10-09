#!/usr/bin/env python3
"""Tests for the redistributed-asset licence coverage audit."""

from __future__ import annotations

import importlib.util
import unittest
from pathlib import Path

AUDIT = Path(__file__).with_name("check_asset_licence_coverage.py")
AUDIT_SPEC = importlib.util.spec_from_file_location("asset_licence_audit", AUDIT)
assert AUDIT_SPEC is not None and AUDIT_SPEC.loader is not None
AUDIT_MODULE = importlib.util.module_from_spec(AUDIT_SPEC)
AUDIT_SPEC.loader.exec_module(AUDIT_MODULE)

# A notices file shaped like the real one: it records one bundled set by its
# directory and another by its exact path.
NOTICES = """
# Third Party Notices

- Bundled at: `packages/launcher_ui/fonts`

TopiaForge redistributes Unity's TextMesh Pro essential resources at
`tools/unity-ui-bundle/Assets/TextMesh Pro`, and the release archives carry
that directory.

- Bundled at: `tools/unity-ui-bundle/Assets/TextMesh Pro/Fonts/LiberationSans.ttf`
- Web-derived raster files: `sheriff.webp`

The pixel robot replaced a previously bundled `robot.webp` taken from the web
bundle. Prose in this file freely names directories such as mods and website
without marking them as paths.

## First-party binary and generated assets

- `packages/launcher_ui/assets/brand/topiaforge-icon.png` — the source mark.
- `apps/topiaforge_launcher_flutter/macos/Runner/Assets.xcassets/AppIcon.appiconset` —
  the same icon at the eight sizes macOS requires.
"""


class CoverageRuleTests(unittest.TestCase):
    def covered(self, path: str) -> str | None:
        return AUDIT_MODULE.coverage_for(path, NOTICES)

    def test_unknown_asset_is_not_covered(self):
        self.assertIsNone(self.covered("mods/Something/loot.png"))

    def test_emojione_under_a_recorded_ancestor_is_still_caught(self):
        # The regression this audit exists for. `TextMesh Pro` is recorded in
        # the notices; EmojiOne shipped one level below it with no
        # redistribution grant. An ancestor match would have hidden it.
        self.assertIsNone(
            self.covered(
                "tools/unity-ui-bundle/Assets/TextMesh Pro/Sprites/EmojiOne.png"
            )
        )

    def test_recorded_bundled_directory_covers_its_files(self):
        # The fonts are recorded by directory because their on-disk names
        # differ from the upstream filenames.
        self.assertIsNotNone(
            self.covered("packages/launcher_ui/fonts/Quicksand-VariableFont_wght.ttf")
        )

    def test_directory_match_is_the_immediate_parent_only(self):
        # Same recorded directory, one level deeper: not covered.
        self.assertIsNone(self.covered("packages/launcher_ui/fonts/nested/extra.ttf"))

    def test_exact_path_is_covered(self):
        self.assertIsNotNone(
            self.covered(
                "tools/unity-ui-bundle/Assets/TextMesh Pro/Fonts/LiberationSans.ttf"
            )
        )

    def test_bare_filename_is_not_enough(self):
        # `sheriff.webp` appears in the notices as a filename only. A filename
        # is not unique, so honouring it would let any new file inherit an
        # unrelated entry by reusing the name. The real notices record these by
        # full path for exactly this reason.
        self.assertIsNone(self.covered("mods/Elsewhere/sheriff.webp"))

    def test_prose_word_matching_a_directory_does_not_cover(self):
        # The word "mods" occurs throughout the notices prose. A substring
        # search treated that as a recorded directory and silently covered
        # anything dropped in `mods/`.
        self.assertIsNone(self.covered("mods/sneaky.png"))

    def test_a_filename_the_notices_call_removed_does_not_cover(self):
        # The notices mention `robot.webp` while explaining that it was
        # *retired*. A substring search read that as coverage, so a brand-new
        # unlicensed file could pass by taking the name of a deleted one.
        self.assertIsNone(self.covered("mods/TopiaForge.Sandbox/robot.webp"))

    def test_code_span_paths_are_read_from_the_notices(self):
        spans = AUDIT_MODULE.recorded_paths(NOTICES)
        self.assertIn("packages/launcher_ui/fonts", spans)
        self.assertIn("robot.webp", spans)
        # Prose words are not code spans, so they never become coverage.
        self.assertNotIn("mods", spans)

    def test_blanket_licensed_tree_is_covered(self):
        self.assertIsNotNone(
            self.covered("third_party/BepInEx/win_x64_5.4.23.5/BepInEx/core/BepInEx.dll")
        )

    def test_first_party_asset_recorded_in_notices_is_covered(self):
        self.assertIsNotNone(
            self.covered("packages/launcher_ui/assets/brand/topiaforge-icon.png")
        )

    def test_recorded_icon_set_directory_covers_its_files(self):
        self.assertIsNotNone(
            self.covered(
                "apps/topiaforge_launcher_flutter/macos/Runner/Assets.xcassets/"
                "AppIcon.appiconset/app_icon_512.png"
            )
        )

    def test_an_unrecorded_windows_icon_is_not_covered(self):
        # Selecting `.ico` files only helps if an unrecorded one then fails.
        self.assertIsNone(
            self.covered(
                "apps/topiaforge_launcher_flutter/windows/runner/resources/app_icon.ico"
            )
        )


class AssetSelectionTests(unittest.TestCase):
    def test_only_non_source_suffixes_are_audited(self):
        selected = AUDIT_MODULE.redistributed_assets(
            [
                "a/readme.md",
                "a/thing.cs",
                "a/font.ttf",
                "a/pic.PNG",
                "a/layout.unity",
                "a/bundle.bundle",
            ]
        )
        self.assertEqual(selected, ["a/bundle.bundle", "a/font.ttf", "a/pic.PNG"])

    def test_windows_icon_files_are_audited(self):
        # The regression: `.ico` was absent, so the launcher icon embedded into
        # the Windows executable was never enumerated at all.
        selected = AUDIT_MODULE.redistributed_assets(
            ["runner/resources/app_icon.ico", "a/LEGACY.ICO", "a/notes.txt"]
        )
        self.assertEqual(selected, ["a/LEGACY.ICO", "runner/resources/app_icon.ico"])


def is_binary(path: Path) -> bool:
    """Git's own heuristic: a NUL byte in the first 8000 bytes."""
    with path.open("rb") as handle:
        return b"\0" in handle.read(8000)


class RepositoryTests(unittest.TestCase):
    def test_the_repository_currently_passes(self):
        # An audit nobody can satisfy gets disabled, so the committed tree must
        # be clean at the moment it lands.
        self.assertEqual(AUDIT_MODULE.audit(), [])

    def test_every_binary_file_has_an_audited_suffix(self):
        # ASSET_SUFFIXES is a closed list, so a binary type it does not name is
        # skipped silently instead of failing. That is how `app_icon.ico`
        # escaped. Reading the bytes catches the next such type when it lands.
        unaudited = []
        for path in AUDIT_MODULE.repository_files():
            file = AUDIT_MODULE.ROOT / path
            if file.is_symlink() or not file.is_file() or not is_binary(file):
                continue
            if Path(path).suffix.lower() not in AUDIT_MODULE.ASSET_SUFFIXES:
                unaudited.append(path)
        self.assertEqual(
            unaudited,
            [],
            "binary files whose suffix ASSET_SUFFIXES does not name; add the "
            "suffix, then record each file in THIRD_PARTY_NOTICES.md",
        )

    def test_the_windows_launcher_icon_is_audited_and_recorded(self):
        icon = "apps/topiaforge_launcher_flutter/windows/runner/resources/app_icon.ico"
        self.assertEqual(AUDIT_MODULE.redistributed_assets([icon]), [icon])
        notices = (AUDIT_MODULE.ROOT / AUDIT_MODULE.NOTICES).read_text(
            encoding="utf-8"
        )
        self.assertIsNotNone(AUDIT_MODULE.coverage_for(icon, notices))


if __name__ == "__main__":
    unittest.main()
