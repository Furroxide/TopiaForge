#!/usr/bin/env python3
"""Tests for the trademark notice drift audit."""

from __future__ import annotations

import importlib.util
import tempfile
import unittest
from pathlib import Path

AUDIT = Path(__file__).with_name("check_trademark_notice.py")
AUDIT_SPEC = importlib.util.spec_from_file_location("trademark_audit", AUDIT)
assert AUDIT_SPEC is not None and AUDIT_SPEC.loader is not None
AUDIT_MODULE = importlib.util.module_from_spec(AUDIT_SPEC)
AUDIT_SPEC.loader.exec_module(AUDIT_MODULE)

FULL = (
    "TopiaForge is an independent, community-built modding toolkit. It is not "
    "developed, published, or endorsed by Tomato Cake."
)
SHORT = "TopiaForge is independent and not endorsed by Tomato Cake."

CANONICAL = f"""# Trademarks and affiliation

Some prose that is not quoted.

## Full statement

Shown wherever there is room for it.

> {FULL}

## Short statement

Reserved for constrained surfaces.

> {SHORT}
"""


def tree(canonical: str = CANONICAL, **surfaces: str) -> Path:
    """Builds a throwaway repository root and points the audit at it."""
    root = Path(tempfile.mkdtemp(prefix="trademark-audit-test-"))
    (root / "TRADEMARKS.md").write_text(canonical, encoding="utf-8")
    for name in AUDIT_MODULE.FULL_STATEMENT_FILES:
        body = surfaces.get(name.replace(".", "_"), f"# {name}\n\n{FULL}\n")
        if body is not None:
            (root / name).write_text(body, encoding="utf-8")
    AUDIT_MODULE.ROOT = root
    return root


class NormalizeTests(unittest.TestCase):
    def test_collapses_hard_wrapping(self):
        # The reason the audit normalises at all: the same sentence wrapped at
        # 80 and at 100 columns is the same notice, and must not read as drift.
        self.assertEqual(
            AUDIT_MODULE.normalize("one\n  two\tthree \n\nfour"),
            "one two three four",
        )

    def test_strips_leading_and_trailing_space(self):
        self.assertEqual(AUDIT_MODULE.normalize("  padded  "), "padded")


class BlockquoteTests(unittest.TestCase):
    def test_extracts_the_first_blockquote_under_a_heading(self):
        self.assertEqual(
            AUDIT_MODULE._blockquote_after("## Full statement", CANONICAL), FULL
        )

    def test_stops_at_the_end_of_the_quote(self):
        # It must not run on into the next section, or the short statement would
        # be swallowed into the full one and every comparison would pass.
        self.assertEqual(
            AUDIT_MODULE._blockquote_after("## Short statement", CANONICAL), SHORT
        )

    def test_joins_a_hard_wrapped_quote_into_one_line(self):
        text = "## Full statement\n\n> first half\n> second half\n"
        self.assertEqual(
            AUDIT_MODULE._blockquote_after("## Full statement", text),
            "first half second half",
        )

    def test_missing_heading_is_a_tool_error_not_a_failure(self):
        # A malformed canonical file means the audit could not run. Reporting
        # that as drift would send someone editing the wrong file.
        with self.assertRaises(AUDIT_MODULE.AuditToolError):
            AUDIT_MODULE._blockquote_after("## Absent", CANONICAL)

    def test_heading_without_a_quote_is_a_tool_error(self):
        with self.assertRaises(AUDIT_MODULE.AuditToolError):
            AUDIT_MODULE._blockquote_after(
                "## Full statement", "## Full statement\n\nno quote here\n"
            )


class AuditTests(unittest.TestCase):
    def test_matching_surfaces_pass(self):
        tree()
        self.assertEqual(AUDIT_MODULE.audit(), [])

    def test_a_surface_wrapped_differently_still_passes(self):
        # The realistic case: the same words re-wrapped by an editor.
        rewrapped = "# README\n\n" + FULL.replace(". ", ".\n") + "\n"
        tree(README_md=rewrapped)
        self.assertEqual(AUDIT_MODULE.audit(), [])

    def test_drifted_wording_is_caught(self):
        # The regression this audit exists for: SUPPORT.md disclaiming a subset.
        tree(SUPPORT_md="# SUPPORT\n\nTopiaForge is not affiliated with anyone.\n")
        failures = AUDIT_MODULE.audit()
        self.assertEqual(len(failures), 1)
        self.assertIn("SUPPORT.md", failures[0])

    def test_every_drifted_surface_is_reported_not_just_the_first(self):
        tree(SUPPORT_md="# SUPPORT\n\ndrifted\n", README_md="# README\n\ndrifted\n")
        self.assertEqual(len(AUDIT_MODULE.audit()), 2)

    def test_a_missing_surface_is_reported(self):
        root = tree()
        (root / "SUPPORT.md").unlink()
        failures = AUDIT_MODULE.audit()
        self.assertEqual(len(failures), 1)
        self.assertIn("the file is missing", failures[0])

    def test_a_short_statement_that_is_not_shorter_is_caught(self):
        # A "short" form that grew stops fitting the surfaces it exists for.
        longer = CANONICAL.replace(f"> {SHORT}", f"> {FULL} And then some more.")
        tree(canonical=longer)
        failures = AUDIT_MODULE.audit()
        self.assertEqual(len(failures), 1)
        self.assertIn("not shorter", failures[0])

    def test_a_missing_canonical_file_is_a_tool_error(self):
        root = tree()
        (root / "TRADEMARKS.md").unlink()
        with self.assertRaises(AUDIT_MODULE.AuditToolError):
            AUDIT_MODULE.audit()


class RepositoryTests(unittest.TestCase):
    def test_the_repository_currently_passes(self):
        # An audit nobody can satisfy gets disabled, so the committed tree must
        # be clean at the moment it lands.
        AUDIT_MODULE.ROOT = Path(__file__).resolve().parents[2]
        self.assertEqual(AUDIT_MODULE.audit(), [])


if __name__ == "__main__":
    unittest.main()
