#!/usr/bin/env python3
"""Keep the counts README.md states as fact true against the repository.

README.md publishes five figures -- services, CLI commands, mod templates,
first-party mods, and a docs badge -- as plain sentences. Nothing verified any
of them. A branch that retires a subsystem moves the count without touching the
sentence that states it, so the textual merge succeeds and the page silently
starts lying. PR #66 (build 2409) retired UGC live sync and Creator Tools and
moved four of the five at once while README.md merged clean.

Each figure is re-derived from its source of truth here, and a reader raises
rather than returning an empty list, so a rename fails this gate instead of
quietly producing a smaller number that then "matches" a stale README.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
README = "README.md"


class AuditToolError(RuntimeError):
    """A local prerequisite failed, so the audit could not be completed."""


def _read(relative: str) -> str:
    path = ROOT / relative
    if not path.is_file():
        raise AuditToolError(
            f"{relative} is missing; the counts cannot be derived from it."
        )
    return path.read_text(encoding="utf-8")


def _require(items, relative: str, what: str):
    if not items:
        raise AuditToolError(f"Found no {what} in {relative}. Did the format change?")
    return items


def services() -> list[str]:
    """Get-only properties on IModContext, which is the SDK surface README counts.

    Scoped to that interface's own body. The file declares IModLogger as well,
    which happens to be all methods today -- so scanning the whole file gives
    the right answer by luck, and would start counting the first get-only
    property anyone adds to a sibling type as a service.
    """
    source = _read("src/TopiaForge.Mods.Abstractions/IModContext.cs")
    start = re.search(r"public interface IModContext\s*\n\s*\{", source)
    if not start:
        raise AuditToolError("Could not find `public interface IModContext` in IModContext.cs.")
    body = source[start.end():]
    # Interfaces sit one level inside the namespace block, so the first closing
    # brace at that indentation ends this one.
    end = re.search(r"\n    \}", body)
    if not end:
        raise AuditToolError("`interface IModContext` in IModContext.cs is not closed as expected.")
    found = re.findall(
        r"^\s+([A-Za-z][A-Za-z0-9_]*)\s+([A-Za-z][A-Za-z0-9_]*)\s*\{ get; \}",
        body[: end.start()],
        re.MULTILINE,
    )
    return [name for _, name in _require(found, "IModContext.cs", "services")]


def cli_commands() -> list[str]:
    """Top-level CLI verbs, cross-checked between the help list and the dispatcher.

    `help` is counted: it is a verb users type, answered ahead of the switch
    rather than inside it. Both sources are read because either can go stale on
    its own -- a verb added to the dispatcher but not to the help list is
    invisible to `topiaforge help` and to did-you-mean, and the reverse
    advertises a verb that does not run.
    """
    listed = _read("apps/topiaforge_cli/bin/topiaforge_help.dart")
    block = re.search(r"const _commands = \[(.*?)\];", listed, re.DOTALL)
    if not block:
        raise AuditToolError(
            "Could not find `const _commands` in topiaforge_help.dart."
        )
    published = _require(
        re.findall(r"'([a-z][a-z-]*)'", block.group(1)),
        "topiaforge_help.dart",
        "CLI commands",
    )

    dispatch = _read("apps/topiaforge_cli/bin/topiaforge.dart")
    switch = re.search(
        r"return switch \(command\) \{(.*?)_ => _unknown\(command\),",
        dispatch,
        re.DOTALL,
    )
    if not switch:
        raise AuditToolError(
            "Could not find the `switch (command)` dispatcher in topiaforge.dart."
        )
    dispatched = set(
        _require(
            re.findall(r"^\s+'([a-z][a-z-]*)' =>", switch.group(1), re.MULTILINE),
            "topiaforge.dart",
            "dispatched CLI commands",
        )
    )
    dispatched.add("help")  # Answered before the switch, so it never appears in it.

    if set(published) != dispatched:
        detail = []
        only_listed = sorted(set(published) - dispatched)
        only_dispatched = sorted(dispatched - set(published))
        if only_listed:
            detail.append(f"listed but not dispatched: {', '.join(only_listed)}")
        if only_dispatched:
            detail.append(f"dispatched but not listed: {', '.join(only_dispatched)}")
        raise AuditToolError(
            "topiaforge_help.dart and topiaforge.dart disagree on the command set "
            f"({'; '.join(detail)}). Fix that before trusting the README count."
        )
    return published


def mod_templates() -> list[str]:
    directory = ROOT / "templates" / "mod"
    if not directory.is_dir():
        raise AuditToolError("templates/mod/ is missing.")
    found = sorted(p.name for p in directory.iterdir() if p.is_dir())
    return _require(found, "templates/mod/", "mod templates")


def first_party_mods() -> list[str]:
    """Catalogued mods, which is the release payload plus the DevTool gallery."""
    source = _read("docs/FirstPartyMods.md")
    found = re.findall(
        r"^\| `io\.github\.furroxide\.topiaforge\.([a-z-]+)` \|", source, re.MULTILINE
    )
    return _require(found, "docs/FirstPartyMods.md", "first-party mods")


def guides() -> list[str]:
    """Top-level guides only. docs/internal/ is not published, so the badge omits it."""
    directory = ROOT / "docs"
    if not directory.is_dir():
        raise AuditToolError("docs/ is missing.")
    found = sorted(p.name for p in directory.glob("*.md"))
    return _require(found, "docs/", "guides")


# Every count README states, paired with the pattern that finds it. Services
# appear twice -- in the feature table and in the prose pointing at
# docs/CoreServices.md -- so fixing one and leaving the other still fails.
STATED_COUNTS = (
    (r"(\d+) owner-scoped services on `IModContext`", "services", "IModContext.cs"),
    (r"for all (\d+) services", "services", "IModContext.cs"),
    (r"\*\*`topiaforge` CLI\*\* \| (\d+) commands", "CLI commands", "the CLI dispatcher"),
    (r"\*\*(\d+) mod templates\*\*", "mod templates", "templates/mod/"),
    (r"\*\*(\d+) first-party mods\*\*", "first-party mods", "docs/FirstPartyMods.md"),
    (r"docs-(\d+)%20guides", "guides", "docs/*.md"),
)


def audit() -> list[str]:
    readme = _read(README)
    derived = {
        "services": services(),
        "CLI commands": cli_commands(),
        "mod templates": mod_templates(),
        "first-party mods": first_party_mods(),
        "guides": guides(),
    }

    failures: list[str] = []
    for pattern, what, source in STATED_COUNTS:
        actual = len(derived[what])
        stated = re.findall(pattern, readme)
        if not stated:
            # A dropped or reworded claim is a failure too: silently skipping it
            # would turn this gate off for that figure without anyone noticing.
            failures.append(
                f"{README}: found no text matching /{pattern}/ to check the {what} "
                "count against. Either the wording changed or the claim was dropped; "
                "this gate needs it to stay checkable."
            )
            continue
        for number in stated:
            if int(number) != actual:
                failures.append(
                    f"{README}: states {number} {what}, but {source} has {actual}"
                )

    # The template row names every template as well as counting them, so a
    # renamed template passes the count and still leaves the list wrong.
    row = re.search(r"\*\*\d+ mod templates\*\* \| (.*?) — ", readme)
    if not row:
        failures.append(
            f"{README}: could not find the mod template row to read its names from."
        )
    else:
        named = re.findall(r"`([a-z]+)`", row.group(1))
        if sorted(named) != sorted(derived["mod templates"]):
            failures.append(
                f"{README}: names templates {', '.join(named)}, but templates/mod/ "
                f"holds {', '.join(derived['mod templates'])}"
            )

    return failures


def main() -> int:
    try:
        failures = audit()
    except AuditToolError as error:
        print(f"README count audit could not run: {error}", file=sys.stderr)
        return 2

    if failures:
        print("README count audit failed:", file=sys.stderr)
        for failure in failures:
            print(f"  - {failure}", file=sys.stderr)
        print(
            f"\nEvery count in {README} is derived from the repository by this gate. "
            "Update the sentence to match the code, rather than the other way round.",
            file=sys.stderr,
        )
        return 1

    print("README count audit passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
