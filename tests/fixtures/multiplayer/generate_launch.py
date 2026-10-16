"""Generate tests/fixtures/multiplayer/{launch-v5,progress-v2,channels}/cases.json from first principles,
independently of the C# and Dart code.

Session launch documents are sorted JSON: every object's keys in code point order, no white space, UTF-8, and only
the escapes Python's json module writes with ensure_ascii=False (the quotation mark, the backslash and U+0000 to
U+001F). The game and the launcher write exactly these bytes. The FNV-1a package-set digest is computed here too, and
the server ids, descriptor hashes and package digests come from descriptor/cases.json, so these documents name the
servers the other fixtures describe. Nothing is random, so a rerun reproduces every byte.

Run from the repository root: python -I tests/fixtures/multiplayer/generate_launch.py .
"""
import copy
import json
import pathlib
import sys

ROOT = pathlib.Path(sys.argv[1])
OUT = ROOT / "tests" / "fixtures" / "multiplayer"


def sorted_json(value) -> str:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def reordered(value) -> str:
    """The same document with every object's keys reversed and indented: readers accept any order and spacing."""
    def reverse(item):
        if isinstance(item, dict):
            return {key: reverse(item[key]) for key in sorted(item, reverse=True)}
        if isinstance(item, list):
            return [reverse(element) for element in item]
        return item
    return json.dumps(reverse(value), indent=2, ensure_ascii=False)


def write(name: str, value) -> None:
    path = OUT / name
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")


def package_set_digest(packages) -> str:
    """FNV-1a 64 over the sorted id@version entries' UTF-16LE code units, entries separated by one LF byte."""
    value = 14695981039346656037
    entries = sorted(item["id"] + "@" + item["version"] for item in packages)
    for index, entry in enumerate(entries):
        if index > 0:
            value = ((value ^ 10) * 1099511628211) & 0xFFFFFFFFFFFFFFFF
        for byte in entry.encode("utf-16-le"):
            value = ((value ^ byte) * 1099511628211) & 0xFFFFFFFFFFFFFFFF
    return format(value, "016x")


def valid(case_id, about, document, **extra):
    case = {"id": case_id, "about": about, "document": document, "bytes": sorted_json(document),
            "reordered": reordered(document)}
    case.update(extra)
    return case


def invalid(case_id, about, document=None, text=None):
    return {"id": case_id, "about": about, "text": sorted_json(document) if text is None else text}


def mutated(base, change):
    document = copy.deepcopy(base)
    change(document)
    return document


DESCRIPTORS = json.loads((OUT / "descriptor" / "cases.json").read_text(encoding="utf-8"))["valid"]
PUBLIC = DESCRIPTORS[0]
PINS = {"serverId": PUBLIC["serverId"], "descriptorSha256": PUBLIC["descriptorSha256"],
        "packageDigest": PUBLIC["packageDigest"]}
OTHER = DESCRIPTORS[1]
PACKAGES = sorted(({"id": item["id"], "version": item["version"]} for item in PUBLIC["descriptor"]["packages"]),
                  key=lambda item: (item["id"], item["version"]))
INTENT = "intent-7d3e1f20"
REQUEST = "0f1e2d3c4b5a69788796a5b4c3d2e1f0"
ORIGIN = "https://mp.topiaforge.dev"
INVITE = "ABCDE12345"

# Direct targets use ports and hosts that appear nowhere else, so a privacy check can look for them.
LAN_HOST, LAN_PORT = "192.168.77.31", 41297
PUBLIC_HOST, PUBLIC_PORT = "203.0.113.7", 47613
NAME_HOST, NAME_PORT = "gameroom.local", 38611
IPV6_HOST, IPV6_PORT = "fd00:7::31", 39023
DIRECT_MARKERS = [LAN_HOST, PUBLIC_HOST, NAME_HOST, IPV6_HOST, "play.example.org"] + \
    [str(port) for port in (LAN_PORT, PUBLIC_PORT, NAME_PORT, IPV6_PORT)]


def transport(privacy="relay-only", port=0, upnp=False, lan=True, consent=None):
    value = {"privacy": privacy, "port": port, "upnp": upnp, "lan": lan}
    if consent is not None:
        value["directConsent"] = consent
    return value


def consent(intent=INTENT, disclosure=1):
    return {"intentId": intent, "disclosure": disclosure}


def host_options(**overrides):
    value = {"name": "Sam's Sandbox", "maxPlayers": 8, "visibility": "lan", "voice": True,
             "passwordRequired": False, "servePackages": False}
    value.update(overrides)
    return value


def target(kind, **fields):
    value = {"kind": kind, **PINS}
    value.update(fields)
    return value


def session(role, **overrides):
    value = {"role": role, "intentId": INTENT, "displayName": "Sam", "developerMode": False,
             "transport": transport(port=27250 if role == "host" else 0)}
    value.update(overrides)
    return value


PLAN = {"targetId": "io.github.furroxide.topiaforge.sandbox.play",
        "gamemodeId": "io.github.furroxide.topiaforge.sandbox.open",
        "worldId": "io.github.furroxide.topiaforge.sandbox.city.east",
        "worldFamilyId": "io.github.furroxide.topiaforge.sandbox.city", "transition": "scene-replacement",
        "request": {"targetId": "io.github.furroxide.topiaforge.sandbox.play",
                    "worldOverride": "io.github.furroxide.topiaforge.sandbox.city.east"},
        "packages": PACKAGES, "digest": package_set_digest(PACKAGES)}


def v5(session_value, plan=None, inherit=False, packages=PACKAGES):
    document = {
        "schemaVersion": 5,
        "profileId": "mp-3f9a1c0d5e7b2a46",
        "profileRevision": 0,
        "requestId": REQUEST,
        "command": "host-session" if session_value["role"] == "host" else "join-session",
        "inheritManagerModState": inherit,
        "enabledMods": sorted(item["id"] for item in packages),
        "selectedVersions": {item["id"]: item["version"] for item in packages},
        "packages": packages,
        "digest": package_set_digest(packages),
        "session": session_value,
    }
    if plan is not None:
        document["plan"] = plan
    return document


def fields(document):
    """What both readers must extract from a valid request."""
    value = document["session"]
    picked = {
        "command": document["command"],
        "role": value["role"],
        "executableCommand": "launch-target" if "plan" in document else "main-menu",
        "intentId": value["intentId"],
        "displayName": value["displayName"],
        "developerMode": value["developerMode"],
        "privacy": value["transport"]["privacy"],
        "consented": "directConsent" in value["transport"],
        "port": value["transport"]["port"],
        "serviceOrigin": value.get("serviceOrigin"),
        "targetKind": value["target"]["kind"] if "target" in value else None,
        "hostName": value["host"]["name"] if "host" in value else None,
        "packageCount": len(document["packages"]),
    }
    return picked


HOST_TARGET = v5(session("host", host=host_options()), plan=PLAN)
HOST_PUBLIC = v5(session("host", displayName="Zoë 東京", serviceOrigin=ORIGIN,
                         transport=transport("direct", port=0, upnp=True, consent=consent()),
                         host=host_options(name="Zoë's \"quiet\" server \\ 東京", maxPlayers=16, visibility="public",
                                           passwordRequired=True, voice=False),
                         devInstance={"index": 0, "count": 2}), inherit=True)
JOIN_INVITE = v5(session("join", serviceOrigin=ORIGIN, target=target("invite", inviteCode=INVITE)))
JOIN_LAN = v5(session("join", target=target("listing", source="lan")))
JOIN_INTERNET = v5(session("join", serviceOrigin=ORIGIN, target=target("listing", source="internet"),
                           devInstance={"index": 1, "count": 2}))
JOIN_DIRECT_LAN = v5(session("join", target=target("direct", host=LAN_HOST, port=LAN_PORT)))
JOIN_DIRECT_NAME = v5(session("join", target=target("direct", host=NAME_HOST, port=NAME_PORT)))
JOIN_DIRECT_IPV6 = v5(session("join", target=target("direct", host=IPV6_HOST, port=IPV6_PORT)))
JOIN_DIRECT_PUBLIC = v5(session("join", displayName="A B", transport=transport("direct", consent=consent()),
                                target=target("direct", host=PUBLIC_HOST, port=PUBLIC_PORT)))
HOST_SERVES = v5(session("host", developerMode=True, host=host_options(servePackages=True)), plan=PLAN)
JOIN_INVITE_PORT = v5(session("join", serviceOrigin="https://0x7f.topiaforge.dev:8443",
                              target=target("invite", inviteCode=INVITE)))

launch_valid = [
    valid("host-target", "A host starts a launch target, then opens it on the LAN under relay-only privacy.",
          HOST_TARGET, fields=fields(HOST_TARGET)),
    valid("host-menu-public-direct", "A public host at the main menu: direct privacy with its consent, UPnP, any "
          "port, a password asked for in game, escapes and non-ASCII text, and a developer peer slot.",
          HOST_PUBLIC, fields=fields(HOST_PUBLIC)),
    valid("join-invite", "Joining an invite the control plane resolved.", JOIN_INVITE, fields=fields(JOIN_INVITE)),
    valid("join-lan-listing", "Joining a LAN listing needs no service.", JOIN_LAN, fields=fields(JOIN_LAN)),
    valid("join-internet-listing", "Joining an Internet listing, as a developer peer.", JOIN_INTERNET,
          fields=fields(JOIN_INTERNET)),
    valid("join-direct-lan", "Relay-only allows a direct target on the local network.", JOIN_DIRECT_LAN,
          fields=fields(JOIN_DIRECT_LAN)),
    valid("join-direct-local-name", "Relay-only allows a name that resolves on the local network.", JOIN_DIRECT_NAME,
          fields=fields(JOIN_DIRECT_NAME)),
    valid("join-direct-unique-local", "Relay-only allows a unique-local IPv6 address.", JOIN_DIRECT_IPV6,
          fields=fields(JOIN_DIRECT_IPV6)),
    valid("join-direct-public-consented", "A public direct target with the player's consent to direct play; a "
          "display name with a line separator inside is written raw.", JOIN_DIRECT_PUBLIC,
          fields=fields(JOIN_DIRECT_PUBLIC)),
    valid("host-serves-packages", "In developer mode, a host may serve its packages.", HOST_SERVES,
          fields=fields(HOST_SERVES)),
    valid("join-invite-service-port", "A service origin writes a port other than 443, and only its last label must "
          "not be a number.", JOIN_INVITE_PORT, fields=fields(JOIN_INVITE_PORT)),
]


def drop(*path):
    def change(document):
        node = document
        for key in path[:-1]:
            node = node[key]
        del node[path[-1]]
    return change


def put(value, *path):
    def change(document):
        node = document
        for key in path[:-1]:
            node = node[key]
        node[path[-1]] = value
    return change


good = sorted_json(JOIN_INVITE)
launch_invalid = [
    invalid("session-password", "Passwords are typed in game, never staged.",
            mutated(JOIN_INVITE, put("hunter2", "session", "password"))),
    invalid("host-password", "Host options never carry the password either.",
            mutated(HOST_TARGET, put("hunter2", "session", "host", "password"))),
    invalid("relay-only-public-direct", "Under relay-only, a direct target beyond the local network lacks the "
            "player's consent to direct play.", mutated(JOIN_DIRECT_PUBLIC, put(transport(), "session", "transport"))),
    invalid("relay-only-public-name", "Under relay-only, a name beyond the local network is never looked up.",
            mutated(JOIN_DIRECT_NAME, put("play.example.org", "session", "target", "host"))),
    invalid("direct-without-consent", "Direct privacy requires the consent record.",
            mutated(JOIN_DIRECT_PUBLIC, drop("session", "transport", "directConsent"))),
    invalid("consent-under-relay-only", "Relay-only never carries a consent.",
            mutated(JOIN_DIRECT_LAN, put(consent(), "session", "transport", "directConsent"))),
    invalid("consent-for-another-intent", "A consent covers only the session it was given for.",
            mutated(JOIN_DIRECT_PUBLIC, put(consent(intent="intent-elsewhere"), "session", "transport",
                                            "directConsent"))),
    invalid("consent-before-disclosure", "A consent names an acknowledged disclosure, version 1 or later.",
            mutated(JOIN_DIRECT_PUBLIC, put(consent(disclosure=0), "session", "transport", "directConsent"))),
    invalid("command-role-mismatch", "The command agrees with the session role.",
            mutated(JOIN_INVITE, put("host-session", "command"))),
    invalid("join-with-plan", "A join never carries a plan.", mutated(JOIN_LAN, put(PLAN, "plan"))),
    invalid("join-inherits", "A join runs exactly its planned packages.",
            mutated(JOIN_LAN, put(True, "inheritManagerModState"))),
    invalid("join-with-host-options", "A join carries no host options.",
            mutated(JOIN_LAN, put(host_options(), "session", "host"))),
    invalid("host-with-target", "A host carries no join target.",
            mutated(HOST_TARGET, put(target("listing", source="lan"), "session", "target"))),
    invalid("host-without-options", "A host carries its options.", mutated(HOST_TARGET, drop("session", "host"))),
    invalid("join-without-target", "A join carries its target.", mutated(JOIN_LAN, drop("session", "target"))),
    invalid("join-maps-port", "A join never maps a port.", mutated(JOIN_LAN, put(True, "session", "transport", "upnp"))),
    invalid("invite-without-service", "An invite needs the control plane.",
            mutated(JOIN_INVITE, drop("session", "serviceOrigin"))),
    invalid("internet-listing-without-service", "An Internet listing needs the control plane.",
            mutated(JOIN_INTERNET, drop("session", "serviceOrigin"))),
    invalid("lan-listing-without-lan", "A LAN listing needs LAN discovery.",
            mutated(JOIN_LAN, put(False, "session", "transport", "lan"))),
    invalid("lan-host-without-lan", "LAN visibility needs LAN discovery.",
            mutated(HOST_TARGET, put(False, "session", "transport", "lan"))),
    invalid("invite-host-without-service", "Invite-only hosting needs the control plane.",
            mutated(HOST_TARGET, put("invite", "session", "host", "visibility"))),
    invalid("safe-mode-member", "Safe mode never hosts or joins, so V5 has no safeMode.",
            mutated(JOIN_LAN, put(False, "safeMode"))),
    invalid("version-4", "The V5 reader takes version 5 only.", mutated(JOIN_LAN, put(4, "schemaVersion"))),
    invalid("service-ip-literal", "The service origin is a DNS name, never an address.",
            mutated(JOIN_INVITE, put("https://203.0.113.5", "session", "serviceOrigin"))),
    invalid("service-plain-http", "The service origin is HTTPS.",
            mutated(JOIN_INVITE, put("http://mp.topiaforge.dev", "session", "serviceOrigin"))),
    invalid("service-with-path", "The service origin has no path.",
            mutated(JOIN_INVITE, put(ORIGIN + "/v1", "session", "serviceOrigin"))),
    invalid("service-explicit-null", "Optional members are omitted, never null.",
            mutated(JOIN_LAN, put(None, "session", "serviceOrigin"))),
    invalid("display-name-short", "Display names are 2 to 24 UTF-16 code units.",
            mutated(JOIN_LAN, put("A", "session", "displayName"))),
    invalid("display-name-padded", "Display text has no surrounding white space.",
            mutated(JOIN_LAN, put(" Sam", "session", "displayName"))),
    invalid("display-name-control", "Display text has no control characters.",
            mutated(JOIN_LAN, put("Ch\u0007arl", "session", "displayName"))),
    invalid("direct-host-not-canonical", "Hosts are written in their one canonical spelling.",
            mutated(JOIN_DIRECT_LAN, put("192.168.077.31", "session", "target", "host"))),
    invalid("direct-host-uppercase", "Host names are lowercase.",
            mutated(JOIN_DIRECT_NAME, put("GameRoom.local", "session", "target", "host"))),
    invalid("direct-port-zero", "A direct target names a port from 1 to 65535.",
            mutated(JOIN_DIRECT_LAN, put(0, "session", "target", "port"))),
    invalid("dev-slot-outside", "A peer slot lies inside its peers.",
            mutated(JOIN_INTERNET, put({"index": 2, "count": 2}, "session", "devInstance"))),
    invalid("dev-too-many-peers", "The developer loop runs 2 to 8 peers.",
            mutated(JOIN_INTERNET, put({"index": 0, "count": 9}, "session", "devInstance"))),
    invalid("invite-lowercase", "Invite codes are canonical.",
            mutated(JOIN_INVITE, put("abcde12345", "session", "target", "inviteCode"))),
    invalid("descriptor-hash-uppercase", "Hashes are lowercase hexadecimal.",
            mutated(JOIN_LAN, put(PINS["descriptorSha256"].upper(), "session", "target", "descriptorSha256"))),
    invalid("digest-mismatch", "The package digest covers the packages.",
            mutated(JOIN_LAN, put("0" * 16, "digest"))),
    invalid("too-few-players", "Sessions hold 2 to 16 players.",
            mutated(HOST_TARGET, put(1, "session", "host", "maxPlayers"))),
    invalid("too-many-players", "Sessions hold 2 to 16 players.",
            mutated(HOST_TARGET, put(17, "session", "host", "maxPlayers"))),
    invalid("unknown-role", "Roles are host and join.", mutated(JOIN_LAN, put("spectator", "session", "role"))),
    invalid("unknown-target-kind", "Targets are invites, listings and direct targets.",
            mutated(JOIN_LAN, put("relay", "session", "target", "kind"))),
    invalid("duplicate-member", "A member appears once.",
            text=good.replace('"intentId":', '"intentId":"intent-other","intentId":', 1)),
    invalid("serve-packages-outside-developer-mode", "Only developer mode lets a host serve its packages.",
            mutated(HOST_SERVES, put(False, "session", "developerMode"))),
    invalid("developer-mode-missing", "Every session says whether the launcher runs in developer mode.",
            mutated(JOIN_LAN, drop("session", "developerMode"))),
    invalid("developer-mode-not-boolean", "Developer mode is a boolean.",
            mutated(JOIN_LAN, put("yes", "session", "developerMode"))),
    invalid("display-name-noncharacter", "Display text never holds U+FFFF, which the game's reader refuses.",
            mutated(JOIN_LAN, put("Sam\uffff", "session", "displayName"))),
    invalid("host-name-noncharacter", "Display text never holds U+FFFE either.",
            mutated(HOST_TARGET, put("Sandbox \ufffe", "session", "host", "name"))),
    invalid("service-hex-address", "A name that is a hexadecimal number is an IPv4 address (127.0.0.1).",
            mutated(JOIN_INVITE, put("https://0x7f000001", "session", "serviceOrigin"))),
    invalid("service-hex-last-label", "A name whose last label is a hexadecimal number reads as an address.",
            mutated(JOIN_INVITE, put("https://lan.0x1f", "session", "serviceOrigin"))),
    invalid("service-numeric-last-label", "A name whose last label is a decimal number reads as an address.",
            mutated(JOIN_INVITE, put("https://play.example.42", "session", "serviceOrigin"))),
    invalid("service-default-port", "The default port 443 is never written out.",
            mutated(JOIN_INVITE, put(ORIGIN + ":443", "session", "serviceOrigin"))),
    invalid("service-uppercase", "Service origins are lowercase.",
            mutated(JOIN_INVITE, put("https://MP.topiaforge.dev", "session", "serviceOrigin"))),
]

write("launch-v5/cases.json", {
    "schema": 1,
    "about": "One-shot launch request V5 (host-session and join-session). Valid cases parse to the listed fields, "
             "read the same in any member order, and write back to bytes exactly; invalid cases are refused. Every "
             "other launch keeps writing V4. Generated by generate_launch.py.",
    "directMarkers": DIRECT_MARKERS,
    "valid": launch_valid,
    "invalid": launch_invalid,
})

# ---------------------------------------------------------------- progress and outcome V2

SESSION_ID = "session-0a1b2c3d"
OTHER_SERVER = OTHER["serverId"]


def progress(phase, block, sequence=3, **extra):
    value = {"schemaVersion": 2, "requestId": REQUEST, "sequence": sequence, "phase": phase, "session": block}
    value.update(extra)
    return value


def progress_session(role, state, **extra):
    value = {"role": role, "intentId": INTENT, "state": state}
    value.update(extra)
    return value


JOIN_CONNECTING = progress("connecting", progress_session("join", "connecting", serverId=PINS["serverId"]))
JOIN_PLAYING = progress("running", progress_session("join", "in-session", serverId=PINS["serverId"], players=3,
                                                    maxPlayers=8), sequence=9, sessionId=SESSION_ID)
HOST_HOSTING = progress("running", progress_session("host", "hosting", serverId=OTHER_SERVER, players=1, maxPlayers=16,
                                                    inviteCode=INVITE), sequence=12, sessionId=SESSION_ID,
                        nativeBusy=False)
HOST_PENDING = progress("preparing", progress_session("host", "pending"), sequence=0)
JOIN_PASSWORD = progress("connecting", progress_session("join", "awaiting-password", serverId=PINS["serverId"]),
                         sequence=5)

progress_valid = [
    valid("join-connecting", "A join connecting to its server.", JOIN_CONNECTING),
    valid("join-in-session", "A join in session, following the host's world.", JOIN_PLAYING),
    valid("host-hosting", "A host with players and its invite code.", HOST_HOSTING),
    valid("host-pending", "A host whose session has not started yet.", HOST_PENDING),
    valid("join-awaiting-password", "A join waiting for the password typed in game.", JOIN_PASSWORD),
]
progress_invalid = [
    invalid("host-connecting", "Only a join connects.", mutated(HOST_PENDING, put("connecting", "phase"))),
    invalid("host-synchronizing", "Only a join synchronizes.", mutated(HOST_PENDING, put("synchronizing", "phase"))),
    invalid("join-hosting-state", "A join's states are its own.",
            mutated(JOIN_CONNECTING, put("hosting", "session", "state"))),
    invalid("host-join-state", "A host's states are its own.",
            mutated(HOST_PENDING, put("in-session", "session", "state"))),
    invalid("players-without-maximum", "Players and the maximum come together.",
            mutated(JOIN_PLAYING, drop("session", "maxPlayers"))),
    invalid("players-over-maximum", "No more players than the maximum.",
            mutated(JOIN_PLAYING, put(9, "session", "players"))),
    invalid("maximum-17", "Sessions hold at most 16 players.", mutated(JOIN_PLAYING, put(17, "session", "maxPlayers"))),
    invalid("invite-on-join", "Only a host reports an invite code.",
            mutated(JOIN_CONNECTING, put(INVITE, "session", "inviteCode"))),
    invalid("v1-with-session", "Version 1 carries no session block.",
            mutated(JOIN_CONNECTING, put(1, "schemaVersion"))),
    invalid("v1-connecting", "Version 1 has no connecting phase.",
            {"schemaVersion": 1, "requestId": REQUEST, "sequence": 1, "phase": "connecting"}),
    invalid("v2-without-session", "Version 2 carries its session block.",
            mutated(JOIN_CONNECTING, drop("session"))),
    invalid("session-address", "A session block never names an address.",
            mutated(JOIN_CONNECTING, put(LAN_HOST, "session", "host"))),
    invalid("session-port", "A session block never names a port.",
            mutated(HOST_HOSTING, put(27250, "session", "port"))),
    invalid("server-id-uppercase", "Server ids are lowercase.",
            mutated(JOIN_CONNECTING, put(PINS["serverId"].upper(), "session", "serverId"))),
    invalid("server-id-null", "Optional members are omitted, never null.",
            mutated(JOIN_CONNECTING, put(None, "session", "serverId"))),
    invalid("unknown-state", "States are a closed set.", mutated(JOIN_CONNECTING, put("relaying", "session", "state"))),
]


def outcome(kind, status, phase, block, sequence=10, **extra):
    value = {"schemaVersion": 2, "kind": kind, "requestId": REQUEST, "sequence": sequence, "status": status,
             "phase": phase, "blocks": [], "multiplayer": block}
    if kind == "launch":
        value["command"] = "host-session" if block["role"] == "host" else "join-session"
    value.update(extra)
    return value


def multiplayer(role, **extra):
    value = {"role": role, "intentId": INTENT}
    value.update(extra)
    return value


JOIN_STARTED = outcome("launch", "succeeded", "running", multiplayer("join", serverId=PINS["serverId"]),
                       sessionId=SESSION_ID)
JOIN_PASSWORD_FAILED = outcome("launch", "failed", "connecting", multiplayer(
    "join", serverId=PINS["serverId"], failure="password-rejected", detail="The password was wrong.",
    admission={"code": "password-wrong", "problems": []}))
JOIN_CONTENT_CHANGED = outcome("launch", "failed", "connecting", multiplayer(
    "join", serverId=PINS["serverId"], failure="admission-changed",
    detail="The server's packages changed after you planned the join.",
    admission={"code": "content", "problems": ["Zombies 0.2.0 is missing.", "Sandbox 0.2.0 differs."]}))
HOST_PORT_FAILED = outcome("launch", "failed", "idle", multiplayer(
    "host", failure="port-in-use", detail="Another program is using port 27250."))
HOST_MENU_STARTED = outcome("launch", "succeeded", "idle", multiplayer("host", serverId=OTHER_SERVER))
JOIN_ENDED = outcome("session", "succeeded", "idle", multiplayer("join", serverId=PINS["serverId"],
                                                                 endReason="host-ended"), sequence=40,
                     sessionId=SESSION_ID)
JOIN_REJECTED_END = outcome("session", "succeeded", "idle", multiplayer(
    "join", endReason="admission-rejected", admission={"code": "full", "problems": []}), sequence=41)
JOIN_CANCELLED = outcome("launch", "cancelled", "synchronizing", multiplayer("join"), sequence=7)
HOST_ERROR = outcome("launch", "failed", "preparing", multiplayer("host"), sequence=2,
                     error={"code": "unavailable", "message": "The multiplayer provider is not loaded."})

outcome_valid = [
    valid("join-started", "A join in session, with the host's world running.", JOIN_STARTED),
    valid("join-password-rejected", "A wrong password, with the host's admission report.", JOIN_PASSWORD_FAILED),
    valid("join-admission-changed", "The server's content changed after planning; the launcher plans once more.",
          JOIN_CONTENT_CHANGED),
    valid("host-port-in-use", "A host that could not bind its port.", HOST_PORT_FAILED),
    valid("host-started-at-menu", "A host open at the main menu, without a gamemode session.", HOST_MENU_STARTED),
    valid("join-session-ended", "The host ended the session.", JOIN_ENDED),
    valid("join-session-rejected", "A reconnect the host's admission refused.", JOIN_REJECTED_END),
    valid("join-cancelled", "The player cancelled in game.", JOIN_CANCELLED),
    valid("host-error", "A runtime error before the session started.", HOST_ERROR),
]
outcome_invalid = [
    invalid("success-with-failure", "A success carries no failure.",
            mutated(JOIN_STARTED, put("timeout", "multiplayer", "failure"))),
    invalid("failure-when-cancelled", "Only a failed outcome carries a failure.",
            mutated(JOIN_CANCELLED, put("timeout", "multiplayer", "failure"))),
    invalid("host-join-failure", "A host fails with a host's failures.",
            mutated(HOST_PORT_FAILED, put("timeout", "multiplayer", "failure"))),
    invalid("join-host-failure", "A join fails with a join's failures.",
            mutated(JOIN_PASSWORD_FAILED, put("port-in-use", "multiplayer", "failure"))),
    invalid("admission-without-failure", "Only a failure carries the admission report.",
            mutated(JOIN_STARTED, put({"code": "content", "problems": []}, "multiplayer", "admission"))),
    invalid("admission-on-host", "A host gets no admission report.",
            mutated(HOST_PORT_FAILED, put({"code": "full", "problems": []}, "multiplayer", "admission"))),
    invalid("terminal-with-command", "A terminal outcome names no command.",
            mutated(JOIN_ENDED, put("join-session", "command"))),
    invalid("terminal-without-reason", "A terminal outcome says why the session ended.",
            mutated(JOIN_ENDED, drop("multiplayer", "endReason"))),
    invalid("terminal-not-idle", "A terminal outcome is Idle.", mutated(JOIN_ENDED, put("running", "phase"))),
    invalid("terminal-with-failure", "A terminal outcome reports an end reason, not a launch failure.",
            mutated(JOIN_ENDED, put("timeout", "multiplayer", "failure"))),
    invalid("terminal-admission-other-reason", "Only an admission rejection carries its report.",
            mutated(JOIN_REJECTED_END, put("kicked", "multiplayer", "endReason"))),
    invalid("launch-with-end-reason", "A launch outcome has no end reason.",
            mutated(JOIN_STARTED, put("left", "multiplayer", "endReason"))),
    invalid("command-role-mismatch", "The command agrees with the role.",
            mutated(JOIN_STARTED, put("host-session", "command"))),
    invalid("v1-command-in-v2", "Version 2 names a session command.",
            mutated(JOIN_STARTED, put("main-menu", "command"))),
    invalid("session-command-in-v1", "Version 1 names a gamemode command.",
            {"schemaVersion": 1, "kind": "launch", "requestId": REQUEST, "command": "join-session", "sequence": 1,
             "status": "failed", "phase": "idle", "blocks": [], "error": {"code": "unknown", "message": "x"}}),
    invalid("running-without-session-id", "A success at Running names its gamemode session.",
            mutated(JOIN_STARTED, drop("sessionId"))),
    invalid("idle-with-session-id", "A success at Idle names no gamemode session.",
            mutated(HOST_MENU_STARTED, put(SESSION_ID, "sessionId"))),
    invalid("success-while-connecting", "A launch succeeds at Running or Idle.",
            mutated(JOIN_STARTED, put("connecting", "phase"))),
    invalid("host-synchronizing", "Only a join synchronizes.", mutated(HOST_PORT_FAILED, put("synchronizing", "phase"))),
    invalid("failed-without-cause", "A failure has a block, an error or a session failure.",
            mutated(HOST_PORT_FAILED, drop("multiplayer", "failure"))),
    invalid("detail-control-character", "Details are display text.",
            mutated(JOIN_PASSWORD_FAILED, put("Wrong\npassword.", "multiplayer", "detail"))),
    invalid("detail-noncharacter", "Display text never holds U+FFFF.",
            mutated(JOIN_PASSWORD_FAILED, put("Wrong password \uffff", "multiplayer", "detail"))),
    invalid("problem-noncharacter", "Admission problems are display text without U+FFFE.",
            mutated(JOIN_CONTENT_CHANGED, put(["Zombies \ufffe"], "multiplayer", "admission", "problems"))),
    invalid("admission-65-problems", "An admission report lists at most 64 problems.",
            mutated(JOIN_CONTENT_CHANGED, put(["Problem %d." % index for index in range(65)], "multiplayer",
                                              "admission", "problems"))),
    invalid("multiplayer-address", "The multiplayer block never names an address.",
            mutated(JOIN_PASSWORD_FAILED, put(LAN_HOST, "multiplayer", "address"))),
    invalid("unknown-failure", "Failures are a closed set.",
            mutated(JOIN_PASSWORD_FAILED, put("wrong-password", "multiplayer", "failure"))),
    invalid("v2-without-multiplayer", "Version 2 carries its multiplayer block.",
            mutated(JOIN_STARTED, drop("multiplayer"))),
]

FAILURE_CODES = {
    "join": ["timeout", "nat-traversal-failed", "relay-unavailable", "server-full", "password-rejected", "kicked",
             "protocol-mismatch", "admission-changed", "disconnected", "direct-not-allowed", "snapshot-failed"],
    "host": ["port-in-use", "identity-unavailable", "network-stopped", "service-unavailable"],
}
write("progress-v2/cases.json", {
    "schema": 1,
    "about": "Progress V2 and outcome V2, which a session launch (request V5) reports. Valid cases read the same in "
             "any member order and write back to bytes exactly; invalid cases are refused. None names an address or "
             "a port. Generated by generate_launch.py.",
    "failureCodes": FAILURE_CODES,
    "endReasons": ["left", "host-ended", "kicked", "banned", "timed-out", "replaced", "admission-rejected",
                   "protocol-mismatch", "transport-failed"],
    "progress": {"valid": progress_valid, "invalid": progress_invalid},
    "outcome": {"valid": outcome_valid, "invalid": outcome_invalid},
})

# ---------------------------------------------------------------- channels and preferences

PROCESS = "a1b2c3d4e5f60718293a4b5c6d7e8f90"


def handoff(target_value, handoff_id="handoff-01"):
    return {"schemaVersion": 1, "requestId": PROCESS, "handoffId": handoff_id, "target": target_value}


HANDOFF_INVITE = handoff({"kind": "invite", "inviteCode": INVITE})
HANDOFF_LAN = handoff({"kind": "listing", "serverId": PINS["serverId"], "source": "lan"}, "handoff-02")
HANDOFF_INTERNET = handoff({"kind": "listing", "serverId": OTHER_SERVER, "source": "internet"}, "handoff-03")
HANDOFF_CURRENT = handoff({"kind": "current"}, "handoff-04")
handoff_valid = [
    valid("invite", "The player typed an invite code in game.", HANDOFF_INVITE),
    valid("lan-listing", "The player picked a LAN session in game.", HANDOFF_LAN),
    valid("internet-listing", "The player picked an Internet listing in game.", HANDOFF_INTERNET),
    valid("current", "The current session's admission changed; plan it once more.", HANDOFF_CURRENT),
]
handoff_invalid = [
    invalid("direct-target", "A handoff never carries an address; typed addresses go through a link.",
            handoff({"kind": "direct", "serverId": PINS["serverId"], "host": LAN_HOST, "port": LAN_PORT})),
    invalid("listing-with-host", "A listing names its server, never where it is.",
            mutated(HANDOFF_LAN, put(LAN_HOST, "target", "host"))),
    invalid("current-with-members", "The current target is the kind alone.",
            mutated(HANDOFF_CURRENT, put(PINS["serverId"], "target", "serverId"))),
    invalid("unknown-kind", "Handoff targets are invites, listings and the current session.",
            handoff({"kind": "relay"})),
    invalid("missing-handoff-id", "A handoff names itself.", mutated(HANDOFF_INVITE, drop("handoffId"))),
    invalid("version-2", "Version 1 only.", mutated(HANDOFF_INVITE, put(2, "schemaVersion"))),
    invalid("invite-not-canonical", "Invite codes are canonical.",
            mutated(HANDOFF_INVITE, put("ABCDE-12345", "target", "inviteCode"))),
]


def live_join(session_value, join_id="join-5e6f7a8b"):
    return {"schemaVersion": 1, "requestId": PROCESS, "joinId": join_id, "packageSetDigest":
            package_set_digest(PACKAGES), "session": session_value}


JOIN_LIVE_INVITE = live_join(session("join", serviceOrigin=ORIGIN, target=target("invite", inviteCode=INVITE)))
JOIN_LIVE_LAN = live_join(session("join", target=target("listing", source="lan")), "join-01")
JOIN_LIVE_PUNCH = live_join(session("join", serviceOrigin=ORIGIN, transport=transport("direct", consent=consent()),
                                    target=target("listing", source="internet")), "join-02")
join_valid = [
    valid("invite", "A live join of an invite, the game's packages already matching.", JOIN_LIVE_INVITE),
    valid("lan-listing", "A live join of a LAN listing.", JOIN_LIVE_LAN),
    valid("internet-direct-privacy", "A live join that may use direct paths, with its consent.", JOIN_LIVE_PUNCH),
]
join_invalid = [
    invalid("direct-target", "A live join never carries an address; a direct target needs a launch request.",
            live_join(session("join", target=target("direct", host=LAN_HOST, port=LAN_PORT)))),
    invalid("host-session", "A live join joins.", live_join(session("host", host=host_options()))),
    invalid("join-id-is-request", "A live join reports under its own id.",
            mutated(JOIN_LIVE_LAN, put(PROCESS, "joinId"))),
    invalid("digest-uppercase", "The package-set digest is lowercase hexadecimal.",
            mutated(JOIN_LIVE_LAN, put(package_set_digest(PACKAGES).upper(), "packageSetDigest"))),
    invalid("missing-session", "A live join carries its session.", mutated(JOIN_LIVE_LAN, drop("session"))),
    invalid("session-password", "Passwords are typed in game, never staged.",
            mutated(JOIN_LIVE_LAN, put("hunter2", "session", "password"))),
]


def preferences(**overrides):
    value = {"schemaVersion": 1, "revision": 0, "journalApplied": 0, "displayName": "Player",
             "privacy": {"mode": "relay-only", "disclosure": 0}, "voice": {"consent": 0, "enabled": False},
             "network": {"lan": True, "upnp": False},
             "hosting": {"name": "Player's server", "maxPlayers": 8, "visibility": "lan", "port": 27250,
                         "voice": False}}
    value.update(overrides)
    return value


PREFERENCES_DEFAULT = preferences()
PREFERENCES_SET = preferences(revision=7, journalApplied=3, displayName="Zoë 東京", serviceOrigin=ORIGIN,
                              privacy={"mode": "relay-only", "disclosure": 1}, voice={"consent": 1, "enabled": True},
                              network={"lan": False, "upnp": True},
                              hosting={"name": "Zoë's \"quiet\" server", "maxPlayers": 16, "visibility": "public",
                                       "port": 0, "voice": True})
preferences_valid = [
    valid("defaults", "A new player's preferences: relay-only, voice off without consent.", PREFERENCES_DEFAULT),
    valid("configured", "Every preference set, with the service configured.", PREFERENCES_SET),
]
preferences_invalid = [
    invalid("direct-default", "Direct play is never a stored default.",
            preferences(privacy={"mode": "direct", "disclosure": 1})),
    invalid("voice-without-consent", "Voice stays off until the player consents.",
            preferences(voice={"consent": 0, "enabled": True})),
    invalid("display-name-short", "Display names are 2 to 24 UTF-16 code units.", preferences(displayName="A")),
    invalid("display-name-noncharacter", "Display text never holds U+FFFE.", preferences(displayName="Player\ufffe")),
    invalid("hosting-name-noncharacter", "Display text never holds U+FFFF.",
            mutated(PREFERENCES_DEFAULT, put("Server \uffff", "hosting", "name"))),
    invalid("service-hex-address", "A hexadecimal number is an address, not a DNS name.",
            preferences(serviceOrigin="https://0x7f000001")),
    invalid("service-ip-literal", "The service origin is a DNS name.",
            preferences(serviceOrigin="https://203.0.113.5")),
    invalid("service-with-path", "The service origin has no path.", preferences(serviceOrigin=ORIGIN + "/")),
    invalid("favorites", "Favorites and history are never projected.", preferences(favorites=[])),
    invalid("hosting-port", "Ports run to 65535.",
            mutated(PREFERENCES_DEFAULT, put(65536, "hosting", "port"))),
    invalid("version-2", "Version 1 only.", preferences(schemaVersion=2)),
]


def journal(*changes):
    return {"schemaVersion": 1, "changes": list(changes)}


JOURNAL_EMPTY = journal()
JOURNAL_ALL = journal(
    {"sequence": 4, "displayName": "Sam"},
    {"sequence": 5, "disclosure": 1},
    {"sequence": 6, "voice": {"consent": 1, "enabled": True}},
    {"sequence": 8, "network": {"lan": True, "upnp": True}},
    {"sequence": 9, "hosting": {"name": "Sam's Sandbox", "maxPlayers": 4, "visibility": "lan", "port": 27260,
                                "voice": True}},
)
journal_valid = [
    valid("empty", "Nothing changed in game.", JOURNAL_EMPTY),
    valid("every-change", "One change of each kind, in sequence order.", JOURNAL_ALL),
]
journal_invalid = [
    invalid("sequence-repeated", "Sequences increase strictly.",
            journal({"sequence": 2, "displayName": "Sam"}, {"sequence": 2, "disclosure": 1})),
    invalid("sequence-zero", "Sequences start at 1.", journal({"sequence": 0, "displayName": "Sam"})),
    invalid("two-preferences", "A change sets one preference.",
            journal({"sequence": 1, "displayName": "Sam", "disclosure": 1})),
    invalid("no-preference", "A change sets one preference.", journal({"sequence": 1})),
    invalid("privacy-change", "Direct play is never stored, so privacy is not a change.",
            journal({"sequence": 1, "privacy": "direct"})),
    invalid("disclosure-zero", "An acknowledgement names version 1 or later.",
            journal({"sequence": 1, "disclosure": 0})),
    invalid("voice-without-consent", "Voice stays off until the player consents.",
            journal({"sequence": 1, "voice": {"consent": 0, "enabled": True}})),
    invalid("display-name-noncharacter", "Display text never holds U+FFFF.",
            journal({"sequence": 1, "displayName": "Sam\uffff"})),
    invalid("too-many", "The journal holds at most 64 changes.",
            journal(*({"sequence": index + 1, "disclosure": 1} for index in range(65)))),
]


def apply_journal(projection, changes):
    """The launcher's rule: every change after journalApplied, in order, as one new revision."""
    fresh = [change for change in changes if change["sequence"] > projection["journalApplied"]]
    if not fresh:
        return projection
    result = copy.deepcopy(projection)
    result["revision"] = projection["revision"] + 1
    for change in fresh:
        result["journalApplied"] = change["sequence"]
        if "displayName" in change:
            result["displayName"] = change["displayName"]
        elif "disclosure" in change:
            result["privacy"]["disclosure"] = max(result["privacy"]["disclosure"], change["disclosure"])
        elif "voice" in change:
            result["voice"] = dict(change["voice"])
        elif "network" in change:
            result["network"] = dict(change["network"])
        elif "hosting" in change:
            result["hosting"] = dict(change["hosting"])
    return result


ACKNOWLEDGED = preferences(revision=4, journalApplied=4, privacy={"mode": "relay-only", "disclosure": 2})
apply_cases = []
for case_id, about, base, changes in (
    ("all-from-defaults", "Every change applies, as one new revision.", PREFERENCES_DEFAULT, JOURNAL_ALL["changes"]),
    ("only-after-applied", "Changes the projection already covers are skipped, and an acknowledgement never lowers "
     "the disclosure.", ACKNOWLEDGED, JOURNAL_ALL["changes"]),
    ("nothing-new", "With nothing new, the projection stays as it is.",
     preferences(revision=2, journalApplied=9), JOURNAL_ALL["changes"]),
):
    apply_cases.append({"id": case_id, "about": about, "projection": sorted_json(base),
                        "journal": sorted_json(journal(*changes)), "result": sorted_json(apply_journal(base, changes))})

write("channels/cases.json", {
    "schema": 1,
    "about": "The session channels in manager staging: session-handoff (game to launcher), session-join (launcher "
             "to game), and the multiplayer-preferences-v1 projection (launcher to game) with its change journal "
             "(game to launcher). Valid cases read the same in any member order and write back to bytes exactly; "
             "invalid cases are refused. journalApply pins how the launcher applies a journal. None names an "
             "address. Generated by generate_launch.py.",
    "handoff": {"valid": handoff_valid, "invalid": handoff_invalid},
    "join": {"valid": join_valid, "invalid": join_invalid},
    "preferences": {"valid": preferences_valid, "invalid": preferences_invalid},
    "journal": {"valid": journal_valid, "invalid": journal_invalid},
    "journalApply": apply_cases,
})

# Only a launch request may hold a direct target's address. Nothing else written here does.
for folder in ("progress-v2", "channels"):
    text = (OUT / folder / "cases.json").read_text(encoding="utf-8")
    valid_text = json.dumps([case for section in json.loads(text).values() if isinstance(section, dict)
                             for case in section.get("valid", [])], ensure_ascii=False)
    for marker in DIRECT_MARKERS:
        assert marker not in valid_text, (folder, marker)
print("fixtures written to", OUT)
