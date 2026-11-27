"""Generate outcome forward-compatibility vectors independently of either reader.

Run from the repository root: python -I tests/fixtures/multiplayer/generate_outcome_compat.py .
Input bytes deliberately contain hostile text; expected bytes never do.
"""
import copy
import json
import pathlib
import sys

root = pathlib.Path(sys.argv[1])
advice = "The multiplayer session could not continue. Try again; if it keeps failing, update TopiaForge."
marker = "future-private-203.0.113.77:47819"
detail = "Private provider detail at https://secret.example.org/session?token=fixture-secret"


def encode(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def record(role, terminal=False):
    value = {"schemaVersion": 2, "requestId": "compat-request", "sequence": 4,
             "kind": "session" if terminal else "launch", "blocks": [],
             "phase": "idle" if terminal or role == "host" else "connecting",
             "status": "succeeded" if terminal else "failed",
             "multiplayer": {"role": role, "intentId": "compat-intent", "serverId": "a" * 64,
                             "endReason" if terminal else "failure": marker, "detail": detail}}
    if not terminal:
        value["command"] = role + "-session"
    return value


valid = []
invalid = []


def accepts(name, value):
    expected = copy.deepcopy(value)
    block = expected["multiplayer"]
    block["endReason" if value["kind"] == "session" else "failure"] = "other"
    block["detail"] = advice
    valid.append({"id": name, "text": encode(value), "expected": encode(expected)})


def refuses(name, value):
    invalid.append({"id": name, "text": encode(value) if isinstance(value, dict) else value})


for role in ("join", "host"):
    for terminal in (False, True):
        label = role + ("-end" if terminal else "-failure")
        base = record(role, terminal)
        field = "endReason" if terminal else "failure"
        accepts(label, base)
        for suffix, reason in (("empty", ""), ("spaces", " new reason "),
                               ("escaped", 'private-"quote"\\path\n203.0.113.77'),
                               ("long", marker * 16), ("canonical", "other")):
            changed = copy.deepcopy(base)
            changed["multiplayer"][field] = reason
            accepts(label + "-" + suffix, changed)
        changed = copy.deepcopy(base)
        del changed["multiplayer"]["detail"]
        accepts(label + "-no-detail", changed)
        changed = copy.deepcopy(base)
        changed["sessionId"] = "last-world"
        accepts(label + "-world", changed)
        if terminal:
            changed = copy.deepcopy(base)
            changed["status"] = "failed"
            changed["error"] = {"code": "external", "message": "Cleanup failed."}
            accepts(label + "-cleanup-failed", changed)
        else:
            for status in ("succeeded", "cancelled"):
                changed = copy.deepcopy(base)
                changed["status"] = status
                refuses(label + "-" + status, changed)
        for suffix, bad in (("null", None), ("number", 7), ("bool", False), ("array", []), ("object", {})):
            changed = copy.deepcopy(base)
            changed["multiplayer"][field] = bad
            refuses(label + "-reason-" + suffix, changed)
        for suffix, bad in (("null", None), ("number", 7), ("empty", ""), ("control", "bad\ntext"),
                            ("padded", " padded"), ("long", "x" * 1025), ("noncharacter", "bad\uffff")):
            changed = copy.deepcopy(base)
            changed["multiplayer"]["detail"] = bad
            refuses(label + "-detail-" + suffix, changed)
        for field_name, bad in (("role", "spectator"), ("intentId", "../private"), ("serverId", "bad"),
                                ("address", marker), ("port", 27250), ("password", "fixture-secret")):
            changed = copy.deepcopy(base)
            changed["multiplayer"][field_name] = bad
            refuses(label + "-" + field_name, changed)
        for field_name, bad in (("schemaVersion", 3), ("requestId", "../private"), ("sequence", -1),
                                ("status", "finished"), ("phase", "future"), ("kind", "future")):
            changed = copy.deepcopy(base)
            changed[field_name] = bad
            refuses(label + "-" + field_name, changed)
        changed = copy.deepcopy(base)
        changed["multiplayer"]["admission"] = {"code": "other", "problems": ["Check the installed mods."]}
        if role == "join" and not terminal:
            accepts(label + "-admission", changed)
        else:
            refuses(label + "-admission", changed)
        changed["multiplayer"]["admission"]["code"] = "future"
        refuses(label + "-admission-code", changed)
        text = encode(base)
        refuses(label + "-duplicate", text.replace('"' + field + '":', '"' + field + '":"timeout","' + field + '":'))
        refuses(label + "-duplicate-escaped", text.replace('"role":', '"r\\u006fle":"join","role":'))
        refuses(label + "-truncated", text[:-1])
        refuses(label + "-trailing", text + marker)
        changed = copy.deepcopy(base)
        changed["multiplayer"]["failure" if terminal else "endReason"] = marker
        refuses(label + "-mixed-reasons", changed)
        if terminal:
            for field_name, bad in (("phase", "running"), ("command", role + "-session"), ("status", "failed")):
                changed = copy.deepcopy(base)
                changed[field_name] = bad
                refuses(label + "-terminal-" + field_name, changed)
        else:
            changed = copy.deepcopy(base)
            changed["multiplayer"]["failure"] = "timeout" if role == "host" else "port-in-use"
            refuses(label + "-wrong-role-reason", changed)
            changed = copy.deepcopy(base)
            changed["command"] = "join-session" if role == "host" else "host-session"
            refuses(label + "-wrong-command", changed)
            if role == "host":
                for phase in ("connecting", "synchronizing"):
                    changed = copy.deepcopy(base)
                    changed["phase"] = phase
                    refuses(label + "-" + phase, changed)

receiving = record("join")
receiving["phase"] = "synchronizing"
receiving["sessionId"] = "receiving-world"
accepts("join-receiving", receiving)
refuses("malformed-private-field", '{"' + marker + '":}')
refuses("malformed-private-string", encode(record("join")).replace(marker, marker + '\\q'))
path = root / "tests/fixtures/multiplayer/progress-v2/forward-compat.json"
path.write_text(json.dumps({"schema": 1, "valid": valid, "invalid": invalid}, indent=2, ensure_ascii=False) + "\n",
                encoding="utf-8", newline="\n")
print("Outcome compatibility vectors:", len(valid), "valid,", len(invalid), "invalid")
