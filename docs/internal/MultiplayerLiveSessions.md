# Multiplayer live sessions — design record

Status: Implementing (WS2f of the live-multiplayer plan) · Date: 2026-10-13 · Owner: multiplayer

This records how live LAN and direct sessions are found, trusted and joined. The transport underneath is recorded in
`MultiplayerSecureTransport.md`, and its cryptography in `MultiplayerTransportCrypto.md`. The code lives in
`mods/TopiaForge.Multiplayer/Network/`, inside the provider only, and every type is internal.

WS2f lands in steps. This first one defines the identity, the documents and the wire formats that the game, the
launcher and the control plane share, with fixtures all three replay. The network thread, the sockets, and the host
and join flows follow.

## 1. Identity

Each install has one identity, in the provider's data folder at `multiplayer/identity.json`:

| Part | Use |
| --- | --- |
| X25519 static key | Proved in every transport handshake. Its public half, in lowercase hexadecimal, is the *fingerprint*: how hosts name this player, and what bans are keyed by. |
| Ed25519 signing key | Signs this install's server descriptors. Its SHA-256, in lowercase hexadecimal, is the *server id*. |
| Host secret, 32 random bytes | Derives the player keys this install gives others when it hosts (section 7). |

- There are no accounts, so the identity is the key. Rotating it creates a new identity, which also escapes any ban
  keyed by the old one; the player-facing docs say so.
- The file is canonical JSON (section 2) with schema `topiaforge.identity/1`. A file that cannot be read is kept as
  `multiplayer/identity.unreadable.json` beside a new identity, so it is never silently lost. The notice of that names
  no key.
- `LocalIdentity` hands out copies of its secrets only to their single consumer, the transport endpoint or the
  descriptor signer, and zeroes its own copies when disposed.

## 2. Canonical JSON

Every signed or shared document uses one canonical form, a subset of RFC 8785 that C#, Dart and TypeScript all
produce byte for byte. Each runtime also has an ordinary JSON writer that produces it: sorted keys, no whitespace.

- **Values.** Objects, arrays, strings, `true`, `false`, `null`, and integers within ±(2^53 − 1). There are no
  fractions or exponents, and `-0` is not allowed.
- **Keys** are 1 to 64 ASCII letters and digits, in ordinal order. That order is the same in every runtime.
- **Whitespace.** None.
- **Strings** escape only the quotation mark, the backslash, and U+0000 to U+001F: `\b \t \n \f \r` where they
  exist, otherwise `\u00xx` in lowercase. Everything else, U+007F and non-ASCII included, is written as UTF-8.
  Unpaired surrogates are refused.
- **Parsing.** A reader accepts a document only if writing what it parsed gives back the same bytes. A signature
  therefore never covers an ambiguous document. Depth is limited to 16, and every document type has a size bound.
- **Binary values.** Keys and signatures are unpadded base64url. The unused low bits of the last character must be
  zero, so every value has one spelling. Hashes and ids are lowercase hexadecimal.
- **Display text.** Names are 1 to N UTF-16 code units, with no surrounding whitespace, control characters or
  unpaired surrogates. Every runtime counts length in UTF-16 code units, so a limit means the same everywhere.

## 3. Server descriptor

A host describes its session in a descriptor and signs it. A joining player checks the descriptor before connecting.

```json
{"descriptor":{…},"signature":"<base64url Ed25519 signature>"}
```

| Member | Value |
| --- | --- |
| `schema` | `topiaforge.server-descriptor/1` |
| `serverId` | SHA-256 of `signingKey`, hexadecimal. It must match. |
| `signingKey` | The host's Ed25519 public key |
| `transportKey` | The host's X25519 static key. The transport handshake must present it. |
| `issuedAt` | Signing time in Unix seconds |
| `name` | Server name, 1 to 48 characters |
| `gameBuild` | Robotopia build, 1 to 16 of `[0-9A-Za-z._-]` |
| `runtimeVersion`, `protocolVersion` | Semantic versions of the TopiaForge runtime and the multiplayer protocol |
| `maxPlayers` | 2 to 16 |
| `passwordRequired`, `voice` | Session settings shown before joining |
| `visibility` | `public`, `invite` or `lan` |
| `hostServesPackages` | Developer mode: the host serves its own package archives to joining players |
| `gamemode`, `world` | Display names, 1 to 48 characters, or `null` |
| `packages` | Each package: `id`, `version`, `sha256` and `role` (`session-required`, `session-optional` or `implicit`). Sorted by id, each once, at most 512. Mods marked `client-local` or `server-only` are left out. |
| `packageDigest` | SHA-256 of the canonical `packages` array. It must match. |

- **Signing.** The signature is Ed25519 over the 32-byte ASCII label `TopiaForge server descriptor v1\n` followed by
  the descriptor's canonical bytes. The label keeps the signature from passing as one over anything else.
- **Checks.** A descriptor is accepted when:
  - it is canonical JSON of at most 256 KiB;
  - it has exactly the version 1 members, all valid;
  - its server id and package digest match;
  - its signature verifies under its own signing key;
  - and, when the caller expects a particular server, its server id is that one.
  Version 1 refuses unknown members; a later version changes the schema string.
- **Pins.** The descriptor's hash, the SHA-256 of the exact served bytes, and the package digest are what a join plan
  pins.
- **Trust chain.** A link, a listing or a LAN beacon names a server id. Only a descriptor that verifies against that
  id can supply the transport key, and the handshake then refuses any other key. A hand-typed address names no
  server, so it falls back to trust on first use (section 8).
- **Privacy.** A descriptor never holds a network address.

## 4. Session links and invite codes

| Link | Meaning |
| --- | --- |
| `topiaforge://join/<code>` | An invite, resolved through the control plane |
| `topiaforge://join/server/<server id>` | A listed server, found through the control plane |
| `topiaforge://join/direct/<server id>/<host>/<port>` | A direct address, pinned by the server id |

- **Grammar.** No query, fragment or percent-encoding, and at most 512 characters. Only the scheme ignores case.
- **Hosts.** A host is one of three things:
  - an IPv4 address in dotted decimal;
  - an IPv6 address in RFC 5952 form, without brackets since it fills a whole path segment;
  - a lowercase DNS name.
  An IPv4-mapped IPv6 address becomes the IPv4 address it maps.
- **Invite codes** are 10 characters of Crockford base32, 50 random bits. Reading them is forgiving: case, hyphens
  and spaces are ignored, O reads as 0, and I and L read as 1. The canonical code is upper case without separators,
  and people see it as `XXXXX-XXXXX`.
- **Privacy.** A direct link carries the host's address, so it exists only in the network layer and the launcher.
  It never reaches mods, snapshots, logs or diagnostics. A link never joins on its own: it opens the consent flow.

## 5. LAN discovery

- **Beacons.** A host with LAN visibility (public or LAN) sends a beacon every two seconds, from its game socket, as
  an IPv4 broadcast to UDP 27251. The beacon is 36 bytes: `E6`, version `1`, the 32-byte server id, and the game port
  as two bytes, little-endian. IPv6 multicast discovery is later work.
- **Browsing.** A browser listens on 27251, with address reuse so the game and the launcher can both listen. When it
  hears a beacon, it sends the transport's info query (`MultiplayerSecureTransport.md`, section 12) to the sender's
  address at the game port.
- **Session info.** The info reply carries canonical JSON of at most 1,188 bytes, schema `topiaforge.session-info/1`:
  - the server id and the hash of the signed descriptor;
  - the name, game build, runtime and protocol versions;
  - the gamemode and world display names;
  - players and maximum players;
  - whether a password is required and whether voice is on.

  The widest valid info measures 935 bytes.
- **Trust.** Beacons and info are unauthenticated hints, and they never carry an address. Before trusting anything
  in them, a browser fetches the descriptor they name and checks it against the beacon's server id.
- **Descriptor endpoint.** A host serves its signed descriptor over TCP on the same port number as its game socket,
  27250 by default. The plan names TCP 27250 outright, but `topiaforge dev --peers` hosts on automatic ports, so the
  endpoint follows the game port.

## 6. Password proof

A password-protected host never receives the password. A joining player sends
`HMAC-SHA256(PBKDF2-HMAC-SHA256(password, hostTransportKey, 100,000 rounds, 32 bytes), handshakeHash)`. The password
is NFC-normalized and encoded as UTF-8.

- The host's transport key is the salt, so a proof made for one host is worthless at another. The handshake hash binds
  the proof to one transport session, so it cannot be replayed, and it travels inside that session's encryption.
- An impostor without the host's key cannot complete a pinned handshake. An unpinned first contact could reach one, so
  the 100,000 rounds make guessing a captured proof offline expensive.
- Passwords are 1 to 64 characters without control characters. They are typed in game and never stored or staged.
- PBKDF2 is written out in the provider, because Mono's class library may lack the SHA-256 form. It runs off the main
  thread, and a host derives its key once when it starts hosting.

## 7. Player keys

A host gives each identity the player key `base64url(first 16 bytes of HMAC-SHA256(hostSecret, "TopiaForge player key
v1" || clientTransportKey))`. It is stable for one player at one host and unlinkable across hosts, since each host
keeps its own secret. Mods see it as `MultiplayerParticipant.PlayerKey`.

## 8. Trust on first use

A hand-typed address carries no server id. The first descriptor fetched from it is recorded, and a later descriptor
from another server is reported as a changed identity for the player to confirm.

- The record lives in `multiplayer/known-hosts.json`. Each entry maps the SHA-256 of the normalized `host:port` to
  the server id, so the file never holds an address or a name.
- It keeps the 256 most recent targets. An unreadable file is treated as empty: losing it only costs a prompt.

## 9. Fixtures

`tests/fixtures/multiplayer/` holds the vectors every runtime replays: C# in the provider's tests, Dart in the
launcher, and TypeScript in the control plane.

| Folder | Cases |
| --- | --- |
| `descriptor/` | Two keys; valid descriptors that each runtime must sign to the exact served bytes; 30 tampered documents each runtime must refuse |
| `links/` | Valid links with their fields and canonical forms, invalid links, and invite code normalization |
| `lan/` | Beacon bytes, session info, and invalid forms of each |
| `secrets/` | PBKDF2-HMAC-SHA256 vectors from RFC 7914, password proofs (including composed and decomposed Unicode), and player keys |

An independent Python and OpenSSL implementation generated them, from fixed seeds, so they repeat exactly. Ed25519 is
deterministic, so signing a valid case's descriptor with its key must reproduce the served bytes.

## 10. Tests

`NetworkDescriptorTests`, `NetworkLinkTests` and `NetworkIdentityTests` replay every fixture. They also cover:

- canonical JSON's refusals: whitespace, order, repeated keys, numbers, escapes, surrogates, depth, malformed UTF-8;
- descriptor bounds: 512 packages fit in 256 KiB and 513 are refused;
- RFC 5952 address forms;
- identity persistence, damage and rotation;
- the known-hosts record and its cap.
