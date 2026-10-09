#!/usr/bin/env python3
"""Require every redistributed non-source file to be accounted for.

`THIRD_PARTY_NOTICES.md` states that the inventory covers "every non-source
file a user receives rather than only the ones with licence texts". Nothing
enforced that. The release metadata inventory in
`apps/topiaforge_cli/lib/src/release_metadata_inventory.dart` lists licence
*texts* and verifies those files exist, which is the opposite direction: it
cannot notice an asset that arrives without one.

That is not hypothetical. An EmojiOne sprite sheet shipped in the release
archives with no redistribution grant, and Liberation Sans shipped with no
notice entry, because both carried an attribution text rather than a licence
file and no check ever enumerated the assets themselves. See `P0-OSS-01` in
`docs/LaunchBlockers.md`.

This audit inverts the direction and fails closed: it enumerates redistributed
binary assets and requires each one to be covered by an explicit rule. A new
asset is uncovered until someone records what it is, so the default for an
unrecognised file is failure rather than silence.
"""

from __future__ import annotations

import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
NOTICES = "THIRD_PARTY_NOTICES.md"

# Non-source files a user can receive. Text, code, and Unity YAML are excluded:
# they are readable in review, and the licence question here is about opaque
# redistributed bytes.
ASSET_SUFFIXES = {
    ".7z",
    ".bundle",
    ".dll",
    ".dylib",
    ".gif",
    ".jpeg",
    ".jpg",
    ".mp3",
    ".ogg",
    ".otf",
    ".png",
    ".so",
    ".ttf",
    ".wav",
    ".webp",
    ".woff",
    ".woff2",
    ".zip",
}

# Trees covered wholesale by one declared upstream licence, with the licence
# texts redistributed beside the bundles. Listing the tree rather than each of
# its 29 binaries keeps the exception reviewable.
BLANKET_LICENSED_TREES = {
    "third_party/BepInEx/": (
        "BepInEx 5.4.23.5 and its bundled runtime dependencies; licence texts "
        "in third_party/BepInEx/LICENSES/ and the table in " + NOTICES
    ),
}

# There is deliberately no first-party allowlist here. First-party artwork is
# recorded in NOTICES under "First-party binary and generated assets", which
# already claims to cover every non-source file a user receives, and this audit
# reads that claim rather than keeping a second copy of it. Two lists of the
# same facts drift, and only the case nobody exercises reveals it.


class AuditToolError(RuntimeError):
    """A local prerequisite failed, so the audit could not be completed."""


def repository_files() -> list[str]:
    result = subprocess.run(
        ["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard"],
        cwd=ROOT,
        check=True,
        stdout=subprocess.PIPE,
    )
    return [item.decode("utf-8") for item in result.stdout.split(b"\0") if item]


def redistributed_assets(paths: list[str]) -> list[str]:
    return sorted(p for p in paths if Path(p).suffix.lower() in ASSET_SUFFIXES)


CODE_SPAN = re.compile(r"`([^`\n]+)`")


def recorded_paths(notices: str) -> set[str]:
    """Every path the notices record, as exact Markdown code spans.

    Matching spans rather than searching the raw text is what makes this an
    audit instead of a grep. A substring search over the prose accepts far too
    much: an asset at `mods/anything.png` matched because the bare word "mods"
    occurs in a sentence, and a new file named `robot.webp` matched because the
    notices mention that filename while explaining that it was *removed*. Both
    reported "covered" for assets nobody had recorded, which is the exact
    failure this audit exists to prevent.
    """
    return {match.group(1).strip() for match in CODE_SPAN.finditer(notices)}


def coverage_for(path: str, notices: str) -> str | None:
    """Why `path` is accounted for, or None when nothing covers it."""
    for tree, reason in BLANKET_LICENSED_TREES.items():
        if path.startswith(tree):
            return reason
    recorded = recorded_paths(notices)
    # The full path, recorded exactly. Bare filenames are deliberately not
    # accepted: a filename is not unique, so honouring one would let a new file
    # inherit an unrelated entry just by reusing its name.
    if path in recorded:
        return f"recorded in {NOTICES}"
    # The notices also record a bundled set by its directory ("Bundled at:
    # `packages/launcher_ui/fonts`"), which is how the fonts are covered: their
    # on-disk names differ from the upstream filenames.
    #
    # Only the immediate parent counts, never an ancestor. That distinction is
    # the whole value of this rule: `tools/unity-ui-bundle/Assets/TextMesh Pro`
    # is recorded, and EmojiOne sat one level below it in `.../Sprites/`. An
    # ancestor match would have covered the exact file this audit exists for.
    parent = str(Path(path).parent).replace("\\", "/")
    if parent and parent != "." and parent in recorded:
        return f"bundled directory recorded in {NOTICES}"
    return None


def audit() -> list[str]:
    notices_path = ROOT / NOTICES
    if not notices_path.is_file():
        raise AuditToolError(f"{NOTICES} is missing.")
    notices = notices_path.read_text(encoding="utf-8")

    failures: list[str] = []
    for path in redistributed_assets(repository_files()):
        if coverage_for(path, notices) is None:
            failures.append(
                f"{path}: redistributed asset is not recorded in {NOTICES} "
                f"and is not inside a blanket-licensed tree"
            )
    return failures


def main() -> int:
    try:
        failures = audit()
    except AuditToolError as error:
        print(f"Asset licence coverage audit could not run: {error}", file=sys.stderr)
        return 2
    except subprocess.CalledProcessError as error:
        print(f"Asset licence coverage audit could not run: {error}", file=sys.stderr)
        return 2

    if failures:
        print("Asset licence coverage audit failed:", file=sys.stderr)
        for failure in failures:
            print(f"  - {failure}", file=sys.stderr)
        print(
            "\nEvery redistributed non-source file must be accounted for. Record "
            f"the asset in {NOTICES} as a backticked path: either its exact path "
            "or the directory it is bundled in. A bare filename is not enough, "
            "because a filename is not unique. First-party artwork belongs under "
            '"First-party binary and generated assets", which already claims to '
            "cover every non-source file a user receives.",
            file=sys.stderr,
        )
        return 1

    print("Asset licence coverage audit passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
