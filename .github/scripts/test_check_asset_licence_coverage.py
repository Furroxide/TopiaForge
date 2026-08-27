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

    def test_bare_filename_in_prose_is_covered(self):
        self.assertIsNotNone(
            self.covered("packages/launcher_ui/assets/brand/sheriff.webp")
        )

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


class RepositoryTests(unittest.TestCase):
    def test_the_repository_currently_passes(self):
        # An audit nobody can satisfy gets disabled, so the committed tree must
        # be clean at the moment it lands.
        self.assertEqual(AUDIT_MODULE.audit(), [])


if __name__ == "__main__":
    unittest.main()
