#!/usr/bin/env python3
"""Keep the gamemode-contract fixture index closed over the fixtures on disk.

The C# and Dart manifest readers are written by hand, twice, and C# has no JSON
Schema validator at all -- the schema files are read only by Dart. The fixtures
under tests/fixtures/gamemode-v6 are therefore the only thing holding the two
readers to one contract, and a fixture nothing executes is worse than no fixture
at all: it looks like coverage and asserts nothing.

The older tests/fixtures/manifests corpus shows how that happens. Neither runner
enumerates the directory; both read corpus.txt. A fixture added without a corpus
line is silently dead, and nothing reports it.

So index.json is generated, never hand-edited, and both runners enumerate it and
assert it is closed over the tree. This gate regenerates it and diffs, which is
what turns "the index is stale" into a failed build instead of a quiet gap.

Run with no arguments to check; pass --write to regenerate after adding a case.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
FIXTURE_ROOT = ROOT / "tests" / "fixtures" / "gamemode-v6"
INDEX = FIXTURE_ROOT / "index.json"
INDEX_VERSION = 1

# Which runners are obliged to execute every case on a channel. This is policy,
# not something derivable from the tree, so it lives here and is generated into
# the index rather than being restated in each case file -- a case must never be
# able to exempt itself from a language.
#
# `schema` is Dart-only because it validates a document against the JSON Schema,
# and C# has no JSON Schema validator: `grep -rn "topiaforge.mod.schema.json"
# --include=*.cs` returns nothing. Every other channel is both.
CHANNEL_RUNNERS = {
    "serialization": ["csharp", "dart"],
    "schema": ["dart"],
    "resolution": ["csharp", "dart"],
}

# Kinds both runners dispatch on. Each runner switches exhaustively with a
# failing default, so a kind added here without teaching both runners fails the
# harness rather than skipping.
KNOWN_KINDS = {
    "serialization": {
        "launch-intent-round-trip",
        "launch-intent-hostile",
        "manifest-accepts",
        "manifest-rejects",
        "manifest-model-rejects",
        "transport-codec",
    },
    "schema": set(),
    "resolution": {"launch-resolution"},
}


class FixtureIndexError(RuntimeError):
    """A fixture is malformed, so no index can be derived from the tree."""


def _relative(path: Path) -> str:
    return path.relative_to(FIXTURE_ROOT).as_posix()


def _load_case(path: Path) -> dict:
    relative = _relative(path)
    try:
        case = json.loads(path.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, UnicodeError) as error:
        raise FixtureIndexError(f"{relative} is not valid JSON: {error}") from error
    if not isinstance(case, dict):
        raise FixtureIndexError(f"{relative} must be a JSON object.")

    for field in ("id", "channel", "kind"):
        if not isinstance(case.get(field), str) or not case[field]:
            raise FixtureIndexError(f"{relative} is missing a string '{field}'.")

    if case["id"] != path.stem:
        raise FixtureIndexError(
            f"{relative} declares id '{case['id']}' but its file stem is "
            f"'{path.stem}'; the two are the same name so a case can be found "
            "from a failure message."
        )

    channel = relative.split("/", 1)[0]
    if "/" not in relative or channel not in CHANNEL_RUNNERS:
        raise FixtureIndexError(f"{relative} is outside a known channel directory.")
    if case["channel"] != channel:
        raise FixtureIndexError(
            f"{relative} declares channel '{case['channel']}' but sits under "
            f"'{channel}/'. The directory decides the channel."
        )

    if case["kind"] not in KNOWN_KINDS[channel]:
        known = ", ".join(sorted(KNOWN_KINDS[channel])) or "(none yet)"
        raise FixtureIndexError(
            f"{relative} declares kind '{case['kind']}', which the {channel} "
            f"channel does not define. Known kinds: {known}. Add it to "
            "KNOWN_KINDS and to both runners' dispatch in the same commit."
        )

    expected = set(CHANNEL_RUNNERS[channel])
    if not isinstance(case.get("expect"), dict):
        raise FixtureIndexError(f"{relative} requires an expectation object.")
    declared = set(case["expect"])
    if declared != expected:
        raise FixtureIndexError(
            f"{relative} declares expectations for {sorted(declared)} but the "
            f"{channel} channel obliges {sorted(expected)}. Every obliged "
            "runner states its own outcome; there is no way to omit one."
        )

    for runner, expectation in case["expect"].items():
        if not isinstance(expectation, dict) or expectation.get("outcome") not in ("accept", "reject"):
            raise FixtureIndexError(f"{relative} requires an accept/reject outcome for {runner}.")
        if expectation["outcome"] == "reject" and case["channel"] == "resolution":
            blocks = expectation.get("blocks")
            if not isinstance(blocks, list) or not blocks:
                raise FixtureIndexError(f"{relative} requires ordered full blocks for {runner}.")
            tuples = []
            for block in blocks:
                if not isinstance(block, dict) or set(block) != {"code", "subject", "subjectVersion"} or any(not isinstance(block[key], str) for key in block):
                    raise FixtureIndexError(f"{relative} requires closed string block tuples for {runner}.")
                tuples.append((block["code"], block["subject"], block["subjectVersion"]))
            if tuples != sorted(set(tuples)):
                raise FixtureIndexError(f"{relative} requires ordinal sorted unique blocks for {runner}.")
            continue
        if expectation["outcome"] == "reject":
            codes = expectation.get("errorCodes")
            if not isinstance(codes, list) or not codes or any(not isinstance(code, str) or not code for code in codes):
                raise FixtureIndexError(f"{relative} requires nonempty errorCodes for {runner}.")
            if len(codes) != len(set(codes)):
                raise FixtureIndexError(f"{relative} repeats errorCodes for {runner}.")
        elif "errorCodes" in expectation:
            raise FixtureIndexError(f"{relative} cannot accept with errorCodes for {runner}.")
    directory = "manifest" if case["kind"].startswith("manifest-") else "transport" if case["kind"] == "transport-codec" else "launch-intent"
    if channel == "resolution": directory = ""
    if not relative.startswith(f"{channel}/" + (f"{directory}/" if directory else "")):
        raise FixtureIndexError(f"{relative} is misplaced; its kind belongs under {channel}/{directory}/.")

    mutation = case.get("modelMutation")
    mutations = {"empty-contributions", "missing-content", "missing-spawn", "missing-implementation", "missing-world", "empty-requirements", "discovered-family"}
    if case["kind"] == "manifest-model-rejects":
        if mutation not in mutations:
            raise FixtureIndexError(f"{relative} requires a known modelMutation.")
    elif "modelMutation" in case:
        raise FixtureIndexError(f"{relative} cannot change a model in a reader operation.")

    if case["kind"].startswith("manifest-"):
        if case.get("schemaOutcome") not in ("accept", "reject"):
            raise FixtureIndexError(f"{relative} requires schemaOutcome for its manifest payload.")
        if case["expect"]["csharp"] != case["expect"]["dart"]:
            raise FixtureIndexError(f"{relative} has divergent same-operation expectations.")
        expectation = case["expect"]["csharp"]
        if expectation.get("outcome") == "accept" and not isinstance(expectation.get("normalized"), dict):
            raise FixtureIndexError(f"{relative} requires a structured normalized result.")
        if "divergenceReason" in case:
            raise FixtureIndexError(f"{relative} cannot exempt manifest-reader parity.")
    elif channel == "resolution" or case["kind"] == "transport-codec":
        if case["expect"]["csharp"] != case["expect"]["dart"]:
            raise FixtureIndexError(f"{relative} has divergent same-operation expectations.")
    else:
        if case.get("operations") != {"csharp": "read-intent", "dart": "write-intent"}:
            raise FixtureIndexError(f"{relative} requires explicit wire operations.")
    if case["kind"] == "transport-codec" or channel == "resolution":
        expectation = case["expect"]["csharp"]
        if expectation["outcome"] == "accept" and not isinstance(expectation.get("normalized"), dict):
            raise FixtureIndexError(f"{relative} requires a structured normalized result.")
        if "divergenceReason" in case or "operations" in case:
            raise FixtureIndexError(f"{relative} cannot exempt same-operation parity.")
    if case["kind"] == "transport-codec":
        if case.get("transport") not in {"plan", "profile", "progress", "outcome", "observation"} or (("payload" in case) == ("wireJson" in case)):
            raise FixtureIndexError(f"{relative} requires a known transport and payload.")
        if "operations" in case or "divergenceReason" in case:
            raise FixtureIndexError(f"{relative} cannot exempt transport codec parity.")
    if channel == "resolution":
        profile = case.get("profile")
        if not isinstance(profile, dict) or not isinstance(case.get("request"), dict):
            raise FixtureIndexError(f"{relative} requires profile and request objects.")
        for field in ("packages", "disabledPackages"):
            packages = profile.get(field, [])
            if not isinstance(packages, list):
                raise FixtureIndexError(f"{relative} requires a {field} array.")
            for package in packages:
                if not isinstance(package, dict) or package.get("schemaOutcome") not in ("accept", "reject") or not isinstance(package.get("validation"), dict):
                    raise FixtureIndexError(f"{relative} requires schemaOutcome and validation for every package manifest.")
                validity = package["validation"]
                if validity.get("outcome") not in ("accept", "reject"):
                    raise FixtureIndexError(f"{relative} requires manifest validation accept/reject outcomes.")
                codes = validity.get("errorCodes", [])
                if not isinstance(codes, list) or any(not isinstance(code, str) or not code for code in codes) or codes != sorted(set(codes)) or (validity["outcome"] == "reject") != bool(codes):
                    raise FixtureIndexError(f"{relative} requires exact sorted manifest validation codes.")
                if case["expect"]["csharp"]["outcome"] == "accept" and (package["schemaOutcome"] != "accept" or validity["outcome"] != "accept"):
                    raise FixtureIndexError(f"{relative} cannot accept an operational resolution with invalid input manifests.")
    return case


def build_index() -> dict:
    if not FIXTURE_ROOT.is_dir():
        raise FixtureIndexError(
            f"{_relative_to_root(FIXTURE_ROOT)} is missing; there are no "
            "fixtures to index."
        )

    cases = []
    seen_ids: dict[str, str] = {}
    for path in sorted(FIXTURE_ROOT.rglob("*")):
        if not path.is_file():
            continue
        if path.suffix != ".json":
            raise FixtureIndexError(f"{_relative(path)} is an unexpected non-JSON fixture file.")
        if path.parent == FIXTURE_ROOT and path.name in ("index.json", "fixture.schema.json"):
            continue
        case = _load_case(path)
        relative = _relative(path)
        if case["id"] in seen_ids:
            raise FixtureIndexError(
                f"{relative} reuses id '{case['id']}', already used by "
                f"{seen_ids[case['id']]}. Ids are unique across the tree."
            )
        seen_ids[case["id"]] = relative
        cases.append({key: case[key] for key in ("id", "channel", "kind")} | {"path": relative})

    if not cases:
        raise FixtureIndexError(
            "No fixtures were found. An empty index would let both runners "
            "pass while asserting nothing."
        )

    return {
        "schemaVersion": INDEX_VERSION,
        "generatedBy": ".github/scripts/check_fixture_index.py",
        "channelRunners": {
            channel: list(runners)
            for channel, runners in sorted(CHANNEL_RUNNERS.items())
            if any(case["channel"] == channel for case in cases)
        },
        "cases": cases,
    }


def _relative_to_root(path: Path) -> str:
    return path.relative_to(ROOT).as_posix()


def _serialize(index: dict) -> str:
    return json.dumps(index, indent=2, ensure_ascii=False) + "\n"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--write",
        action="store_true",
        help="Regenerate index.json instead of checking it.",
    )
    args = parser.parse_args(argv)

    try:
        index = build_index()
    except FixtureIndexError as error:
        print(f"Fixture index audit failed: {error}", file=sys.stderr)
        return 1

    rendered = _serialize(index)
    if args.write:
        # newline='' keeps LF on Windows too: CI regenerates this on Linux
        # and diffs it, so the bytes must not depend on who ran the script.
        with INDEX.open("w", encoding="utf-8", newline="") as handle:
            handle.write(rendered)
        print(
            f"Wrote {_relative_to_root(INDEX)} with {len(index['cases'])} cases."
        )
        return 0

    if not INDEX.is_file():
        print(
            f"Fixture index audit failed: {_relative_to_root(INDEX)} is "
            "missing. Run 'python3 .github/scripts/check_fixture_index.py "
            "--write' to generate it.",
            file=sys.stderr,
        )
        return 1

    current = INDEX.read_text(encoding="utf-8")
    if current != rendered:
        print(
            f"Fixture index audit failed: {_relative_to_root(INDEX)} does not "
            "match the fixtures on disk.\n"
            "Run 'python3 .github/scripts/check_fixture_index.py --write' and "
            "commit the result. Both conformance runners enumerate this file, "
            "so a case missing from it is a case nothing executes.",
            file=sys.stderr,
        )
        return 1

    print(f"Fixture index audit passed for {len(index['cases'])} cases.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
