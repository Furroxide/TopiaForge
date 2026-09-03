#!/usr/bin/env python3
"""Tests for the README diagram audit."""

from __future__ import annotations

import importlib.util
import tempfile
import unittest
from pathlib import Path

AUDIT = Path(__file__).with_name("check_readme_diagrams.py")
AUDIT_SPEC = importlib.util.spec_from_file_location("readme_diagram_audit", AUDIT)
assert AUDIT_SPEC is not None and AUDIT_SPEC.loader is not None
AUDIT_MODULE = importlib.util.module_from_spec(AUDIT_SPEC)
AUDIT_SPEC.loader.exec_module(AUDIT_MODULE)

# Stands in for build_readme_svg.py so the tests need neither Pillow nor the
# brand PNGs. It is loaded by path exactly as the real generator is.
GENERATOR = '''
THEMES = {"light": {}, "dark": {}}


def _svg(title, desc, labels):
    body = "".join('<text x="0" y="0">%s</text>' % label for label in labels)
    return (
        '<?xml version="1.0" encoding="UTF-8"?>\\n'
        '<svg xmlns="http://www.w3.org/2000/svg" role="img" aria-labelledby="t d">\\n'
        '<title id="t">%s</title>\\n<desc id="d">%s</desc>\\n%s\\n</svg>\\n'
    ) % (title, desc, body)


def build_hero(theme):
    return _svg("TopiaForge", "A terminal shows the quickstart.",
                ["ROBOTOPIA MODDING TOOLKIT", "topiaforge doctor"])


def build_architecture(theme):
    return _svg("TopiaForge architecture", "How the pieces fit.",
                ["TopiaForge.ModManager", "ModManager.Core", "launcher_data"])
'''

HERO_ALT = "TopiaForge — build mods without touching Unity. A terminal shows the quickstart."
ARCH_ALT = (
    "Architecture. TopiaForge.ModManager owns ModManager.Core, and the desktop "
    "side sits on launcher_data."
)


def _picture(name: str, alt: str) -> str:
    return (
        "<p align=\"center\">\n"
        "  <picture>\n"
        f'    <source media="(prefers-color-scheme: dark)" srcset="./assets/readme/{name}-dark.svg">\n'
        f'    <img src="./assets/readme/{name}-light.svg"\n'
        f'         alt="{alt}">\n'
        "  </picture>\n"
        "</p>\n"
    )


README = _picture("hero", HERO_ALT) + "\n## How it fits together\n\n" + _picture(
    "architecture", ARCH_ALT
)

# Held open for the run and cleaned up at exit; mkdtemp alone would leave a
# directory behind per test, which the sibling audits' tests do not do.
_TEMP_DIRS: list[tempfile.TemporaryDirectory] = []


def tree(readme: str = README, generator: str = GENERATOR, **svgs) -> Path:
    """Builds a throwaway repository root whose SVGs match the stub generator.

    Seeding the committed SVGs from the generator is the state the audit calls
    correct, so each test states its own drift as an override rather than
    restating four files.
    """
    holder = tempfile.TemporaryDirectory(prefix="readme-diagram-test-")
    _TEMP_DIRS.append(holder)
    root = Path(holder.name)
    (root / "README.md").write_text(readme, encoding="utf-8")

    source = root / "assets" / "readme" / "source"
    source.mkdir(parents=True)
    (source / "build_readme_svg.py").write_text(generator, encoding="utf-8")

    AUDIT_MODULE.ROOT = root
    for name, body in AUDIT_MODULE.regenerated().items():
        (root / "assets" / "readme" / name).write_bytes(body)
    for name, body in svgs.items():
        path = root / "assets" / "readme" / name.replace("_", "-") + ".svg"
        if body is None:
            path.unlink()
        else:
            path.write_text(body, encoding="utf-8")
    return root


def edit(root: Path, name: str, old: str, new: str) -> None:
    """Hand-edits a committed SVG, which is what the audit exists to catch."""
    path = root / "assets" / "readme" / name
    path.write_text(path.read_text(encoding="utf-8").replace(old, new), encoding="utf-8")


class ReaderTests(unittest.TestCase):
    def test_both_figures_are_found_with_their_variants_and_alt(self):
        tree()
        figures = AUDIT_MODULE.readme_figures()
        self.assertEqual(len(figures), 2)
        self.assertEqual(figures[0]["light"], "assets/readme/hero-light.svg")
        self.assertEqual(figures[0]["dark"], "assets/readme/hero-dark.svg")
        self.assertEqual(figures[1]["alt"], ARCH_ALT)

    def test_only_identifier_shaped_labels_count_as_components(self):
        # The prose on the hero ("ROBOTOPIA MODDING TOOLKIT", a command line)
        # must not be mistaken for a component the alt text has to name.
        tree()
        self.assertEqual(
            AUDIT_MODULE.drawn_components("assets/readme/architecture-light.svg"),
            ["ModManager.Core", "TopiaForge.ModManager", "launcher_data"],
        )
        self.assertEqual(AUDIT_MODULE.drawn_components("assets/readme/hero-light.svg"), [])

    def test_components_come_from_drawn_text_not_from_the_description(self):
        # A component named only in <desc> is not drawn, so requiring it in the
        # alt text would be checking prose against prose.
        root = tree()
        edit(root, "architecture-light.svg", "How the pieces fit.", "Mentions Only.InDesc here.")
        edit(root, "architecture-dark.svg", "How the pieces fit.", "Mentions Only.InDesc here.")
        self.assertNotIn(
            "Only.InDesc", AUDIT_MODULE.drawn_components("assets/readme/architecture-light.svg")
        )

    def test_a_generator_that_cannot_be_imported_is_a_tool_error(self):
        # Seeded from a working generator first, then broken: a missing Pillow
        # must read as "could not run", never as "the diagrams are fine".
        root = tree()
        (root / "assets/readme/source/build_readme_svg.py").write_text(
            "import pillow_that_is_not_installed\n", encoding="utf-8"
        )
        with self.assertRaises(AUDIT_MODULE.AuditToolError) as caught:
            AUDIT_MODULE.regenerated()
        self.assertIn("Pillow", str(caught.exception))

    def test_a_missing_generator_is_a_tool_error(self):
        root = tree()
        (root / "assets/readme/source/build_readme_svg.py").unlink()
        with self.assertRaises(AUDIT_MODULE.AuditToolError):
            AUDIT_MODULE.regenerated()

    def test_a_readme_with_no_generated_figures_is_a_tool_error(self):
        tree(readme="# TopiaForge\n\nNo pictures at all.\n")
        with self.assertRaises(AUDIT_MODULE.AuditToolError):
            AUDIT_MODULE.readme_figures()

    def test_a_malformed_svg_is_a_tool_error(self):
        root = tree()
        (root / "assets/readme/hero-light.svg").write_text("<svg><title>", encoding="utf-8")
        with self.assertRaises(AUDIT_MODULE.AuditToolError):
            AUDIT_MODULE.described("assets/readme/hero-light.svg")


class AuditTests(unittest.TestCase):
    def test_a_consistent_tree_passes(self):
        tree()
        self.assertEqual(AUDIT_MODULE.audit(), [])

    def test_a_hand_edited_svg_is_caught(self):
        # The gap this closes: the generator was never run in CI, so an SVG
        # edited directly stayed committed and the generator quietly stopped
        # being the source of truth.
        root = tree()
        edit(root, "hero-light.svg", "topiaforge doctor", "topiaforge doctor --strict")
        failures = AUDIT_MODULE.audit()
        self.assertEqual(len(failures), 1)
        self.assertIn("does not match what", failures[0])

    def test_alt_text_that_does_not_name_a_drawn_component_is_caught(self):
        # The realistic case: a box is renamed, both themes are regenerated
        # correctly, and README keeps describing the old name.
        renamed = GENERATOR.replace("ModManager.Core", "ModManager.Kernel")
        tree(generator=renamed)
        failures = AUDIT_MODULE.audit()
        self.assertEqual(len(failures), 1)
        self.assertIn("does not name ModManager.Kernel", failures[0])

    def test_a_variant_edited_by_hand_is_caught_as_divergence(self):
        root = tree()
        edit(root, "architecture-dark.svg", "How the pieces fit.", "How the pieces fit, roughly.")
        failures = AUDIT_MODULE.audit()
        # Reported both as divergence and as no longer matching the generator.
        self.assertEqual(len(failures), 2)
        self.assertIn("do not share one <title> and <desc>", failures[0])

    def test_a_missing_dark_variant_source_is_caught(self):
        tree(readme=README.replace(
            '    <source media="(prefers-color-scheme: dark)" srcset="./assets/readme/hero-dark.svg">\n',
            "",
        ))
        failures = AUDIT_MODULE.audit()
        self.assertTrue(any("no dark <source srcset>" in f for f in failures))

    def test_empty_alt_text_is_caught(self):
        tree(readme=README.replace(f'alt="{HERO_ALT}"', 'alt=""'))
        failures = AUDIT_MODULE.audit()
        self.assertTrue(any("empty alt text" in f for f in failures))

    def test_a_referenced_svg_that_does_not_exist_is_caught(self):
        root = tree()
        (root / "assets/readme/hero-dark.svg").unlink()
        failures = AUDIT_MODULE.audit()
        self.assertTrue(any("does not exist" in f for f in failures))

    def test_a_generated_diagram_readme_never_shows_is_caught(self):
        # An unreferenced diagram is one nobody would notice going wrong.
        tree(readme=_picture("architecture", ARCH_ALT))
        failures = AUDIT_MODULE.audit()
        self.assertTrue(any("never references it" in f for f in failures))

    def test_every_problem_is_reported_not_just_the_first(self):
        root = tree(readme=README.replace(f'alt="{HERO_ALT}"', 'alt=""'))
        edit(root, "architecture-light.svg", "launcher_data", "launcher_store")
        failures = AUDIT_MODULE.audit()
        # Empty alt, the renamed label the alt no longer names, and the edited file.
        self.assertEqual(len(failures), 3)


class RepositoryTests(unittest.TestCase):
    def test_the_repository_currently_passes(self):
        # An audit nobody can satisfy gets disabled, so the committed tree must
        # be clean at the moment it lands.
        AUDIT_MODULE.ROOT = Path(__file__).resolve().parents[2]
        try:
            failures = AUDIT_MODULE.audit()
        except AUDIT_MODULE.AuditToolError as error:
            self.skipTest(f"generator prerequisites unavailable: {error}")
        self.assertEqual(failures, [])


if __name__ == "__main__":
    unittest.main()
