"""Generate tests/fixtures/multiplayer/server-content/cases.json from first principles, independently of the C# and
Dart code.

The record of server content, BepInEx/TopiaForge/server-content.json, is how the game's loader and the launcher tell
the versions joins installed from the player's own. The launcher writes it; both read it. These cases hold both readers
to one contract:

- valid: a document both readers accept. `document` is what it holds; `bytes` is the canonical spelling the launcher
  writes (sorted members, no white space, entries sorted, UTF-8); `reordered` is the same record spelled another way,
  which both readers read as the same record. `hides` lists installed versions, each with the SHA-256 of its install
  receipt, and whether the record withholds it from the player's own play. `addedRecords` lists the mods that start
  disabled when a reader finds them with no manager record.
- newer: a document a newer launcher wrote (schemaVersion above 1). Both readers refuse it, and the launcher never
  writes over it.
- invalid: anything else. Both readers treat it as damaged and fail closed: a damaged record never exposes a version
  it may have withheld. `text` is UTF-8 text; `hex` holds bytes that are not.
- generated: cases too large to spell out. Each runner builds them from the recipe and checks the length and the
  SHA-256 of what it built against the numbers this script computed.

Nothing is random, so a rerun reproduces every byte. Run from the repository root:
python -I tests/fixtures/multiplayer/generate_server_content.py .
"""
import hashlib
import json
import pathlib
import sys

ROOT = pathlib.Path(sys.argv[1])
OUT = ROOT / "tests" / "fixtures" / "multiplayer" / "server-content" / "cases.json"

SCHEMA_VERSION = 1
MAXIMUM_BYTES = 1024 * 1024
MAXIMUM_ENTRIES = 1024
MAXIMUM_SAFE_INTEGER = 2 ** 53 - 1
BACKSLASH = chr(92)


def hexdigest(label: str) -> str:
    return hashlib.sha256(label.encode("utf-8")).hexdigest()


def sorted_entries(entries):
    """Entries by lowercase id, then version; a list that is not all entries, as damaged cases hold, stays as it is."""
    if all(isinstance(entry, dict) and isinstance(entry.get("id"), str) and isinstance(entry.get("version"), str)
           for entry in entries):
        return sorted(entries, key=lambda entry: (entry["id"].lower(), entry["version"]))
    return entries


def canonical(document) -> str:
    """Sorted members, no white space, entries sorted by lowercase id then version, added records sorted, UTF-8
    without escapes beyond the quotation mark, the backslash and the control characters."""
    value = dict(document)
    for name in ("versions", "withheld"):
        if isinstance(value.get(name), list):
            value[name] = sorted_entries(value[name])
    added = value.get("addedRecords")
    if isinstance(added, list) and all(isinstance(item, str) for item in added):
        value["addedRecords"] = sorted(added)
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def reordered(document) -> str:
    """Members in reverse order, entries reversed, indented: readers accept any order and spacing."""
    def reverse(item):
        if isinstance(item, dict):
            return {key: reverse(item[key]) for key in sorted(item, reverse=True)}
        if isinstance(item, list):
            return [reverse(element) for element in reversed(item)]
        return item
    return json.dumps(reverse(document), indent=2, ensure_ascii=False)


def record(versions=(), withheld=(), added=()):
    return {"schemaVersion": SCHEMA_VERSION, "versions": list(versions), "withheld": list(withheld),
            "addedRecords": list(added)}


def version(mod_id, ver, label, hidden, installed_at=1760745600):
    return {"id": mod_id, "version": ver, "sha256": hexdigest("archive " + label),
            "receipt": hexdigest("receipt " + label), "hidden": hidden, "installedAt": installed_at}


def withheld(mod_id, ver, label):
    return {"id": mod_id, "version": ver, "receipt": hexdigest("receipt " + label)}


def probe(mod_id, ver, label, hidden):
    return {"id": mod_id, "version": ver, "receipt": hexdigest("receipt " + label), "hidden": hidden}


def valid(case_id, about, document, hides, spelling=None):
    return {"id": case_id, "about": about, "document": document, "bytes": canonical(document),
            "reordered": reordered(document) if spelling is None else spelling, "hides": hides,
            "addedRecords": sorted(document["addedRecords"])}


def invalid(case_id, about, text=None, document=None, raw=None):
    case = {"id": case_id, "about": about}
    if raw is not None:
        case["hex"] = raw.hex()
    else:
        case["text"] = canonical(document) if text is None else text
    return case


LIGHTS = version("io.example.lights", "2.0.0", "lights 2.0.0", True)
ARENA = version("io.example.arena", "1.4.0", "arena 1.4.0", False, installed_at=1760745601)
PAINT = version("Io.Example.Paint", "3.1.0-beta.2+build.7", "paint 3.1.0-beta.2+build.7", True,
                installed_at=MAXIMUM_SAFE_INTEGER)
TOOLS_OLD = withheld("io.example.tools", "1.0.0", "tools 1.0.0")
TOOLS_SERVER = version("io.example.tools", "1.2.0", "tools 1.2.0", True, installed_at=0)
MAPS = withheld("io.example.maps", "0.9.1", "maps 0.9.1")

EMPTY = record()
SERVERS = record([LIGHTS, ARENA], added=["io.example.arena"])
MIXED = record([PAINT])
REBUILT = record([TOOLS_SERVER], [TOOLS_OLD, MAPS])
BOTH_LISTS = record([LIGHTS], [withheld("io.example.lights", "2.0.0", "lights 2.0.0")])
ZERO = record([dict(LIGHTS, installedAt=0), ARENA], added=["io.example.arena"])

ESCAPED = ("{" + '"schemaVersion":1,"versions":[],"withheld":[{"' + BACKSLASH + 'u0069d":"io.example.' + BACKSLASH +
           'u006daps","version":"0.9.1","receipt":"' + MAPS["receipt"] + '"}],"addedRecords":[]}')
NEGATIVE_ZERO = canonical(ZERO).replace('"installedAt":0,', '"installedAt":-0,', 1)
assert NEGATIVE_ZERO.count('"installedAt":-0,') == 1

valid_cases = [
    valid("empty", "Nothing was installed for servers.", EMPTY,
          [probe("io.example.lights", "2.0.0", "lights 2.0.0", False)]),
    valid("server-versions",
          "A join installed a newer version of a mod the player had, hidden from the player's own play, and a mod new "
          "to the player, visible but recorded disabled.", SERVERS,
          [probe("io.example.lights", "2.0.0", "lights 2.0.0", True),
           probe("IO.EXAMPLE.LIGHTS", "2.0.0", "lights 2.0.0", True),
           probe("io.example.lights", "2.0.0", "lights reinstalled by the player", False),
           probe("io.example.lights", "1.0.0", "lights 2.0.0", False),
           probe("io.example.arena", "1.4.0", "arena 1.4.0", False)]),
    valid("prerelease-and-case",
          "Ids keep the manifest's spelling and match in any case; versions match exactly, prerelease and build "
          "included; installedAt reaches the largest safe integer.", MIXED,
          [probe("io.example.paint", "3.1.0-beta.2+build.7", "paint 3.1.0-beta.2+build.7", True),
           probe("Io.Example.Paint", "3.1.0-beta.2", "paint 3.1.0-beta.2+build.7", False),
           probe("Io.Example.Paint", "3.1.0-beta.2+build.7", "paint 3.1.0", False)]),
    valid("rebuilt",
          "A record the launcher rebuilt after damage: the entry that still read, and every other installed version "
          "withheld, except the ones the manager state and the player's own profiles name.", REBUILT,
          [probe("io.example.tools", "1.0.0", "tools 1.0.0", True),
           probe("io.example.tools", "1.2.0", "tools 1.2.0", True),
           probe("io.example.maps", "0.9.1", "maps 0.9.1", True),
           probe("io.example.maps", "0.9.1", "maps reinstalled", False)]),
    valid("both-lists", "A version may be in both lists; either one withholds it.", BOTH_LISTS,
          [probe("io.example.lights", "2.0.0", "lights 2.0.0", True)]),
    valid("escaped-spelling", "Escapes in member names and values read as the characters they stand for.",
          record([], [MAPS]), [probe("io.example.maps", "0.9.1", "maps 0.9.1", True)], spelling=ESCAPED),
    valid("negative-zero", "Minus zero is the integer zero, as both JSON readers decode it.", ZERO,
          [probe("io.example.lights", "2.0.0", "lights 2.0.0", True)], spelling=NEGATIVE_ZERO),
]

newer_cases = [
    {"id": "schema-2", "about": "A newer launcher's record, with a member this one does not know.",
     "text": json.dumps({"addedRecords": [], "futureField": True, "schemaVersion": 2, "versions": [], "withheld": []},
                        sort_keys=True, separators=(",", ":"))},
    {"id": "schema-2-alone", "about": "Only the version is read before a newer record is refused.",
     "text": '{"schemaVersion":2}'},
    {"id": "schema-largest", "about": "The largest 64-bit integer is still a version number.",
     "text": '{"schemaVersion":9223372036854775807,"versions":"another shape"}'},
]


def with_entry(field, value, base=LIGHTS):
    entry = dict(base)
    if value is None:
        entry.pop(field)
    else:
        entry[field] = value
    return record([entry])


def entry_text(field, raw):
    """A one-version record whose `field` is spelled exactly as `raw`."""
    text = canonical(record([LIGHTS]))
    old = '"' + field + '":' + json.dumps(LIGHTS[field], ensure_ascii=False)
    assert text.count(old) == 1
    return text.replace(old, '"' + field + '":' + raw, 1)


VALID_TEXT = canonical(SERVERS)
assert VALID_TEXT.startswith('{"addedRecords"') and VALID_TEXT.count('"schemaVersion":1') == 1
invalid_cases = [
    invalid("empty-file", "No bytes at all.", text=""),
    invalid("truncated", "A write cut short.", text=VALID_TEXT[:40]),
    invalid("array", "Not an object.", text="[]"),
    invalid("string", "Not an object.", text='"server-content"'),
    invalid("null", "Not an object.", text="null"),
    invalid("trailing-content", "One document and more.", text=VALID_TEXT + "x"),
    invalid("two-documents", "Two documents.", text=VALID_TEXT + VALID_TEXT),
    invalid("byte-order-mark", "A byte order mark is not part of the contract.", text=chr(0xFEFF) + VALID_TEXT),
    invalid("invalid-utf8", "Bytes that are not UTF-8, inside a string.",
            raw=VALID_TEXT.encode("utf-8").replace(b"arena", b"ar\xffna", 1)),
    invalid("duplicate-member", "A member twice, which one JSON reader would quietly collapse.",
            text=VALID_TEXT.replace('{"addedRecords"', '{"schemaVersion":1,"addedRecords"', 1)),
    invalid("duplicate-entry-member", "A member twice inside an entry.",
            text=VALID_TEXT.replace('"hidden":true', '"hidden":false,"hidden":true', 1)),
    invalid("duplicate-escaped-member", "Two spellings of one member name.",
            text=VALID_TEXT.replace('"schemaVersion":1', '"schemaVersion":1,"schema' + BACKSLASH + 'u0056ersion":1',
                                    1)),
    invalid("missing-schema-version", "No version.",
            document={key: value for key, value in SERVERS.items() if key != "schemaVersion"}),
    invalid("schema-zero", "Version zero.", document=dict(SERVERS, schemaVersion=0)),
    invalid("schema-negative", "A negative version.", document=dict(SERVERS, schemaVersion=-1)),
    invalid("schema-fraction", "A version with a fraction.",
            text=VALID_TEXT.replace('"schemaVersion":1', '"schemaVersion":1.0', 1)),
    invalid("schema-exponent", "A version with an exponent.",
            text=VALID_TEXT.replace('"schemaVersion":1', '"schemaVersion":1e0', 1)),
    invalid("schema-past-64-bits", "A version past 64 bits is not an integer to either reader.",
            text=VALID_TEXT.replace('"schemaVersion":1', '"schemaVersion":9223372036854775808', 1)),
    invalid("schema-string", "A version in quotes.", document=dict(SERVERS, schemaVersion="1")),
    invalid("missing-withheld", "A member missing.",
            document={key: value for key, value in SERVERS.items() if key != "withheld"}),
    invalid("missing-versions", "A member missing.",
            document={key: value for key, value in SERVERS.items() if key != "versions"}),
    invalid("missing-added-records", "A member missing.",
            document={key: value for key, value in SERVERS.items() if key != "addedRecords"}),
    invalid("extra-member", "A member version 1 does not have.", document=dict(SERVERS, note="kept by hand")),
    invalid("versions-object", "A list that is not one.", document=dict(SERVERS, versions={})),
    invalid("withheld-null", "A list that is not one.", document=dict(SERVERS, withheld=None)),
    invalid("entry-not-object", "An entry that is not an object.", document=dict(SERVERS, versions=["lights"])),
    invalid("entry-extra-field", "An entry with a member it does not have.",
            document=record([dict(LIGHTS, note="x")])),
    invalid("entry-missing-receipt", "An entry without its receipt.", document=with_entry("receipt", None)),
    invalid("entry-missing-hidden", "An entry without hidden.", document=with_entry("hidden", None)),
    invalid("id-leading-dot", "An id that does not start with a letter or digit.",
            document=with_entry("id", ".example.lights")),
    invalid("id-too-short", "An id of one character.", document=with_entry("id", "l")),
    invalid("id-too-long", "An id of 65 characters.", document=with_entry("id", "l" * 65)),
    invalid("id-space", "An id with a space.", document=with_entry("id", "io.example lights")),
    invalid("id-trailing-newline", "An id followed by a line feed.", document=with_entry("id", "io.example.lights\n")),
    invalid("id-nul", "An id with a NUL.", document=with_entry("id", "io.example" + chr(0) + "lights")),
    invalid("version-short", "Two components.", document=with_entry("version", "2.0")),
    invalid("version-leading-zero", "A leading zero.", document=with_entry("version", "02.0.0")),
    invalid("version-empty-prerelease", "An empty prerelease.", document=with_entry("version", "2.0.0-")),
    invalid("version-prerelease-zero", "A numeric prerelease identifier with a leading zero.",
            document=with_entry("version", "2.0.0-01")),
    invalid("version-trailing-newline", "A version followed by a line feed.",
            document=with_entry("version", "2.0.0\n")),
    invalid("version-too-long", "A version of 65 characters.",
            document=with_entry("version", "2.0.0-" + "a" * 59)),
    invalid("sha256-uppercase", "Digests are lowercase.", document=with_entry("sha256", LIGHTS["sha256"].upper())),
    invalid("sha256-short", "63 characters.", document=with_entry("sha256", LIGHTS["sha256"][:63])),
    invalid("receipt-not-hex", "Not hexadecimal.", document=with_entry("receipt", "z" * 64)),
    invalid("hidden-string", "A Boolean in quotes.", document=with_entry("hidden", "true")),
    invalid("hidden-number", "A Boolean as a number.", document=with_entry("hidden", 1)),
    invalid("installed-negative", "Before 1970.", text=entry_text("installedAt", "-1")),
    invalid("installed-fraction", "A fraction.", text=entry_text("installedAt", "1760745600.5")),
    invalid("installed-exponent", "An exponent, even of an integer.", text=entry_text("installedAt", "1e9")),
    invalid("installed-unsafe", "Past the largest safe integer.", text=entry_text("installedAt", str(2 ** 53))),
    invalid("installed-string", "A number in quotes.", text=entry_text("installedAt", '"1760745600"')),
    invalid("duplicate-version", "One version twice, in another case.",
            document=record([LIGHTS, dict(LIGHTS, id="IO.EXAMPLE.LIGHTS", hidden=False)])),
    invalid("duplicate-withheld", "One withheld version twice.", document=record([], [TOOLS_OLD, TOOLS_OLD])),
    invalid("withheld-extra-field", "A withheld entry with a member it does not have.",
            document=record([], [dict(TOOLS_OLD, hidden=True)])),
    invalid("withheld-missing-receipt", "A withheld entry without its receipt.",
            document=record([], [{"id": "io.example.tools", "version": "1.0.0"}])),
    invalid("withheld-bad-version", "A withheld entry whose version is not one.",
            document=record([], [dict(TOOLS_OLD, version="1.0")])),
    invalid("added-uppercase", "Added records are lowercase ids.", document=record(added=["Io.Example.Arena"])),
    invalid("added-duplicate", "An added record twice.", document=record(added=["io.example.arena"] * 2)),
    invalid("added-not-id", "An added record that is not an id.", document=record(added=["io example"])),
    invalid("added-number", "An added record that is not a string.", document=record(added=[5])),
    invalid("deep-nesting", "Nesting far past what the record has.",
            text=VALID_TEXT.replace('"versions":[', '"versions":[' + "[" * 200 + "]" * 200 + ",", 1)),
]


def big_record(count, id_of, first, second, receipts_from=0):
    versions = [{"id": id_of(index), "version": first, "sha256": "%064x" % index,
                 "receipt": "%064x" % (receipts_from + index + count), "hidden": True,
                 "installedAt": MAXIMUM_SAFE_INTEGER} for index in range(count)]
    withheld_entries = [{"id": id_of(index), "version": second, "receipt": "%064x" % (receipts_from + index + 2 * count)}
                        for index in range(count)]
    return versions, withheld_entries, [id_of(index) for index in range(count)]


def long_id(index: int) -> str:
    return ("m%04d" % index) + "x" * 59


def short_id(index: int) -> str:
    return "m%05d" % index


LONG_FIRST = "1.0.0-" + "a" * 58
LONG_SECOND = "2.0.0-" + "b" * 58
assert len(long_id(0)) == 64 and len(LONG_FIRST) == 64 and len(LONG_SECOND) == 64

versions_max, withheld_max, added_max = big_record(MAXIMUM_ENTRIES, long_id, LONG_FIRST, LONG_SECOND)
MAXIMAL = canonical(record(versions_max, withheld_max, added_max)).encode("utf-8")
assert len(MAXIMAL) <= MAXIMUM_BYTES, len(MAXIMAL)

versions_over, withheld_over, added_over = big_record(MAXIMUM_ENTRIES + 1, short_id, "1.0.0", "2.0.0")
TOO_MANY = {
    "versions": canonical(record(versions=versions_over)).encode("utf-8"),
    "withheld": canonical(record(withheld=withheld_over)).encode("utf-8"),
    "addedRecords": canonical(record(added=added_over)).encode("utf-8"),
}
for name, data in TOO_MANY.items():
    assert len(data) <= MAXIMUM_BYTES, (name, len(data))


def padded(length):
    return (canonical(EMPTY) + " " * (length - len(canonical(EMPTY)))).encode("utf-8")


def generated_case(case_id, about, recipe, data, is_valid, **extra):
    case = {"id": case_id, "about": about, "recipe": recipe, "valid": is_valid, "length": len(data),
            "sha256": hashlib.sha256(data).hexdigest()}
    case.update(extra)
    return case


generated = [
    generated_case("maximal", "The largest record: 1,024 entries in each list, every id and version 64 characters "
                   "long and every installedAt the largest safe integer. Both readers accept it, and it fits the byte "
                   "bound.", "maximal", MAXIMAL, True, entries=MAXIMUM_ENTRIES),
    generated_case("too-many-versions", "1,025 versions: the count, not the size, is refused.", "too-many",
                   TOO_MANY["versions"], False, entries=MAXIMUM_ENTRIES + 1, list="versions"),
    generated_case("too-many-withheld", "1,025 withheld versions.", "too-many", TOO_MANY["withheld"], False,
                   entries=MAXIMUM_ENTRIES + 1, list="withheld"),
    generated_case("too-many-added", "1,025 added records.", "too-many", TOO_MANY["addedRecords"], False,
                   entries=MAXIMUM_ENTRIES + 1, list="addedRecords"),
    generated_case("oversized", "The empty record followed by spaces to one byte past the bound. JSON allows the "
                   "spaces; the bound does not.", "padded", padded(MAXIMUM_BYTES + 1), False),
    generated_case("at-bound", "The empty record followed by spaces to exactly the bound, which both accept.",
                   "padded", padded(MAXIMUM_BYTES), True),
]

recipes = {
    "maximal": "For i from 0 to entries - 1, with id = 'm', i as 4 decimal digits, then 59 'x': versions[i] = {id, "
               "version: '1.0.0-' and 58 'a', sha256: i as 64 lowercase hexadecimal digits, receipt: i + entries as "
               "64 hexadecimal digits, hidden: true, installedAt: 9007199254740991}; withheld[i] = {id, version: "
               "'2.0.0-' and 58 'b', receipt: i + 2 * entries as 64 hexadecimal digits}; addedRecords[i] = id. "
               "Written canonically.",
    "too-many": "As maximal, but only the list named holds entries, the id is 'm' and i as 5 decimal digits, and the "
                "versions are '1.0.0' and '2.0.0'.",
    "padded": "The canonical empty record followed by spaces up to length bytes.",
}

document = {
    "schema": 1,
    "about": "The record of server content, BepInEx/TopiaForge/server-content.json, which the launcher writes and both "
             "the launcher and the game's loader read. See docs/internal/MultiplayerLaunchContract.md section 11 and "
             "this generator's docstring.",
    "limits": {"schemaVersion": SCHEMA_VERSION, "maximumBytes": MAXIMUM_BYTES, "maximumEntries": MAXIMUM_ENTRIES,
               "maximumSafeInteger": MAXIMUM_SAFE_INTEGER, "idLength": 64, "versionLength": 64},
    "valid": valid_cases,
    "newer": newer_cases,
    "invalid": invalid_cases,
    "generated": generated,
    "recipes": recipes,
}

ids = [case["id"] for case in valid_cases + newer_cases + invalid_cases + generated]
assert len(ids) == len(set(ids)), "case ids repeat"
OUT.parent.mkdir(parents=True, exist_ok=True)
OUT.write_text(json.dumps(document, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
print("wrote", OUT.relative_to(ROOT).as_posix(), len(ids), "cases")
