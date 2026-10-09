#!/usr/bin/env python3
"""Keep every copy of the trademark notice identical to the canonical one.

The notice existed in two files and had already drifted: README.md disclaimed
development, publication, endorsement *and* affiliation and addressed ownership
of the marks; SUPPORT.md disclaimed a subset and said nothing about ownership.
Nobody edited one on purpose and left the other behind -- there was simply no
canonical copy, so both were originals.

TRADEMARKS.md is now that canonical copy. This audit fails when a surface states
the notice in words that do not match it, so a counsel-approved rewrite lands as
one edit rather than a hunt.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CANONICAL = "TRADEMARKS.md"

# Files that state the notice in full. A surface using the short form is not
# listed here: the short form is checked as its own string below.
FULL_STATEMENT_FILES = (
    "README.md",
    "SUPPORT.md",
    "THIRD_PARTY_NOTICES.md",
)


class AuditToolError(RuntimeError):
    """A local prerequisite failed, so the audit could not be completed."""


def _blockquote_after(heading: str, text: str) -> str:
    """The first blockquote following `heading`, unwrapped to one line."""
    start = text.find(heading)
    if start < 0:
        raise AuditToolError(f"{CANONICAL} has no '{heading}' section.")
    lines = text[start + len(heading):].splitlines()
    quoted: list[str] = []
    for line in lines:
        stripped = line.strip()
        if stripped.startswith(">"):
            quoted.append(stripped.lstrip("> ").strip())
        elif quoted:
            break
    if not quoted:
        raise AuditToolError(f"{CANONICAL} has no quoted text under '{heading}'.")
    return normalize(" ".join(quoted))


def normalize(text: str) -> str:
    """Collapse whitespace so hard-wrapping at different widths still matches."""
    return re.sub(r"\s+", " ", text).strip()


def audit() -> list[str]:
    canonical_path = ROOT / CANONICAL
    if not canonical_path.is_file():
        raise AuditToolError(f"{CANONICAL} is missing.")
    canonical = canonical_path.read_text(encoding="utf-8")

    full = _blockquote_after("## Full statement", canonical)
    short = _blockquote_after("## Short statement", canonical)

    failures: list[str] = []
    for relative in FULL_STATEMENT_FILES:
        path = ROOT / relative
        if not path.is_file():
            failures.append(f"{relative}: listed as stating the notice, but the file is missing")
            continue
        if full not in normalize(path.read_text(encoding="utf-8")):
            failures.append(
                f"{relative}: does not state the full statement from {CANONICAL}. "
                "Whitespace is collapsed before comparing, so re-wrapping is fine "
                "and the words themselves differ."
            )

    # The short form is a promise about UI surfaces. Nothing embeds it yet, so
    # this only proves the canonical file defines one that is actually shorter --
    # a "short" form that grew would silently stop fitting the surfaces it exists
    # for, and the drift this audit prevents would come back as a layout bug.
    if len(short) >= len(full):
        failures.append(
            f"{CANONICAL}: the short statement is not shorter than the full one"
        )

    return failures


def main() -> int:
    try:
        failures = audit()
    except AuditToolError as error:
        print(f"Trademark notice audit could not run: {error}", file=sys.stderr)
        return 2

    if failures:
        print("Trademark notice audit failed:", file=sys.stderr)
        for failure in failures:
            print(f"  - {failure}", file=sys.stderr)
        print(
            f"\nEvery surface must state the notice in the words {CANONICAL} defines. "
            "Edit that file and copy it out, rather than rewording one copy.",
            file=sys.stderr,
        )
        return 1

    print("Trademark notice audit passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
