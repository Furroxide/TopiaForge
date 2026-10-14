# Multiplayer live sessions — design record

Status: Implementing (WS2f of the live-multiplayer plan) · Date: 2026-10-13 · Owner: multiplayer

This records how live LAN and direct sessions are found, trusted and joined. The transport underneath is recorded in
`MultiplayerSecureTransport.md`, and its cryptography in `MultiplayerTransportCrypto.md`. The code lives in
`mods/TopiaForge.Multiplayer/Network/`, inside the provider only, and every type is internal.

WS2f lands in steps:

1. **Identity and formats** (sections 1 to 10): the identity, the documents and the wire formats that the game, the
   launcher and the control plane share, with fixtures all three replay.
2. **The network runtime** (sections 11 to 13): the network thread, the UDP socket, and the links the session engine
   sees.
3. **Services** (sections 14 to 16): the descriptor endpoint, its client, and LAN discovery.
4. **Hosting and joining** (sections 17 to 22): the host and join flows, admission, persistence and the developer
   switch that drives them until the in-game interface exists.

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
  Version 1 refuses unknown members; a later version changes the schema string. Each text rule covers the whole
  value, so a trailing newline fails it. .NET's `$` would let one through, so the C# patterns end in `\z`.
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
  - a lowercase DNS name of letters, digits and hyphens, where labels may start with a digit (RFC 1123) but a name of
    digits and dots only is refused, since it would read as a malformed address.
  An IPv4-mapped IPv6 address becomes the IPv4 address it maps.
- **Invite codes** are 10 characters of Crockford base32, 50 random bits. Reading them is forgiving: case, hyphens
  and spaces are ignored, O reads as 0, and I and L read as 1. The canonical code is upper case without separators,
  and people see it as `XXXXX-XXXXX`.
- **Case folding is ASCII only**, in invite codes and host names alike. Unicode folding would accept look-alikes: the
  long s (U+017F) uppercases to S, and the Kelvin sign (U+212A) lowercases to k. Both are refused.
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
from another server is reported as a changed identity for the player to confirm. The report names the server the
address serves now, and confirming trusts exactly that server there: any other server is refused again.

- The record lives in `multiplayer/known-hosts.json`. Each entry maps the SHA-256 of the normalized `host:port` to
  the server id, so the file never holds an address or a name.
- It keeps the 256 most recent targets. An unreadable file is treated as empty: losing it only costs a prompt.

## 9. Fixtures

`tests/fixtures/multiplayer/` holds the vectors every runtime replays: C# in the provider's tests, Dart in the
launcher, and TypeScript in the control plane.

| Folder | Cases |
| --- | --- |
| `descriptor/` | Two keys; valid descriptors that each runtime must sign to the exact served bytes; 33 tampered documents each runtime must refuse, trailing newlines included |
| `links/` | Valid links with their fields and canonical forms, invalid links (Unicode look-alikes included), and invite code normalization |
| `lan/` | Beacon bytes, session info, and invalid forms of each |
| `secrets/` | PBKDF2-HMAC-SHA256 vectors from RFC 7914, password proofs (including composed and decomposed Unicode), and player keys |
| `control-plane/` | HTTP exchanges and a signaling transcript for the control plane's clients ([ControlPlaneApi.md](ControlPlaneApi.md), section 11) |

An independent Python and OpenSSL implementation generated them, from fixed seeds, so they repeat exactly. Ed25519 is
deterministic, so signing a valid case's descriptor with its key must reproduce the served bytes. The `control-plane/`
files are the exception: the service's own tests record them, with a fixed clock and seeded randomness.

## 10. Tests

`NetworkDescriptorTests`, `NetworkLinkTests` and `NetworkIdentityTests` replay every fixture. They also cover:

- canonical JSON's refusals: whitespace, order, repeated keys, numbers, escapes, surrogates, depth, malformed UTF-8;
- descriptor bounds: 512 packages fit in 256 KiB and 513 are refused;
- RFC 5952 address forms;
- identity persistence, damage and rotation;
- the known-hosts record and its cap.

`NetworkStackTests` runs links over the virtual network: frames both ways with player keys and a shared handshake
hash, a stalled reader holding a fast sender to its budget and window (empty frames and closing links included), an
overloaded link closing, graceful closes that deliver everything (more than the budget included, and frames queued just
before the stack closes every link), a close that a link reports only after its last frame is read (under a real race
too), refused attempts and a full endpoint, and disposal. `NetworkSocketTests` uses real sockets on the loopback
interface: path classification and scoped addresses, the driver over IPv4 and IPv6, an oversized datagram, an
unreachable port, and two session engines joining across two network threads, a command round trip and a leave, all
in under five seconds. `NetworkServiceTests` covers the descriptor endpoint's answers, limits and failing archives, LAN
browsing (a moved server included), and services and connections the thread still owns when it stops. Every real
socket in the tests binds a loopback address, because binding every address makes Windows ask to let the test program
through its firewall; the socket classes take an optional bind address for this, and the game binds every address.

`LiveSessionTests` runs whole providers on the virtual network, with real cryptography, transport, engine and flows:
joining and leaving, passwords (proved, never sent, and limited), kicking, banning and stopping, every check before
connecting, reconnecting within the grace period, and the coordinator's recovery and saving. `LivePolicyTests` covers
join content, admission reports, the ban list, the order the host's policy checks in, and the known-hosts record.
`LiveSessionFailureTests` covers links lost before the first Ready, silent and repeated connections, joining players
and browsers refusing inbound links, the bounded event queue, saving while closing, unreadable side files, networks
that cannot open or give up, a host that never answers, a changed identity, and wiping the password key.
`NetworkPrivacyTests` checks that only the network layer names socket types, that no SDK member is shaped like an
address, that faults and failures name reasons rather than addresses, and that a relay-only resolve contacts nothing
beyond the local network.

## 11. The network thread

- **Ownership.** One thread owns a socket, a `NetworkStack` (the transport endpoint plus the links over its
  connections), the transport's cryptography and its timers. Live connections therefore keep their keepalives and
  retransmissions going while the main thread stalls, as it does through a scene load.
- **The loop.** Each turn runs up to 256 queued commands, reads up to 1,024 waiting datagrams (a dropped one counts),
  polls the stack, sends what it yields, and reports accepted links and info replies. The thread then sleeps until its
  next timer or a datagram, and at most 2 ms, so frames the main thread queues go out within that.
- **Talking to it.** The main thread posts commands, takes events and reads each link's own queues; nothing else is
  shared. At most 1,024 events wait. Past that, new ones are dropped and a link nobody would take is closed, so a
  main thread that never reads cannot make the queue grow. The thread's end is always told.
- **Faults.** A failed turn is reported by exception type only, never its message, because socket messages can name
  addresses. The loop carries on, and after 64 failures in a row it stops and closes every link.
- **Stopping.** `Stop` closes every link gracefully and waits up to a grace period for them to finish, then disposes
  the services, the stack, which wipes the transport's keys, and the socket. The deadline is fixed when `Stop` is
  called, not by a queued command, so the thread ends on time even if its queue never drains.
- **Ownership on the way out.** The thread owns a service from the moment it is attached and a connection from the
  moment it is asked for, so stopping disposes services that never ran and fails attempts whose command never ran,
  without running anything still queued. From `Stop` on, nothing new is taken on: a service attached then is disposed
  at once, and a connection fails at once. A thread stopped before it started releases everything immediately, and
  stopping or disposing it again is harmless.
- **Testing.** `NetworkStack` is sans-I/O like the transport, so the deterministic virtual network drives exactly the
  code the thread runs.

## 12. Links

A `NetworkLink` is a connection as the session engine sees it: `IEngineLink`, naming its peer by transport key and
never by address. Both directions are bounded queues between the two threads.

| Direction | Bound |
| --- | --- |
| Session frames in | The network thread moves frames out of the transport only while the main thread has less than 2 MiB waiting, counting 64 bytes a frame on top of its bytes so even empty frames fill it. A stalled main thread therefore holds the sender back through the transport's own flow control, instead of growing memory. |
| Realtime and voice in | The newest 128 frames; older ones are dropped, as the transport drops stale datagrams. |
| Out | The main thread may queue up to 32 MiB. Past that the link closes, since the transport's 16 MiB backlog cap would refuse it anyway. |

- **Closing.** The network thread reads a link's close request before draining its outbound queue, so a graceful close
  delivers everything the engine sent before asking. Stopping closes every link the same way, so the message that ends
  a player's session still goes out.
- **Draining.** A closing connection still takes the peer's data, so the budget holds until the connection drains.
  Then nothing more can arrive, and its remaining frames move to the link regardless of the budget.
- **Reporting the close.** The engine drains a link and then checks its state, and the network thread can deliver a
  last frame and close the link between the two. A link therefore reports `Closed` only once the engine has read every
  session frame delivered before the close.
- **What a link carries.** On a host, the player key derived for the peer (section 7). On both sides, the transport
  handshake hash, which password proofs bind to.
- **Connecting.** An outbound connection is a `ConnectAttempt` the main thread polls. Every attempt completes: it
  becomes established with its link, or fails with the transport's reason. Reasons include a key that does not match
  the pin, a timeout, relay-only refusing a direct path before anything is sent, and an endpoint already at its
  connection limit, which fails as overloaded.

## 13. The UDP socket

`UdpSocketDriver` is one dual-mode socket bound to every local address. It carries the workarounds the Mono spike
measured (`MultiplayerMonoSpike.md`, section 1):

- IPv4 destinations are sent as IPv4-mapped IPv6, because Mono fails to send to a plain IPv4 endpoint from a dual-mode
  socket.
- On Windows the connection-reset ioctl is turned off, so an ICMP port-unreachable cannot break the next receive. A
  host's fixed port is bound exclusively there, so no other process can share it.
- Every datagram is read whole into scratch space first, because Windows reports the whole queue as available and
  Linux truncates silently. One larger than a transport datagram is dropped.
- Transient socket errors lose that one datagram, as any network may, and recovery resends what mattered.
- Each read consumes at most one datagram. A dropped one yields no path, and the thread counts it against its turn,
  so a flood of junk cannot starve the timers.

Paths come from `UdpPaths`. A key is a type byte, the 16-byte IPv6 form of the address, the port and the IPv6 scope
id: 23 bytes that only this layer and the transport see. The scope keeps a link-local address on the interface it was
heard on. Private, link-local, unique-local and loopback addresses make LAN paths, which relay-only still allows; every
other address, carrier-grade NAT included, makes a direct path.

## 14. Services on the network thread

Work that belongs beside the stack runs on the network thread as an `INetworkService`: once per turn, after waiting
datagrams are read and before the stack is polled.

- **Attaching.** Services are attached and detached through the thread and disposed on it. Stopping the thread
  disposes every one, including any still waiting to start (section 11).
- **Info replies.** Each reply the stack receives is offered to the services first, so the LAN browser consumes the
  replies to its own queries. Any reply no service claims goes to the main thread as an event.
- **Timers.** A service may name its next timer. None may make the thread spin: TCP readiness is not part of the
  thread's wait, so a service with open TCP connections relies on the 2 ms turn.

## 15. The descriptor endpoint

A host serves its signed descriptor over TCP on the same port number as its game socket. The launcher's Dart client
speaks the same protocol.

Every line ends in a single newline (`\n`).

| Request (one per connection) | Answer |
| --- | --- |
| `TFD1 descriptor` | `TFD1 200 <length>`, then the signed descriptor's exact bytes |
| `TFD1 package <sha256>` | In developer mode, when the host has that archive: `TFD1 200 <length>` and the archive. Otherwise `TFD1 404`. |
| Anything else | `TFD1 400` |

- **Request rules.** Requests are printable ASCII ending in a newline, at most 128 bytes, and must arrive within five
  seconds of connecting.
- **Statuses.** `TFD1 404` means the host has no descriptor yet, or no such package. `TFD1 429` means the address
  passed ten requests a second. `TFD1 503` means eight connections are already open.
- **Writing.** Responses are written 64 KiB at a time, at most four chunks a connection per turn, so a fast reader
  never delays the datagrams. A transfer gets two minutes. Once the body is sent, the endpoint shuts down its side, so
  the reader sees the end.
- **Failing archives.** An archive that cannot be opened is answered `TFD1 404`. One that fails mid-read cuts only its
  own transfer short, so the reader's length check fails. Neither faults the network thread.
- **Relay-only hosts** serve LAN addresses only; any other connection is closed unanswered.
- **The client** (`DescriptorClient`) connects with a deadline, bounds the response to 256 KiB, and verifies the
  descriptor before returning it, against the server id it expects when it has one. Its failures name a reason:
  unreachable, timed out, refused or invalid. They never name an address.

## 16. LAN discovery

- **`LanAnnouncer`** sends the host's beacon every two seconds from its own IPv4 socket, so the game socket never
  needs broadcast rights. It goes to the limited broadcast address and to each up interface's directed broadcast,
  refreshed every 30 seconds.
- **`LanBrowser`** listens on UDP 27251 with address reuse. For each beacon it queries the host's game port, again
  after two seconds without info, and lists the session once the host's info names the server the beacon did.
  - A beacon that names another server at the same address never borrows that reply.
  - A beacon that moves a server to another socket drops what the old one answered, round trip included. The entry
    waits for a reply from the new socket that names the same server.
  - A host on another transport version is listed as incompatible.
  - A session silent for six seconds leaves the list.
  - At most 64 sessions are listed.
  - The main thread reads a snapshot that is replaced on every change. Each entry keeps its path inside the network
    layer, for the join flow.
- **Browsing is always relay-only.** LAN paths are allowed, so LAN hosts are queried as usual. A beacon from any
  other address is listed but never queried, so browsing never shows that host this player's address.

## 17. Live sessions

`LiveSessions` is the provider's one place for live play: at most one host or one join at a time, LAN browsing, and
the identity, bans and known hosts they share.

- **Threads.** Everything runs on the main thread through `Pump`, before the engine's own pump. Slow work runs on
  background tasks and lands at a later pump: loading the identity, bans and known hosts, and deriving a password key.
- **Roles.** Hosting opens the solo session to others; it needs a solo session and no join under way. Joining needs
  no host and the identity loaded (`PrepareAsync`). A joined session that ends returns the process to solo play
  through the engine's `AfterClientSessionEnds` hook (`MultiplayerSessionEngine.md`, "Role changes"), with a fresh
  solo session id.
- **Failures** are a `LiveFailure` and a sentence for the player. Neither ever names an address. A host that cannot
  start reports why (`PortInUse`, `IdentityUnavailable`, …) and may simply be started again.

## 18. Hosting

`LiveHost` turns the running solo session into a listen host. The engine keeps the same session throughout: hosting
adds links to it, and stopping closes them while the host plays on.

1. **Binding.** The game socket binds UDP 27250, or any free port when that is taken and the settings allow it. The
   descriptor endpoint then binds TCP on the same port number; if that number is taken, the pair is tried again, four
   times at most.
2. **Relay-only.** Unless the host allowed direct play, its transport is relay-only and its descriptor endpoint serves
   only LAN addresses, so nobody beyond the local network learns its address. Until the relay exists (WS7) such a
   host is reachable on the LAN only.
3. **Slots.** The transport holds twice as many connections as players, four at least. A link that has not asked to
   join within 10 seconds is closed, so peers that connect and say nothing cannot keep players out on keepalives
   alone. One address holds at most four links, enough for a household behind one router; paths whose source cannot
   be told apart, such as a relay's, are not capped.
4. **Serving.** The host signs its descriptor (packages from the runtime, `hostServesPackages` false) and serves it.
   It sets the engine's capacity and join policy, and keeps its session info current as players come and go. A host
   with LAN or public visibility also announces itself on the LAN; an invite-only host does not.
5. **Moderation.** A kick ends one player's session with the host's message. A ban records the player's transport key
   (section 19) and ends their session.
6. **Stopping** ends every player's session with `HostEnded`, gives the closing messages 250 ms to leave, and stops
   the network, and the password key is wiped. The host plays on alone in the same session. A network that gives up
   on its own (section 11) stops the host the same way, reported as `NetworkStopped`, and the player may host again.

## 19. Admission

A host admits players through `HostPolicy`, which checks, in order:

1. **Bans.** A banned transport key is refused first. Rotating the key escapes a ban, as the player-facing docs say.
2. **The engine's own checks:** the protocol's major version, capacity, and every shared contract's schema.
3. **The join content's form.** A player's join request carries version 1, a flag byte, a 32-byte password proof when
   the flag says so, and the runtime's content bytes with a minimal varint length. The engine bounds the whole field
   at 64 KiB, so the content is bounded at that less the largest header (37 bytes). Anything else is a protocol
   refusal.
4. **The password.** A protected session needs a proof bound to this link's handshake (section 6), so a proof made
   for another link is wrong here. Five wrong proofs from one identity within a minute refuse even the right one for
   the rest of that minute. The transport's per-source handshake limit bounds how fast new identities can try.
5. **Content.** The runtime's content admission decides last.

Every refusal carries an admission report, canonical JSON the joining player's interface reads:

```json
{"code":"content","problems":["Zombies 0.2.0 is missing."],"schema":"topiaforge.admission-report/1"}
```

The codes are `other`, `full`, `protocol`, `password-required`, `password-wrong`, `too-many-attempts`, `banned` and
`content`. Problems are display text, 64 at most. A report never names an address and never exceeds the reader's
16 KiB bound: problems are kept in order while they fit.

## 20. Joining

`LiveJoin` takes one join from target to session, on the main thread. Nothing leaves solo play until the link to the
host is up, so any failure before that leaves the player's own session untouched.

| Phase | What happens |
| --- | --- |
| Resolving | The target becomes a path and a verified descriptor, off the main thread. A LAN target comes from the browser. A typed address is looked up, IPv4 first, and trusted on first use (section 8). |
| Checking | The game build, the protocol's major version, the path against the player's privacy choice, and the content must all agree. |
| AwaitingPassword | A protected session waits for the password, which is typed in game, never staged. |
| PreparingProof | The password key is derived off the main thread, salted with the host's transport key. |
| Connecting | The transport connects with the descriptor's transport key pinned. Once the link is up, the engine switches to the host's session and sends the join with a proof bound to this link. |
| Synchronizing, InSession | The engine joins and plays. The host has two minutes to admit the player and send the session. A link lost before the first Ready ends the join, back in solo play. |
| Reconnecting | A link lost in session is retried every two seconds for 30 seconds, each time with a new proof for the new link; a retry whose link drops before Ready keeps the same window. The host keeps the player's place for its grace period, so the player comes back as the same participant. |
| Ended | The join failed or the session ended; the process is in solo play again. |

- **Relay-only is honoured before anything is sent.** Unless the player allowed direct play for this session, the
  resolver contacts only addresses on the local network, not even fetching a descriptor from anywhere else, and a
  host beyond it fails as `DirectNotAllowed`. It does not look names up either, because a name's own servers would
  learn something of the player from the query; only `localhost` names and mDNS `.local` names resolve. The join
  checks the resolved path again before connecting.
- **Nobody connects to a joining player.** Its endpoint refuses every Initiation, so no one else can reach it or
  learn its key, and it closes anything its network reports. LAN browsing works the same way.
- **How it ends** is told to the player: kicked, banned, the host ended it, a protocol mismatch, refused with the
  host's admission report, a host that never answered (a timeout, never mistaken for a failed proof), a network that
  could not open, or the connection lost after the reconnect window. The password key is wiped however the join
  ends, a key still being derived included.

## 21. Persistence

Each provider keeps its live-session files in its own data folder, read on the first use and saved as they change:

| File | Holds |
| --- | --- |
| `multiplayer/identity.json` | The identity (section 1) |
| `multiplayer/bans.json` | `topiaforge.bans/1`: each banned transport key with a display name and the time, at most 1,024, the oldest giving way; read up to 512 KiB, which a full list of the longest names fits |
| `multiplayer/known-hosts.json` | `topiaforge.known-hosts/1`: the hashes of typed addresses and the server each served (section 8), at most 256 |

- **Damage.** A file that does not read as its schema yields an empty record, and the next save replaces it. Entries
  that do not read are skipped and the rest kept. A ban or known-hosts file that cannot be read at all, locked or
  failing, is treated the same way, so it never keeps the player out of live play.
- **Order.** Each file's saves run one after another, so a slow save can never land after, and overwrite, a newer
  one. A ban starts saving at once.
- **Closing.** Saves are never cancelled. Closing starts any save still due and waits up to a second for the saves to
  land, so a ban made just before quitting survives.
- **Merging.** Known hosts read from disk are merged in as older than those this process recorded, in their own order,
  so the cap drops the oldest first.

## 22. The developer switch, and what comes later

The in-game interface for hosting and joining arrives with WS5. Until then a developer switch drives live sessions,
and it is off by default:

| Variable | Effect |
| --- | --- |
| `TOPIAFORGE_LIVE_MULTIPLAYER=1` | Turns live sessions on |
| `TOPIAFORGE_MULTIPLAYER_HOST=<name>` | Opens this session on the LAN under that name |
| `TOPIAFORGE_MULTIPLAYER_PORT=<port>` | The port to host on, 27250 by default |
| `TOPIAFORGE_MULTIPLAYER_JOIN=<host>:<port>` | Joins that host |
| `TOPIAFORGE_MULTIPLAYER_NAME=<name>` | The display name to join as |

Progress is logged by phase and reason, never by address. The switch never allows direct play, so a joining player
reaches LAN addresses only.

These choices differ from the plan:

- **Resume tokens** are replaced by identity-keyed resume. The host's engine already recognises a returning transport
  key within its grace period, and that key is proved by the handshake, so a token would add nothing.
- **Content** goes through `ILiveSessionContent`. Until WS3 supplies the runtime's content descriptors,
  `ProviderOnlyContent` stands in: no packages, nothing presented, everyone admitted, always ready.
- **Driving it.** The developer switch stands in for the interface until WS5.
