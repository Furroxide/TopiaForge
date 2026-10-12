"""Generate tests/fixtures/multiplayer/* from first principles, independently of the C# code.

Canonical JSON comes from Python's json module (sorted keys, no whitespace, UTF-8), Ed25519 and X25519 from the
OpenSSL 3 command line, and hashes from hashlib. Every key is derived from a fixed seed, so a rerun reproduces every
byte; a diff after rerunning means a format changed.

Run from the repository root: python -I tests/fixtures/multiplayer/generate.py .
"""
import base64
import hashlib
import hmac
import json
import pathlib
import subprocess
import sys
import tempfile
import unicodedata

ROOT = pathlib.Path(sys.argv[1])
OUT = ROOT / "tests" / "fixtures" / "multiplayer"
CONTEXT = b"TopiaForge server descriptor v1\n"
OPENSSL = "openssl"


def canonical(value) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def b64url(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).decode("ascii").rstrip("=")


def seed(label: str) -> bytes:
    return hashlib.sha256(("topiaforge fixture " + label).encode("utf-8")).digest()


def openssl(*args, data: bytes = b"") -> bytes:
    return subprocess.run([OPENSSL, *args], input=data, capture_output=True, check=True).stdout


def ed25519_public(secret: bytes) -> bytes:
    with tempfile.TemporaryDirectory() as folder:
        key = pathlib.Path(folder) / "key.der"
        key.write_bytes(bytes.fromhex("302e020100300506032b657004220420") + secret)
        der = openssl("pkey", "-inform", "DER", "-in", str(key), "-pubout", "-outform", "DER")
    assert der[:12] == bytes.fromhex("302a300506032b6570032100")
    return der[12:]


def ed25519_sign(secret: bytes, message: bytes) -> bytes:
    with tempfile.TemporaryDirectory() as folder:
        key = pathlib.Path(folder) / "key.der"
        key.write_bytes(bytes.fromhex("302e020100300506032b657004220420") + secret)
        payload = pathlib.Path(folder) / "message.bin"
        payload.write_bytes(message)
        signature = openssl("pkeyutl", "-sign", "-inkey", str(key), "-keyform", "DER", "-rawin", "-in", str(payload))
    assert len(signature) == 64
    return signature


def x25519_public(private: bytes) -> bytes:
    with tempfile.TemporaryDirectory() as folder:
        key = pathlib.Path(folder) / "key.der"
        key.write_bytes(bytes.fromhex("302e020100300506032b656e04220420") + private)
        der = openssl("pkey", "-inform", "DER", "-in", str(key), "-pubout", "-outform", "DER")
    assert der[:12] == bytes.fromhex("302a300506032b656e032100")
    return der[12:]


def write(name: str, value) -> None:
    path = OUT / name
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")


# ---------------------------------------------------------------- keys

KEYS = {}
for label in ("host-a", "host-b"):
    signing_seed = seed(label + " signing")
    transport_private = seed(label + " transport")
    signing_key = ed25519_public(signing_seed)
    transport_key = x25519_public(transport_private)
    KEYS[label] = {
        "id": label,
        "signingSeed": signing_seed.hex(),
        "signingKey": b64url(signing_key),
        "transportPrivateKey": transport_private.hex(),
        "transportKey": b64url(transport_key),
        "serverId": hashlib.sha256(signing_key).hexdigest(),
    }


def package(mod_id, version, digest_label, role):
    return {"id": mod_id, "version": version, "sha256": hashlib.sha256(digest_label.encode()).hexdigest(), "role": role}


def descriptor(key_label, **overrides):
    key = KEYS[key_label]
    packages = overrides.pop("packages", [
        package("io.github.furroxide.topiaforge.multiplayer", "0.2.0", "multiplayer archive", "session-required"),
        package("io.github.furroxide.topiaforge.sandbox", "0.2.0", "sandbox archive", "implicit"),
        package("io.github.furroxide.topiaforge.zombies", "0.2.0-rc.1+build.7", "zombies archive", "session-optional"),
    ])
    value = {
        "schema": "topiaforge.server-descriptor/1",
        "serverId": key["serverId"],
        "signingKey": key["signingKey"],
        "transportKey": key["transportKey"],
        "issuedAt": 1791849600,
        "name": "Sam's Sandbox",
        "gameBuild": "2545",
        "runtimeVersion": "0.2.0",
        "protocolVersion": "1.0.0",
        "maxPlayers": 8,
        "passwordRequired": False,
        "voice": True,
        "visibility": "public",
        "hostServesPackages": False,
        "gamemode": "Open Sandbox",
        "world": "Test City",
        "packages": packages,
    }
    value["packageDigest"] = hashlib.sha256(canonical(packages)).hexdigest()
    value.update(overrides)
    return value


def sign(key_label, value, context=CONTEXT):
    signature = ed25519_sign(bytes.fromhex(KEYS[key_label]["signingSeed"]), context + canonical(value))
    return {"descriptor": value, "signature": b64url(signature)}


def valid_case(case_id, key_label, value, about):
    envelope = sign(key_label, value)
    signed = canonical(envelope)
    return {
        "id": case_id,
        "about": about,
        "key": key_label,
        "descriptor": value,
        "canonicalDescriptor": canonical(value).decode("utf-8"),
        "signature": envelope["signature"],
        "signedBytes": signed.decode("utf-8"),
        "packageDigest": value["packageDigest"],
        "descriptorSha256": hashlib.sha256(signed).hexdigest(),
        "serverId": KEYS[key_label]["serverId"],
    }


valid = [
    valid_case("public-sandbox", "host-a", descriptor("host-a"), "A public session with three packages, one of each role."),
    valid_case("invite-without-target", "host-b", descriptor(
        "host-b", name="Zoë's \"quiet\" server \\ 東京", visibility="invite", passwordRequired=True, voice=False,
        gamemode=None, world=None, maxPlayers=16, hostServesPackages=True, issuedAt=0),
        "Escapes, non-ASCII text, null targets, the largest session and a host that serves packages."),
    valid_case("no-packages", "host-a", descriptor("host-a", packages=[], visibility="lan", maxPlayers=2, name="LAN"),
               "An empty package list still has a digest: the SHA-256 of []."),
]

base = descriptor("host-a")
good = canonical(sign("host-a", base)).decode("utf-8")


def tampered(case_id, about, text=None, hex_bytes=None, expected=None):
    case = {"id": case_id, "about": about, "expectedServerId": expected}
    if hex_bytes is not None:
        case["bytesHex"] = hex_bytes
    else:
        case["bytes"] = text
    return case


def signed_text(value, key_label="host-a", context=CONTEXT):
    return canonical(sign(key_label, value, context)).decode("utf-8")


flipped = sign("host-a", base)
raw_signature = bytearray(base64.urlsafe_b64decode(flipped["signature"] + "=="))
raw_signature[10] ^= 0x01
flipped["signature"] = b64url(bytes(raw_signature))
noncanonical_key = KEYS["host-a"]["signingKey"]
last = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_".index(noncanonical_key[-1])
noncanonical_key = noncanonical_key[:-1] + "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"[last | 1]
unsorted = descriptor("host-a")
unsorted["packages"] = list(reversed(unsorted["packages"]))
unsorted["packageDigest"] = hashlib.sha256(canonical(unsorted["packages"])).hexdigest()
duplicate = descriptor("host-a")
duplicate["packages"] = [duplicate["packages"][0], duplicate["packages"][0]]
duplicate["packageDigest"] = hashlib.sha256(canonical(duplicate["packages"])).hexdigest()

invalid = [
    tampered("whitespace", "Canonical form has no whitespace.", good.replace(",", ", ", 1)),
    tampered("escaped-slash", "A slash is never escaped.", signed_text(descriptor("host-a", name="a/b")).replace("a/b", "a\\/b")),
    tampered("uppercase-unicode-escape", "Escapes are lowercase and only for control characters.", good.replace("Sam", "\\u0043harl")),
    tampered("members-out-of-order", "Members are in ordinal key order.",
             '{"signature":"' + sign("host-a", base)["signature"] + '","descriptor":' + canonical(base).decode("utf-8") + "}"),
    tampered("unknown-member", "Version 1 refuses members it does not know.", signed_text(dict(base, motd="hi"))),
    tampered("missing-member", "Every member is required; absent values are null.", signed_text({k: v for k, v in base.items() if k != "world"})),
    tampered("wrong-schema", "The schema names version 1.", signed_text(dict(base, schema="topiaforge.server-descriptor/2"))),
    tampered("server-id-mismatch", "The server id is the SHA-256 of the signing key.", signed_text(dict(base, serverId=KEYS["host-b"]["serverId"]))),
    tampered("package-digest-mismatch", "The package digest covers the canonical package list.", signed_text(dict(base, packageDigest="0" * 64))),
    tampered("packages-unsorted", "Packages are sorted by id.", signed_text(unsorted)),
    tampered("packages-duplicated", "Each package appears once.", signed_text(duplicate)),
    tampered("signature-flipped", "One flipped signature bit fails verification.", canonical(flipped).decode("utf-8")),
    tampered("signature-without-context", "The signature covers the context label too.", signed_text(base, context=b"")),
    tampered("signed-by-other-key", "Host B cannot sign host A's descriptor.", signed_text(base, key_label="host-b")),
    tampered("other-server-expected", "A valid descriptor for another server than the one expected.", good, expected=KEYS["host-b"]["serverId"]),
    tampered("too-few-players", "Sessions hold 2 to 16 players.", signed_text(dict(base, maxPlayers=1))),
    tampered("too-many-players", "Sessions hold 2 to 16 players.", signed_text(dict(base, maxPlayers=17))),
    tampered("name-control-character", "Display text has no control characters.", signed_text(dict(base, name="bad\u0007name"))),
    tampered("name-padded", "Display text has no surrounding whitespace.", signed_text(dict(base, name=" padded"))),
    tampered("name-too-long", "A name is at most 48 UTF-16 code units.", signed_text(dict(base, name="x" * 49))),
    tampered("noncanonical-base64", "Unused base64 bits must be zero.", signed_text(dict(base, signingKey=noncanonical_key))),
    tampered("float-number", "Only integers are allowed.", good.replace('"issuedAt":1791849600', '"issuedAt":1791849600.0')),
    tampered("negative-zero", "Negative zero is not canonical.", signed_text(dict(base, issuedAt=0)).replace('"issuedAt":0', '"issuedAt":-0')),
    tampered("lone-surrogate", "Strings hold no unpaired surrogates.", good.replace("Sam", "\\ud800Charl")),
    tampered("duplicate-key", "A key appears once.", good.replace('{"descriptor":', '{"descriptor":{},"descriptor":', 1)),
    tampered("invalid-utf8", "Documents are UTF-8.", hex_bytes=good.encode("utf-8").replace(b"Sam", bytes([0x43, 0xFF, 0x61, 0x72, 0x6C]), 1).hex()),
    tampered("bad-visibility", "Visibility is public, invite or lan.", signed_text(dict(base, visibility="friends"))),
    tampered("bad-role", "Roles are session-required, session-optional or implicit.", signed_text(dict(base, packages=[dict(base["packages"][0], role="required")],
             packageDigest=hashlib.sha256(canonical([dict(base["packages"][0], role="required")])).hexdigest()))),
    tampered("uppercase-package-digest", "Hexadecimal is lowercase.", signed_text(dict(base, packageDigest=base["packageDigest"].upper()))),
    tampered("bad-semver", "Versions are semantic versions.", signed_text(dict(base, runtimeVersion="0.2"))),
]

write("descriptor/cases.json", {
    "schema": 1,
    "about": "Signed server descriptors (topiaforge.server-descriptor/1). Every runtime that signs or checks one replays "
             "these: C# (provider), Dart (launcher) and TypeScript (control plane). Generated by an independent Python "
             "and OpenSSL implementation; Ed25519 signatures are deterministic, so signing a valid case's descriptor "
             "with its key must reproduce signedBytes exactly.",
    "context": CONTEXT.decode("ascii"),
    "keys": list(KEYS.values()),
    "valid": valid,
    "invalid": invalid,
})

# ---------------------------------------------------------------- links

SERVER = KEYS["host-a"]["serverId"]
links_valid = [
    {"input": "topiaforge://join/ABCDE12345", "kind": "invite", "inviteCode": "ABCDE12345", "canonical": "topiaforge://join/ABCDE12345"},
    {"input": "TopiaForge://join/abcde-12345", "kind": "invite", "inviteCode": "ABCDE12345", "canonical": "topiaforge://join/ABCDE12345"},
    {"input": "topiaforge://join/oilOIL0123", "kind": "invite", "inviteCode": "0110110123", "canonical": "topiaforge://join/0110110123"},
    {"input": "topiaforge://join/server/" + SERVER, "kind": "server", "serverId": SERVER, "canonical": "topiaforge://join/server/" + SERVER},
    {"input": "topiaforge://join/direct/" + SERVER + "/192.168.1.20/27250", "kind": "direct", "serverId": SERVER,
     "host": "192.168.1.20", "port": 27250, "canonical": "topiaforge://join/direct/" + SERVER + "/192.168.1.20/27250"},
    {"input": "topiaforge://join/direct/" + SERVER + "/2001:DB8:0:0:0:0:0:1/27250", "kind": "direct", "serverId": SERVER,
     "host": "2001:db8::1", "port": 27250, "canonical": "topiaforge://join/direct/" + SERVER + "/2001:db8::1/27250"},
    {"input": "topiaforge://join/direct/" + SERVER + "/2001:db8:0:0:1:0:0:1/1", "kind": "direct", "serverId": SERVER,
     "host": "2001:db8::1:0:0:1", "port": 1, "canonical": "topiaforge://join/direct/" + SERVER + "/2001:db8::1:0:0:1/1"},
    {"input": "topiaforge://join/direct/" + SERVER + "/::ffff:10.0.0.7/65535", "kind": "direct", "serverId": SERVER,
     "host": "10.0.0.7", "port": 65535, "canonical": "topiaforge://join/direct/" + SERVER + "/10.0.0.7/65535"},
    {"input": "topiaforge://join/direct/" + SERVER + "/Play.Example.ORG/27250", "kind": "direct", "serverId": SERVER,
     "host": "play.example.org", "port": 27250, "canonical": "topiaforge://join/direct/" + SERVER + "/play.example.org/27250"},
    {"input": "topiaforge://join/direct/" + SERVER + "/::1/27250", "kind": "direct", "serverId": SERVER,
     "host": "::1", "port": 27250, "canonical": "topiaforge://join/direct/" + SERVER + "/::1/27250"},
]
links_invalid = [
    {"input": "topiaforge://join/ABCDE12345?x=1", "about": "No query strings."},
    {"input": "topiaforge://join/ABCDE12345#top", "about": "No fragments."},
    {"input": "topiaforge://join/ABCDE%2012345", "about": "No percent-encoding."},
    {"input": "topiaforge://join/ABCDE12345/", "about": "No trailing slash."},
    {"input": "topiaforge://JOIN/ABCDE12345", "about": "Only the scheme ignores case."},
    {"input": "https://join/ABCDE12345", "about": "Another scheme."},
    {"input": "topiaforge://join/ABCDEU2345", "about": "U is not in Crockford base32."},
    {"input": "topiaforge://join/ABCDE1234", "about": "Nine characters."},
    {"input": "topiaforge://join/ABCDE123456", "about": "Eleven characters."},
    {"input": "topiaforge://join/server/" + SERVER.upper(), "about": "Server ids are lowercase."},
    {"input": "topiaforge://join/server/" + SERVER[:-1], "about": "Server ids are 64 characters."},
    {"input": "topiaforge://join/direct/" + SERVER + "/192.168.1.20/0", "about": "Port 0."},
    {"input": "topiaforge://join/direct/" + SERVER + "/192.168.1.20/65536", "about": "Port above 65535."},
    {"input": "topiaforge://join/direct/" + SERVER + "/192.168.1.20/080", "about": "Ports have no leading zero."},
    {"input": "topiaforge://join/direct/" + SERVER + "/192.168.1.20", "about": "A direct link needs a port."},
    {"input": "topiaforge://join/direct/" + SERVER + "/999.1.1.1/27250", "about": "Not an address, and digits cannot end a name."},
    {"input": "topiaforge://join/direct/" + SERVER + "/192.168.001.20/27250", "about": "IPv4 parts have no leading zero."},
    {"input": "topiaforge://join/direct/" + SERVER + "/[::1]/27250", "about": "IPv6 is written without brackets."},
    {"input": "topiaforge://join/direct/" + SERVER + "/bad_host.example/27250", "about": "Host names are letters, digits and hyphens."},
    {"input": "topiaforge://join/direct/" + SERVER + "/-bad.example/27250", "about": "Labels do not start with a hyphen."},
    {"input": "topiaforge://join/direct/" + SERVER + "/example.com./27250", "about": "No trailing dot."},
    {"input": "topiaforge://join/ ABCDE12345", "about": "No spaces inside a link."},
    {"input": "topiaforge://join/" + "A" * 600, "about": "At most 512 characters."},
    {"input": "topiaforge://join/", "about": "Nothing to join."},
]
codes = [
    {"input": "abcde-12345", "canonical": "ABCDE12345", "display": "ABCDE-12345"},
    {"input": "ABCDE 12345", "canonical": "ABCDE12345", "display": "ABCDE-12345"},
    {"input": "0o1iL-zzzzz", "canonical": "00111ZZZZZ", "display": "00111-ZZZZZ"},
    {"input": "ABCDEU2345", "canonical": None},
    {"input": "ABCD", "canonical": None},
    {"input": "ABCDE_12345", "canonical": None},
]
write("links/cases.json", {
    "schema": 1,
    "about": "topiaforge://join links and invite codes, shared by the game, the launcher and the control plane. Valid "
             "inputs parse to the listed fields and format back to the canonical link; invalid inputs are refused.",
    "alphabet": "0123456789ABCDEFGHJKMNPQRSTVWXYZ",
    "valid": links_valid,
    "invalid": links_invalid,
    "inviteCodes": codes,
})

# ---------------------------------------------------------------- LAN

beacon_cases = []
for server_label, port in (("host-a", 27250), ("host-b", 50123)):
    sid = KEYS[server_label]["serverId"]
    datagram = bytes([0xE6, 1]) + bytes.fromhex(sid) + port.to_bytes(2, "little")
    beacon_cases.append({"serverId": sid, "gamePort": port, "hex": datagram.hex()})
good_beacon = bytes.fromhex(beacon_cases[0]["hex"])
beacon_invalid = [
    {"hex": good_beacon[:-1].hex(), "about": "One byte short."},
    {"hex": (good_beacon + b"\x00").hex(), "about": "One byte long."},
    {"hex": (b"\xE5" + good_beacon[1:]).hex(), "about": "Another packet type."},
    {"hex": (good_beacon[:1] + b"\x02" + good_beacon[2:]).hex(), "about": "Another beacon version."},
    {"hex": (good_beacon[:-2] + b"\x00\x00").hex(), "about": "Port zero."},
]


def info(**overrides):
    value = {
        "schema": "topiaforge.session-info/1",
        "serverId": KEYS["host-a"]["serverId"],
        "descriptorSha256": valid[0]["descriptorSha256"],
        "name": "Sam's Sandbox",
        "gameBuild": "2545",
        "runtimeVersion": "0.2.0",
        "protocolVersion": "1.0.0",
        "gamemode": "Open Sandbox",
        "world": "Test City",
        "players": 3,
        "maxPlayers": 8,
        "passwordRequired": False,
        "voice": True,
    }
    value.update(overrides)
    return value


widest = info(name="東" * 48, gamemode="東" * 48, world="東" * 48, runtimeVersion="1.0.0-" + "a" * 58,
              protocolVersion="1.0.0-" + "b" * 58, gameBuild="x" * 16, players=16, maxPlayers=16, passwordRequired=True)
info_valid = [
    {"id": "typical", "info": info(), "canonical": canonical(info()).decode("utf-8")},
    {"id": "no-target", "info": info(gamemode=None, world=None, players=0), "canonical": canonical(info(gamemode=None, world=None, players=0)).decode("utf-8")},
    {"id": "widest", "info": widest, "canonical": canonical(widest).decode("utf-8"), "bytes": len(canonical(widest))},
]
assert len(canonical(widest)) <= 1188, len(canonical(widest))
info_invalid = [
    {"text": canonical(info(players=9)).decode("utf-8"), "about": "More players than the maximum."},
    {"text": canonical(dict(info(), host="192.168.1.20")).decode("utf-8"), "about": "No unknown members, and never an address."},
    {"text": canonical(info(schema="topiaforge.session-info/2")).decode("utf-8"), "about": "Another schema."},
    {"text": json.dumps(info(), sort_keys=True, ensure_ascii=False), "about": "Not canonical."},
    {"text": canonical(info(descriptorSha256="A" * 64)).decode("utf-8"), "about": "Hexadecimal is lowercase."},
]
write("lan/cases.json", {
    "schema": 1,
    "about": "LAN beacons (UDP 27251) and the session info a host returns to an info query. Beacon bytes are hex; "
             "session info is canonical JSON of at most 1,188 bytes.",
    "beacons": {"valid": beacon_cases, "invalid": beacon_invalid},
    "sessionInfo": {"maximumBytes": 1188, "valid": info_valid, "invalid": info_invalid},
})

# ---------------------------------------------------------------- secrets

host_key = base64.urlsafe_b64decode(KEYS["host-a"]["transportKey"] + "=")
handshake_hash = hashlib.sha256(b"fixture handshake hash").digest()


def proof_case(case_id, password):
    key = hashlib.pbkdf2_hmac("sha256", unicodedata.normalize("NFC", password).encode("utf-8"), host_key, 100_000, 32)
    return {"id": case_id, "password": password, "key": key.hex(), "proof": hmac.new(key, handshake_hash, "sha256").hexdigest()}


composed = proof_case("composed", "Café au lait")
decomposed = proof_case("decomposed", "Café au lait")
assert composed["proof"] == decomposed["proof"]
host_secret = seed("host-a host secret")
players = []
for label in ("host-a", "host-b"):
    client_key = base64.urlsafe_b64decode(KEYS[label]["transportKey"] + "=")
    mac = hmac.new(host_secret, b"TopiaForge player key v1" + client_key, "sha256").digest()
    players.append({"clientTransportKey": KEYS[label]["transportKey"], "playerKey": b64url(mac[:16])})
write("secrets/cases.json", {
    "schema": 1,
    "about": "Password proofs (PBKDF2-HMAC-SHA256 over the NFC password, salted with the host transport key, 100,000 "
             "rounds, then HMAC-SHA256 over the handshake hash) and per-host player keys. Only the game computes these; "
             "the vectors pin the construction.",
    "pbkdf2": [
        {"password": "passwd", "salt": "salt", "iterations": 1, "length": 64,
         "key": hashlib.pbkdf2_hmac("sha256", b"passwd", b"salt", 1, 64).hex(), "source": "RFC 7914 section 11"},
        {"password": "Password", "salt": "NaCl", "iterations": 80000, "length": 64,
         "key": hashlib.pbkdf2_hmac("sha256", b"Password", b"NaCl", 80000, 64).hex(), "source": "RFC 7914 section 11"},
    ],
    "passwordProof": {
        "hostTransportKey": KEYS["host-a"]["transportKey"],
        "handshakeHash": handshake_hash.hex(),
        "cases": [proof_case("ascii", "correct horse battery staple"), composed, decomposed],
    },
    "playerKeys": {"hostSecret": host_secret.hex(), "cases": players},
})
print("fixtures written to", OUT)
