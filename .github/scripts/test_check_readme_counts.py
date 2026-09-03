#!/usr/bin/env python3
"""Tests for the README count audit."""

from __future__ import annotations

import importlib.util
import tempfile
import unittest
from pathlib import Path

AUDIT = Path(__file__).with_name("check_readme_counts.py")
AUDIT_SPEC = importlib.util.spec_from_file_location("readme_counts_audit", AUDIT)
assert AUDIT_SPEC is not None and AUDIT_SPEC.loader is not None
AUDIT_MODULE = importlib.util.module_from_spec(AUDIT_SPEC)
AUDIT_SPEC.loader.exec_module(AUDIT_MODULE)

SERVICES = ("Identity", "Runtime", "Logger")
COMMANDS = ("new", "dev", "doctor")  # `help` is added by the audit, so 4 in total.
TEMPLATES = ("minimal", "gameplay", "service")
MODS = ("robotkit", "worlds")
# FirstPartyMods.md is a guide as well as the mod catalogue, so it is listed here
# and written with the catalogue table below rather than as an empty stub.
GUIDES = ("FirstPartyMods.md", "Modding.md", "CoreServices.md", "Troubleshooting.md")

README = """<p align="center">
  <a href="docs/"><img alt="Documentation" src="https://img.shields.io/badge/docs-4%20guides-20F6FE?style=flat-square"></a>
</p>

See [docs/CoreServices.md](docs/CoreServices.md) for all 3 services and
what each one owns.

| Piece | What it is |
| --- | --- |
| **Typed C# SDK** | 3 owner-scoped services on `IModContext`. No Unity types. |
| **`topiaforge` CLI** | 4 commands covering scaffold, restore, and release. |
| **3 mod templates** | `minimal`, `gameplay`, `service` — each scaffolded and packed in CI. |
| **2 first-party mods** | Working reference implementations. |
"""

# Held open for the run and cleaned up at exit; mkdtemp alone would leave a
# directory behind per test, which the sibling audits' tests do not do.
_TEMP_DIRS: list[tempfile.TemporaryDirectory] = []


def _context(services=SERVICES, sibling: str = "") -> str:
    """The real file's shape: two interfaces inside one namespace block.

    `sibling` adds members to IModLogger, which the audit must not count.
    """
    body = "\n".join(f"        I{name}Service {name} {{ get; }}" for name in services)
    return (
        "namespace TopiaForge.Mods\n"
        "{\n"
        "    public interface IModContext\n"
        "    {\n"
        f"{body}\n"
        "    }\n"
        "\n"
        "    public interface IModLogger\n"
        "    {\n"
        "        void Info(string message);\n"
        f"{sibling}"
        "    }\n"
        "}\n"
    )


def _help(commands=COMMANDS) -> str:
    listed = "\n".join(f"  '{name}'," for name in ("help",) + tuple(commands))
    return f"const _commands = [\n{listed}\n];\n"


def _dispatch(commands=COMMANDS) -> str:
    arms = "\n".join(f"      '{name}' => _{name}(rest)," for name in commands)
    return (
        "    final command = args.first;\n"
        "    return switch (command) {\n"
        f"{arms}\n"
        "      _ => _unknown(command),\n"
        "    };\n"
    )


def tree(readme: str = README, **overrides) -> Path:
    """Builds a throwaway repository root and points the audit at it."""
    holder = tempfile.TemporaryDirectory(prefix="readme-counts-test-")
    _TEMP_DIRS.append(holder)
    root = Path(holder.name)

    def write(relative: str, body: str) -> None:
        path = root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(body, encoding="utf-8")

    write("README.md", readme)
    write(
        "src/TopiaForge.Mods.Abstractions/IModContext.cs",
        overrides.get("context", _context()),
    )
    write(
        "apps/topiaforge_cli/bin/topiaforge_help.dart",
        overrides.get("help", _help()),
    )
    write(
        "apps/topiaforge_cli/bin/topiaforge.dart",
        overrides.get("dispatch", _dispatch()),
    )
    for name in overrides.get("templates", TEMPLATES):
        (root / "templates" / "mod" / name).mkdir(parents=True, exist_ok=True)
    for name in overrides.get("guides", GUIDES):
        if name != "FirstPartyMods.md":
            write(f"docs/{name}", f"# {name}\n")
    rows = "\n".join(
        f"| `io.github.furroxide.topiaforge.{name}` | a mod |"
        for name in overrides.get("mods", MODS)
    )
    write("docs/FirstPartyMods.md", f"| Id | What |\n| --- | --- |\n{rows}\n")
    # Not counted by the badge, and present so the test proves that.
    write("docs/internal/Scratch.md", "# internal\n")

    AUDIT_MODULE.ROOT = root
    return root


class ReaderTests(unittest.TestCase):
    def test_services_are_read_in_declaration_order(self):
        tree()
        self.assertEqual(AUDIT_MODULE.services(), list(SERVICES))

    def test_help_is_counted_as_a_command(self):
        tree()
        self.assertEqual(AUDIT_MODULE.cli_commands(), ["help", *COMMANDS])

    def test_the_docs_badge_ignores_the_internal_subdirectory(self):
        tree()
        self.assertEqual(len(AUDIT_MODULE.guides()), len(GUIDES))

    def test_a_help_list_that_omits_a_dispatched_command_is_a_tool_error(self):
        # The count would still be self-consistent, which is why both are read.
        tree(help=_help(commands=COMMANDS[:-1]))
        with self.assertRaises(AUDIT_MODULE.AuditToolError) as caught:
            AUDIT_MODULE.cli_commands()
        self.assertIn("dispatched but not listed", str(caught.exception))

    def test_a_help_list_advertising_a_missing_command_is_a_tool_error(self):
        tree(dispatch=_dispatch(commands=COMMANDS[:-1]))
        with self.assertRaises(AUDIT_MODULE.AuditToolError) as caught:
            AUDIT_MODULE.cli_commands()
        self.assertIn("listed but not dispatched", str(caught.exception))

    def test_a_property_on_a_sibling_interface_is_not_a_service(self):
        # IModContext.cs also declares IModLogger. That is all methods today, so
        # scanning the whole file gives the right answer by luck; the first
        # get-only property added to it would inflate the count and demand a
        # README edit that states something untrue.
        tree(context=_context(sibling="        ILogSink Sink { get; }\n"))
        self.assertEqual(AUDIT_MODULE.services(), list(SERVICES))
        self.assertEqual(AUDIT_MODULE.audit(), [])

    def test_a_missing_interface_declaration_is_a_tool_error(self):
        tree(context="namespace TopiaForge.Mods\n{\n    public interface IOther\n    {\n    }\n}\n")
        with self.assertRaises(AUDIT_MODULE.AuditToolError) as caught:
            AUDIT_MODULE.services()
        self.assertIn("public interface IModContext", str(caught.exception))

    def test_a_renamed_interface_member_format_is_a_tool_error(self):
        # The failure mode a hand-typed count has instead: an empty read must
        # not pass as "zero services", or the audit silently stops checking.
        tree(context=_context(services=()))
        with self.assertRaises(AUDIT_MODULE.AuditToolError) as caught:
            AUDIT_MODULE.services()
        self.assertIn("Found no services", str(caught.exception))

    def test_a_missing_source_file_is_a_tool_error(self):
        root = tree()
        (root / "src/TopiaForge.Mods.Abstractions/IModContext.cs").unlink()
        with self.assertRaises(AUDIT_MODULE.AuditToolError):
            AUDIT_MODULE.services()


class AuditTests(unittest.TestCase):
    def test_a_truthful_readme_passes(self):
        tree()
        self.assertEqual(AUDIT_MODULE.audit(), [])

    def test_a_stale_service_count_is_caught(self):
        # The regression this gate exists for: a service lands and the sentence
        # that counts them is never touched.
        tree(context=_context(services=SERVICES + ("Extensions",)))
        failures = AUDIT_MODULE.audit()
        self.assertEqual(len(failures), 2)  # Stated in two places, so reported twice.
        for failure in failures:
            self.assertIn("states 3 services, but IModContext.cs has 4", failure)

    def test_a_stale_command_count_is_caught(self):
        tree(help=_help(COMMANDS + ("pack",)), dispatch=_dispatch(COMMANDS + ("pack",)))
        failures = AUDIT_MODULE.audit()
        self.assertEqual(len(failures), 1)
        self.assertIn("states 4 CLI commands", failures[0])

    def test_a_stale_docs_badge_is_caught(self):
        root = tree()
        (root / "docs" / "Extra.md").write_text("# extra\n", encoding="utf-8")
        failures = AUDIT_MODULE.audit()
        self.assertEqual(len(failures), 1)
        self.assertIn("states 4 guides, but docs/*.md has 5", failures[0])

    def test_a_stale_first_party_mod_count_is_caught(self):
        tree(mods=MODS + ("zombies",))
        failures = AUDIT_MODULE.audit()
        self.assertEqual(len(failures), 1)
        self.assertIn("first-party mods", failures[0])

    def test_a_renamed_template_is_caught_even_when_the_count_holds(self):
        # The count still matches, so only reading the names finds this.
        tree(templates=("minimal", "gameplay", "world"))
        failures = AUDIT_MODULE.audit()
        self.assertEqual(len(failures), 1)
        self.assertIn("names templates", failures[0])

    def test_every_stale_figure_is_reported_not_just_the_first(self):
        tree(mods=MODS + ("zombies",), templates=TEMPLATES + ("ui",))
        # One count each for mods and templates, plus the template name list.
        self.assertEqual(len(AUDIT_MODULE.audit()), 3)

    def test_dropping_a_claim_does_not_silently_disable_the_check(self):
        # Deleting the sentence would otherwise be the easy way to "fix" a
        # failure, leaving the figure unverified again.
        tree(readme=README.replace("| **2 first-party mods** |", "| **Mods** |"))
        failures = AUDIT_MODULE.audit()
        self.assertEqual(len(failures), 1)
        self.assertIn("found no text matching", failures[0])


class RepositoryTests(unittest.TestCase):
    def test_the_repository_currently_passes(self):
        # An audit nobody can satisfy gets disabled, so the committed tree must
        # be clean at the moment it lands.
        AUDIT_MODULE.ROOT = Path(__file__).resolve().parents[2]
        self.assertEqual(AUDIT_MODULE.audit(), [])


if __name__ == "__main__":
    unittest.main()
