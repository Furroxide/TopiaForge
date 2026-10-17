# Multiplayer NAT traversal — design record

Status: Implementing (WS7 of the live-multiplayer plan, game-side connectivity) · Date: 2026-10-17 · Owner: multiplayer

This records how two players reach each other across the Internet: STUN, a TURN relay, connectivity checks on the
game's own socket, signaling through the control plane, and best-effort port mapping. The transport it carries is
recorded in `MultiplayerSecureTransport.md`, the network thread and its socket in `MultiplayerLiveSessions.md`, and the
control plane's API in `ControlPlaneApi.md`.

The code lives in `mods/TopiaForge.Multiplayer/Nat/`, inside the provider only, and every type is internal. Its
namespace is `TopiaForge.Multiplayer.NatTraversal`: a `TopiaForge.Multiplayer.Nat` namespace would hide the vendored
BouncyCastle subset's own `Nat` class from the code that uses it.

The Internet host and join flows call this layer through the interface in section 11; `MultiplayerInternetSessions.md`
records them. An agent that is never started contacts nothing.

## 1. Shape

| Part | Runs on | Does |
| --- | --- | --- |
| `NatAgent` | The network thread | Everything stateful, sans-I/O: the first-byte split, STUN, the TURN allocation, gathering, every session's checks and route, signaling frames |
| `NatDatagramSocket` | The network thread | Wraps the game socket for the thread: splits what arrives, wraps what leaves for a relay |
| `NatNetworkService` | The network thread | Runs the agent each turn and sends what it queued |
| `NatJoin`, `NatHost` | The main thread | Start and stop a session; expose a `ConnectAttempt` and a status that names no address |
| `ISignalingClient` | Its own I/O | The signaling socket; every call returns at once |
| `PortMapper` | Thread-pool tasks | NAT-PMP and UPnP |

The agent never blocks. DNS lookups, credential minting and the signaling socket run elsewhere; the agent starts
them and polls their completion each turn. In tests the virtual network drives the same agent through the same glue,
so the deterministic tests run the code the game runs.

## 2. The shared socket

STUN, TURN and the transport share the one UDP socket, split by first byte as RFC 7983 does:

| First byte | Datagram | Goes to |
| --- | --- | --- |
| `0x00`–`0x03` | STUN | The agent |
| `0x40`–`0x4F` | TURN ChannelData | The agent, which unwraps it |
| anything else | The transport's own: `0xE0`–`0xE5` and `0xE8` | The transport, which drops what it does not know |

The LAN beacon (`0xE6`) uses its own socket. Every STUN message this layer writes starts with 0 or 1, since all its
methods are below `0x080`, and every channel number gives a first byte from 64 to 79. A test walks all 256 values
against both this split and the transport's own `PacketType.IsForeign`, and checks a live session's datagrams.

**The hook.** The network thread needs no change. The flows build it on a `NatDatagramSocket` wrapped round the game
socket, and attach a `NatNetworkService`:

- **Reading.** Each read takes one datagram. A datagram from a source nothing genuine sends from is dropped first:
  port 0, or an unspecified, "this network", multicast, reserved or broadcast address. Transport datagrams pass
  through. STUN and ChannelData go to the agent, and the thread sees a dropped datagram, so they count against its
  turn. ChannelData or a Data indication from this side's TURN server is unwrapped. Its contents go to the transport
  from a relayed path, or back through the agent when they are STUN themselves.
- **Writing.** A send to a relayed path is wrapped for the TURN server; any other send goes straight out.
- **Size.** The hook reads into its own 2,048-byte buffer, since a Data indication carrying a full 1,200-byte transport
  datagram is about 1,250 bytes. What reaches the transport is never larger than 1,200 bytes.
- **Nothing throws.** No datagram, source or answer makes the receive path throw, since a faulted network-thread turn
  counts toward stopping the thread. A seeded test sends 20,000 hostile and edge-case datagrams through the hook:
  checks from port 0 and other unsendable sources, answers forged for real transactions with the longest nonce,
  Data indications naming port 0, IPv6 or no family, ChannelData on every kind of channel, and random bytes.
- **Allocation.** Neither direction allocates, over a bound channel or through the fallback of Send and Data
  indications. A Data indication is read where it lies, and its peer is found by its address bytes. A test replays a
  captured datagram of each kind and sends 20,000 times each way, and measures zero bytes. The same test measures
  zero bytes for 20,000 turns with nothing due, and for each route tick that sends nothing. The status object is
  replaced only when a field changes.

**Relayed paths.** A peer reached through this side's own allocation is a `TransportPathKind.Relayed` path, which
relay-only allows. Its 23-byte key is a type byte (2), the allocation's number (4 bytes), the peer's address as the
relay sees it, in IPv6 form (16), and its port (2). UDP paths use type 1, so the two never collide. The allocation
number keeps one peer reached through two allocations, as during a rotation, on two paths.

**Transport hooks.** The NAT layer asks the endpoint four things, all by path, so it never holds a connection:

- `UsageOf(path)` counts the connections on a path, stalled and validating ones included;
- `UsageWhere(predicate)` counts them the same way across every path that matches, as a joiner does for its one
  connection, wherever the transport has it;
- `CountConnections(predicate)` counts those whose path matches, such as every path through one allocation;
- `ProbeConnections(from, to)` asks the connections on one path to validate another.

The transport's own path validation decides every move (`MultiplayerSecureTransport.md`, section 10).

## 3. STUN (RFC 8489)

`StunMessage` parses and `StunMessageWriter` writes. Parsing copies the datagram and checks:

- the header: the top two bits are zero, the magic cookie, a length that matches the datagram and is a multiple of
  four, and a known method and class pairing;
- every attribute's bounds and padding, at most 32 attributes, and at most 1,500 bytes in all;
- FINGERPRINT, which must be last and must match.

Attributes after MESSAGE-INTEGRITY are dropped, except MESSAGE-INTEGRITY-SHA256 and FINGERPRINT, so nothing
unauthenticated rides behind an integrity check. Integrity is compared in constant time. Unknown
comprehension-required attributes are reported, and a response that carries one fails its transaction. The ERROR-CODE
reason phrase is free text from the server, and it is never read.

Nothing a peer or server sends makes parsing or any accessor throw. A malformed value reads as absent. A seeded fuzz
loop sends 20,000 mutated and random datagrams through every accessor. RFC 5769's four sample messages verify: both
short-term integrity checks, both XOR-MAPPED-ADDRESS families, and the long-term key. The writer reproduces the
long-term sample byte for byte.

A Data indication, the one message that arrives with every relayed datagram once channels are refused, has a reader
of its own that checks the same things where the datagram lies, copying and allocating nothing. It also refuses one
with an unknown comprehension-required attribute, as RFC 8489 discards such an indication.

`StunClient` runs transactions sans-I/O. A response matches by transaction id and by the path the request went to,
and only once its owner accepts it, typically after checking integrity. A response it refuses is dropped, and the
request keeps retransmitting as though nothing came. At most 640 run at once. Every session's share of checks (section
6) is 320 of them, and the rest stays for the relay's own requests, gathering and releases, so no number of remotes that
never answer can keep the relay from refreshing or a new joiner's checks from starting. `NatAgent.StunReserve` is that
remainder, and it would not compile were the shares ever to outgrow the client.

| Timing | Initial RTO | Transmissions | Final wait | Settled by |
| --- | --- | --- | --- | --- |
| Gathering (Binding, Allocate) and releases | 500 ms | 4 | 4 RTO | 5.5 s |
| Live TURN requests | 500 ms | 6 | 16 RTO | 23.5 s |
| Connectivity checks | 250 ms | 5 | 4 RTO | 4.75 s |
| RFC 8489 default, for reference | 500 ms | 7 | 16 RTO | 39.5 s |

## 4. TURN (RFC 8656)

`TurnClient` holds one UDP allocation on the shared socket.

- **Credentials.** The first Allocate goes unauthenticated and draws the server's 401 with realm and nonce. The client
  then sends USERNAME, REALM, NONCE and MESSAGE-INTEGRITY keyed by `MD5(username ":" realm ":" password)`. A 438 is
  retried with the fresh nonce, at most three times per request. A 401 to an authenticated request fails as
  unauthorized, with no retry. Credentials and realms are printable ASCII, which the key's string preparation leaves
  unchanged; anything else is refused.
- **Responses.** A success answering an authenticated request counts only with valid integrity. An error may lack
  integrity: 401 and 438 do by design, and only an on-path attacker who already sees the transaction id could forge
  one.
- **Failures.** Each names its cause: timeout, unauthorized, forbidden, allocation mismatch (437), quota (486),
  capacity (508), unsupported, server error, or malformed. None names an address. A 437 to any request on a live
  allocation means the server holds none on this 5-tuple any more, so the allocation is lost and replaced (see
  **Recovery** below).
- **Requests always fit.** A request that cannot fit a datagram is never sent, so no realm or nonce a server picks can
  make the client throw. A permission request carries as many addresses as fit beside the credential, the realm and
  the nonce, at most eight, so the longest of each still leaves room for one.

| Lifetime | Server keeps it | Client renews it |
| --- | --- | --- |
| Allocation | Its LIFETIME, 600 s by default, clamped to 30–3,600 s | A minute before it lapses |
| Permission (one per peer address) | 300 s | Every 240 s while a session claims it, up to eight per request |
| Channel binding (one per peer address and port) | 600 s | Every 480 s; an unanswered refresh again five seconds later, with the same number |
| The NAT mapping in front of the client | Varies | A Binding indication after 15 quiet seconds |

- **Tables, claimed per session.** Every permission and peer-path is claimed by the session that needs it, the one
  whose candidate named the address or whose checks paired the path, and it stays exactly as long as some session
  claims it. What a session claimed goes when it ends. A session claims at most 16 permissions and 24 peer-paths on
  each allocation; past that it gives up its own oldest permission or least recently used path, never another
  session's. An entry two sessions claim, such as a relay address several peers share, stays while either does. The
  tables have room for every session's whole share at once, 32 sessions of 16 permissions and 24 paths, so however
  much hostile peers claim, none can take what a session holds or keep the next one out.
- **The pool.** A port no session claims, heard from or sent to before anything paired it, as from a spray over many
  source ports, lands in a pool of 64, at most eight from one address, where the least recently used makes way, and
  one this side never sent to goes first. A pooled path never binds a channel, so letting it go leaves nothing on the
  server, and it becomes claimed once a session pairs it. Nothing but a session's claim creates a permission.
- **Channels.** Numbers come from `0x4000`–`0x4FFF`, fresh ones first. A number a peer-path gives up comes back to
  the same peer address and port while the server may still hold that binding, so the two sides never disagree on it,
  and to any other only once the binding would have lapsed, ten minutes after its last bind request, plus the
  five-minute guard (RFC 8656, section 12). That range is what RFC 8656 allows, and its first byte is RFC 7983's
  ChannelData band.
- **Before a binding.** A claimed peer-path is bound the first time it is sent to or heard from. Data waits for the
  binding's answer and goes as Send indications meanwhile. Nothing goes to a peer without an installed permission. A
  permission stays usable while its refresh is in flight. One that keeps failing lapses on both sides together.
- **Lost answers.** A server refreshes a binding whether or not its answer arrives, so ChannelData on a peer's number
  is accepted for ten minutes after the last bind request for it, answered or not. Data keeps leaving as ChannelData
  until ten minutes after the last answered bind, while the refresh is tried again with the same number; a binding
  that lapsed is bound again, with the same number, by the next datagram.
- **Refusals.** A refused batch of permissions is retried one address per request, so one forbidden peer cannot block
  the rest. A refused binding leaves that peer on Send indications. A refused refresh keeps the binding the server
  already holds until it lapses, then data falls back to Send indications.
- **Release.** Releasing is a transaction of its own: the zero-lifetime Refresh retransmits until the server answers,
  for at most 5.5 seconds, and a 437 answer settles it too, since it says nothing is left. A client that never
  allocated has nothing to release. Nothing allocates on a 5-tuple until a release there has settled. A release that
  nobody answered is remembered, and sent again when the next Allocate on its 5-tuple meets a 437.

**Credentials from the control plane.** `IceServerSet` parses `POST /v1/turn/credentials` in Cloudflare's `iceServers`
shape:

- **Servers.** Only `stun:` and UDP `turn:` URLs are kept; TCP and TLS relays are later work. Hosts must be DNS names,
  as the service promises.
- **Credential.** Exactly one entry carries `username` and `credential`, each 1 to 512 printable ASCII characters.
- **Bounds.** Members the client does not know are ignored. The answer is at most 16 KiB, with at most eight servers
  and 16 URLs each.

**Server addresses.** Each TURN server's own name is looked up, once per distinct name, and the agent tries the
addresses in a fixed order: the address family that last worked first (IPv4 before anything has), then the other;
within a family the set's servers in the control plane's order, each with its own name's addresses, at most four.

**Rotation.** Credentials last ten minutes, and their expiry ends the allocation (`ControlPlaneApi.md`, section 7). The
agent mints the next one 90 seconds before, and again whenever the relay is down, since an allocation can only come
back with a credential that still works. A failed mint is tried again after 15 seconds, doubling to five minutes, or
to 30 seconds while a rotation is due.

- **Make before break.** When the set offers another server address of the family in use, another port or another
  resolved address, the new allocation comes up there while the old one carries on. An address that failed this way
  is left alone for 30 minutes, so a port that many networks block, as many block Cloudflare's 53, costs one try.
- **Falling back.** If that new allocation fails, the rotation falls back to the address that worked: the old
  allocation there is released, and the new one takes its 5-tuple as soon as the release settles.
- **Break before make.** With no other address of that family, the old allocation is released first, and the new one
  starts on the same 5-tuple once the release has settled.
- **Moving over.** Either way, the new relay candidate is trickled to every session. Each connection on the old
  allocation moves once a pair through the new one has answered.
- **Cleaning up.** The old allocation is released once no connection runs through it, and at the latest five seconds
  before its credential expires. Its pairs, and the remote relay candidates the remote replaced, are forgotten a while
  later, so a long session's pairs stay bounded.

**Recovery.** A relay that is wanted but not ready always has its next attempt scheduled, on the host for as long as it
hosts and on a joiner until its join ends:

- **When.** After a failure the next attempt waits one second, doubling to 30, and the wait starts again from one
  second once an allocation comes up. A fallback from a rotation waits only for its release.
- **Where.** On the server address that last worked, unless its last request met silence: the network may now block
  it, so it is left alone for 30 minutes and the next address in order is tried. Before any has worked, on the same
  address after an answer, and on the next one after silence. When every address has failed lately, the next in
  order, so the attempts go round them all.
- **What first.** An allocation that came up and was lost is released before anything else, since the server may still
  hold its 5-tuple.
- **A 5-tuple someone else holds.** An Allocate that meets a 437 where this agent remembers no release of its own, as
  when a run that crashed left its allocation behind, cannot free it: the credential that made it is gone. Every
  allocation shares the game's socket, whose local port never changes, so the next attempt takes another 5-tuple by
  going to another server address or port, and that one is left alone for 30 minutes. With a single address, as the
  recorded credentials offer, the relay waits for the old allocation to lapse.
- **Credentials.** One the server refused (401) is not tried again. The first refusal after an allocation worked mints a
  fresh one at once, as when a credential is revoked. After that, as with a misconfigured key, the next mint waits 15
  seconds, doubling to five minutes, and only an allocation that comes up starts it over: at most about 17 mints in an
  hour and three in any minute, inside the control plane's 20 and five for each prefix. Without a way to mint, the
  relay stays down.
- **Gathering.** It waits for a retry that starts before its deadline, and stops waiting once an attempt has met
  silence with nothing heard at all.

## 5. Candidates and gathering

Priorities follow RFC 8445 with component 1: the type preference in the top byte, then a 16-bit local preference.

| Kind | Type preference | Comes from |
| --- | --- | --- |
| Host | 126 | This machine's own addresses: usable, not loopback, not unscoped link-local, at most eight |
| Server-reflexive | 100 | The STUN Binding answer, the TURN server's XOR-MAPPED-ADDRESS, and a port mapping (section 10) |
| Peer-reflexive | 110 | A check from an address the remote never signaled; never signaled itself |
| Relayed | 0 | The allocation's XOR-RELAYED-ADDRESS |

With the highest local preference these give 2,130,706,431, 1,694,498,815 and 16,777,215, the numbers in the control
plane's own transcript.

**Settling.** Gathering settles once every server answered or timed out, or after six seconds:

| Outcome | When |
| --- | --- |
| Complete | Everything asked for came up |
| RelayUnavailable | Something answered, but the relay did not come up |
| UdpBlocked | No STUN or TURN server answered at all, so the network blocks UDP or the servers are unreachable |

A relay that did not come up keeps being tried after gathering (section 4, **Recovery**), and the outcome turns
Complete once it does. Relay-only gathers the allocation alone (section 9).

## 6. Connectivity checks

ICE-lite on the shared socket: both sides gather, exchange descriptions and check, but nomination is left to the
transport's path validation.

- **Credentials.** Each session draws its own: an 8-character fragment and a 24-character password, six bits a
  character, from the transport's random source.
- **Checks.** A check is a Binding request carrying four things:
  - `USERNAME` as `remote:local`;
  - PRIORITY, as a peer-reflexive candidate here would have it;
  - ICE-CONTROLLING for the joiner or ICE-CONTROLLED for the host, with a tie-breaker;
  - MESSAGE-INTEGRITY under the remote's password, then FINGERPRINT.
- **Answers.** An answer must come from where the check went, carry FINGERPRINT, and prove the remote's password.
- **Pairs.** Every base pairs with every remote candidate: the socket, and each allocation. A relay base pairs only
  with public addresses of its own family.
- **Order.** Triggered checks go first. Then come relayed pairs, so the session can start, then direct pairs by RFC
  8445 pair priority. Ordinary checks go out one every 50 ms, and only after the punch. A session waits on at most
  eight ordinary and triggered checks at once; the next goes when one is answered or gives up, so a remote that never
  answers, however many candidates it names or checks it triggers, holds only its own share of the STUN client.
- **Triggered checks.** A verified check from the remote triggers one back on its pair at once, even before the punch.
  That is what opens both NATs when the two sides check together. An unknown source becomes a peer-reflexive
  candidate, paired only with the base it arrived on. When the remote later signals that address, the candidate takes
  the signaled kind: a remote's new relay candidate, first heard as a check from its new allocation, then replaces the
  remote's older relay candidates as any new relay candidate does.
- **Answering.** A check gets an answer only when it carries:
  - FINGERPRINT;
  - a username naming one of this side's sessions and that session's remote fragment;
  - integrity under this side's password;
  - the role opposite this side's.

  Anything else gets nothing at all. The answer goes back on the path the check came in on, with XOR-MAPPED-ADDRESS
  naming the address it came from.
- **Consent (RFC 7675).** The pair in use and the relay pair standing by behind a direct one each send a check every
  15 seconds, give or take a fifth. Consent lapses 30 seconds after the last answer.

| Bound | Value |
| --- | --- |
| Remote candidates per session | 24, of which peer-reflexive 8 |
| Pairs per session | 64 |
| Checks waiting for an answer, per session | 8, and 2 consent checks |
| Sessions per host | 32. With every one taken, the oldest that has no connection and whose checks ran out makes way for a new peer, without a goodbye, so sessions kept warm with checks never keep a joiner out |

## 7. Routes

The transport decides every move. This side only asks it to validate a path, and a connection moves when the peer
answers there.

- **Start on the relay.** A join connects over the best relayed pair that has answered. It goes direct only when no
  relayed pair can exist, when every relayed pair has failed, or when a direct pair has waited out a one-second relay
  grace.
- **Ranking relays.** Relayed pairs rank the same way on both sides: through the host's relay, then the joiner's, then
  both, and the newest allocation first.
- **Upgrade.** The joiner, which controls the checks, asks the transport to validate the best direct pair that has
  answered, at most twice per pair. The host follows, because the transport validates a path its peer moved to.
- **Fall back.** Either side moves a connection back to a relay when its direct path stalls (no authenticated packet
  for three seconds) or loses consent.
- **Rotate.** The side whose allocation is being replaced moves the connections on it to the best pair that does not
  use it.
- **Never onto what is going.** No move targets a pair through an allocation either side is replacing: this side's
  retiring base, or a remote relay candidate the remote has replaced.
- **Trying again.** A pair that twice failed to take a connection is checked again before a third try. A path that
  died since its last answer, such as one through a relay the remote released, then fails its check, and the next
  best pair is tried; one that answers is tried again.

The route is evaluated every 200 ms. The status reports it as relayed or direct, never as an address.

## 8. Signaling

`SignalCodec` speaks the grammar in `ControlPlaneApi.md`, section 8.

- **Writing.** Frames this side sends have exactly the members the service expects. The tests replay the service's
  own recorded transcript, `tests/fixtures/multiplayer/control-plane/signaling.json`: every frame a client sends is
  written back byte for byte, also from a message built afresh, and every frame the service sends reads. The relay
  credentials the service recorded in `exchanges.json` parse too.
- **Reading.** Frames from the service must carry their members with valid values; members the service may add later
  are ignored. A strict mode reads client frames as the service does, for the tests' fake room.
- **Candidates.** An address must be in its canonical text: dotted IPv4, RFC 5952 IPv6, a mapped address as IPv4.
- **Bounds.** At most 16 candidates per offer or answer, a punch delay of 0 to 10,000 ms, and 8 KiB per frame.
  Nothing malformed throws.

**A join.**

1. Gathering settles while the signaling socket opens.
2. Once the welcome says the host is online, the joiner offers.
3. The answer brings the host's credentials and candidates; the host's punch starts the checks after its delay. The
   joiner checks anyway one second after the answer if no punch arrives.
4. Candidates learned later are trickled.
5. A goodbye, or the host's session ending, stops a join that has not connected.

| Join failure | Status says |
| --- | --- |
| Gathering heard nothing | `UdpBlocked` |
| Relay-only, and no relay | `RelayUnavailable` |
| The host stayed offline for 10 s | `HostOffline` |
| No answer within 10 s of the offer | `NoAnswer` |
| No usable pair within 10 s of the checks starting, or the handshake over it failed | `NoRoute` |
| The service ended the host's session, or the host said goodbye | `HostEnded` |
| The service stopped honouring the ticket (4403): its invite was withdrawn or expired, or the server stopped being public | `TicketRevoked` |
| The socket could not be opened or kept | `SignalingFailed` |

**A host.** Each offer gets a session with fresh credentials, an answer once gathering has settled, and a punch of 250
ms. A session whose peer never connected, or whose connection has been gone 30 seconds, ends. The host keeps its
socket for as long as it hosts. It says goodbye to its peers only when it stops, and a host's goodbye keeps that
peer's ticket out of the room for the rest of the session (`ControlPlaneApi.md`, section 8.4).

**A joiner's socket.** The room counts every open peer socket against `maxPlayers + 2` places, and at most four from
one /24 or /56, so a joiner closes its socket once its connection is up and opens it again only when it has something
to say:

- **While its relay changes**: from the moment it starts minting a fresh credential, or loses its allocation, until the
  new allocation is up and every connection has moved off the old one, for at most two minutes. Its new relay
  candidate is trickled while the socket is open. A relay that comes back later is gathered with the socket closed,
  and the host hears of it when the socket next opens.
- **While its connection stalls**: no authenticated packet for three seconds, on whatever path the transport has it,
  until one arrives again.

It opens it at most once a second, and with backoff after an opening that failed, but nothing else holds it back,
whether its own relay changes or its path through the host's relay stalls: its connection lives on it, and both come
no faster than credentials expire and networks drop, far inside the service's 30 upgrades a minute and 240 an hour
for each prefix. Closing sends whatever is queued first, so a candidate trickled just before still reaches the host.
Coming back, the joiner trickles its candidates again, and the host, told by `joined`, sends its own again, since
anything trickled while the socket was closed went nowhere.

**Reconnection.** A peer's socket lives at most fifteen minutes, so it is opened again when its lifetime ends (4411),
at once, and after a lost connection or a rate limit, with backoff from one second doubling to 30: for the host or a
joiner still connecting, at most six times in ten minutes, and for a connected joiner whenever it needs the socket. A
replaced socket (4409), an ended session (4410), a withdrawn ticket (4403) or a goodbye is final. A refused upgrade is
final before the connection is up; once it is up, the joiner tries again later, since a full room refuses an upgrade
too and the client cannot tell the refusals apart. The host keeps a peer's session across its reconnections, since the
peer id comes from the ticket.

**The WebSocket client.** `WebSocketSignalingClient` opens `GET /v1/signal/{serverId}`:

- **Origin.** An https origin makes the socket wss. Plain ws is accepted only to a loopback address, for a control plane
  running on this machine during development.
- **Credential.** The host token or join ticket travels in the `Authorization` header, never in the URL.
- **Loops.** Its own receive and send loops run apart from the agent. Both queues are bounded at 256 frames.
- **Frames.** A binary frame, or one over 8 KiB, closes the socket. Every failure becomes a close reason, never an
  exception or a message: section 8.4's codes map to normal (1000), invalid (1003, 1009, 4400), withdrawn (4403), rate
  limited (4408), replaced (4409), session ended (4410) and expired (4411); a refused upgrade, whatever its status, is
  refused.

The game's Mono runs `ClientWebSocket` over TLS 1.2 (`MultiplayerMonoSpike.md`, section 2). It was measured against
two public echo servers first, and then inside the game against the staging control plane's Worker
(`MultiplayerInternetSessions.md`, section 11). Everything outside this one class depends only on `ISignalingClient`,
and the tests run the real client against a loopback server.

## 9. Privacy

The hard rule: no candidate, peer or observed address reaches mod code, `MultiplayerSessionSnapshot`, logs,
diagnostics or exceptions.

**Relay-only** is the player's Hide my IP, which stays on unless the player allows a direct connection for one
session (`MultiplayerPrivacyText.md`). Direct play is never a silent default: `NatAgentOptions.RelayOnly` and the
transport's `TransportOptions.RelayOnly` are both on unless a caller turns them off for a player who chose direct play,
and a session that starts on a relay-only stack is relay-only whatever the agent's own option says. A test builds both
players from default options behind cone NATs that would otherwise go direct at once: neither looks up the STUN server,
gathers a host or server-reflexive candidate, or sends anywhere but its relay. Relay-only holds in layers, and each
layer has its own test. A relay-only agent:

- never lists this machine's addresses and never asks a STUN server anything, so it holds no host or server-reflexive
  candidate at all;
- has no socket base, so it never checks a direct path;
- answers no check that did not come through its own relay;
- sends nothing anywhere but its own TURN servers, whatever asks: a guard at the agent's one send point;
- signals only what `NatPrivacy`, the one gate every offer, answer and trickled candidate passes, lets through. That is
  relay candidates only, never a peer-reflexive one, an unusable address, or more than 16.

The only name a relay-only agent looks up is its TURN server's, which the control plane chose, so DNS learns no more
than that the player uses the relay. The live flows' rule that relay-only resolves no names
(`MultiplayerLiveSessions.md`) covers join targets, which this layer never resolves.

The service strips non-relay candidates from relay-only sides too, but the client does not rely on it. Its tests turn
the fake service's stripping off and check that the other side never learns, never hears from and never receives a
candidate for any address of the hidden side's own.

**Nothing prints an address.**

- Every type that holds one prints only its kind: candidates, pairs, bases, credentials, signal messages, server
  addresses and transaction ids.
- Diagnostics are fixed text, chosen in the code.
- The status is enums and counts by construction, and a test checks each of its properties' types.
- Exception messages are fixed, and failures carry an enum, never an address.

A leak probe runs successful, upgraded and failed sessions (blocked UDP, a host offline, no route). It collects every
status, diagnostic, exception and printed object, and finds no address in any textual form. Nothing in the layer is
public.

**No control plane, no contact.** An agent never started answers no STUN, unwraps nothing, sends nothing and waits
for nothing. Starting one needs a session plan, and only the control plane's answers can build one. A stopped agent
hears only the answers to its releases, and only while they are in flight. Port mapping starts only once hosting has.

## 10. Port mapping

Best effort, only while hosting, and never for a relay-only host. The mapping belongs to hosting, not to the wrapper
that asked for it: `NatHost` starts it from the command that starts hosting on the network thread, once `StartHost`
has returned, and only if that host shows its address; the agent's `Stop` and `Dispose` delete it, so stopping the
network deletes it as surely as disposing the host does. A network thread that never ran, or a start that threw,
maps nothing.

- **Protocols.** NAT-PMP (RFC 6886) first, against the default IPv4 gateway. UPnP IGD otherwise: an SSDP search, the
  device description, and the WAN connection service's actions.
- **Leases.** An hour, renewed at half time, and deleted when hosting stops.
- **Candidates.** A mapping's public endpoint becomes a server-reflexive candidate, trickled to every session. A gateway
  whose external address is private, behind another NAT, maps nothing.
- **NAT-PMP.** Requests go out four times, 250 ms apart and doubling: far fewer than the RFC's nine, because a game
  would rather fall back to UPnP or the relay than wait two minutes. An answer counts only from the gateway's address
  and port, with the right version, opcode, length and internal port.
- **UPnP: only the LAN gateway counts.**
  - An SSDP answer must come from a LAN address, and its LOCATION must name that same address over plain HTTP.
  - The control URL must stay on that host and port.
  - The description is read with DTDs refused and no external resolution, in whatever order a service lists its
    children, pretty-printed or not: a test reads all 120 orders both ways.
  - HTTP is a small client of its own, with no proxy, no redirect and a timeout on every step, so a LAN request can
    never leave the LAN.
- **UPnP: refusals.** A gateway that grants only permanent mappings (725) gets one, deleted all the same. A conflict
  (718) moves to the next port, at most three times. The mapping's description is the fixed text `TopiaForge`.
- **Sockets.** The NAT-PMP and SSDP sockets take a bind address, the wildcard by default. Tests bind loopback, so no
  firewall prompt appears.

## 11. The interface for the host and join flows

The flows reach the network through `INetworkFactory` and `INetworkControl`. A NAT-aware factory builds the network
thread on the NAT layer's socket; the flows then start a session with a plan made from the control plane's answers.

```csharp
// INetworkFactory.Start(port, stack), NAT-aware. Both options are relay-only unless the player chose direct play:
var nat = NatPlumbing.Wrap(new UdpSocketDriver(port), new NatAgentOptions { RelayOnly = !settings.AllowDirect }, clock);
var thread = new NetworkThread(name, nat.Socket, stack, clock);   // stack built with TransportOptions.RelayOnly = !settings.AllowDirect
thread.Attach(nat.Service);
thread.Start();
INetworkControl network = thread;

var plan = new NatSessionPlan(
    servers,                                   // IceServerSet.TryParse of POST /v1/turn/credentials
    () => WebSocketSignalingClient.Open(origin, serverId, ticketOrToken),
    host => Dns.GetHostAddressesAsync(host))
{
    Renew = MintAsync,                         // POST /v1/turn/credentials again, before the ten minutes are up
    LocalAddresses = addresses,                // NetworkInterface unicast addresses, listed off the network thread
    LocalPort = thread.LocalPort
};

using var join = NatJoin.Start(network, nat, plan, descriptor.TransportKey);         // poll join.Attempt like Connect's
using var host = NatHost.Start(network, nat, plan, mapPorts: settings.AllowDirect);  // peers arrive as ordinary links
```

- `NatJoin.Attempt` fills in exactly as `INetworkControl.Connect` does: it becomes established with its link, or it
  fails. `NatJoin.Status.Failure` then says why, in the words of section 8's table.
- `NatHost.Status` counts sessions, direct and relayed. `NatHost.PortMapping` says whether the port is mapped.
- Either goes relay-only when the agent's option or the stack's `TransportOptions.RelayOnly` says so, so the two can
  only ever disagree toward hiding the player.
- Disposing either sends goodbyes, deletes the port mapping and releases the relay; the releases retransmit until the
  server answers while the network still runs. Stopping the network does the same for whatever is still running.
  A host's goodbyes keep its peers' tickets out of the room for the rest of the registration, so a host restarts
  `NatHost` within one registration only if its peers can take new tickets.
- Every session starts afresh on the agent: its signaling, gathering, relay targets, backoffs and credentials. A host
  may stop and host again, and a player may join again, on the same agent and socket; only releases still settling
  carry over, since the next allocation on their 5-tuple waits for them.

What the flows still own:

- building both option types from the player's choice, `RelayOnly = !settings.AllowDirect`, never from a default;
- minting credentials with the join ticket or host token, at the start and whenever `Renew` asks, including while the
  relay is down; a mint that fails returns null and is asked again later;
- listing local addresses;
- turning each `NatFailure` into player-facing words, `TicketRevoked` included, which needs a new ticket;
- the rule that a link from the server browser or an invite uses this layer, while LAN and direct links connect as
  before.

## 12. Tests

| Suite | What it proves |
| --- | --- |
| `NatStunTests` | RFC 5769's samples, tampering, hostile datagrams, nothing behind integrity, transaction timing and matching |
| `NatTurnTests` | The 401 and 438 flows, failures by cause, forged answers, 25 minutes of permissions, channels and refreshes probed every five seconds, data before a binding, release, keepalives, the `iceServers` shape |
| `NatTurnRecoveryTests` | A channel whose six refresh answers are lost keeps delivering all 120 datagrams and is refreshed again with its own number, one whose answers are all lost past its lapse still takes the ChannelData the server relays, a refused refresh loses nothing, releases retransmit and go again after silence, a 437 marks a vanished allocation, and the longest realm, nonce and credential never overflow a request |
| `NatRelayRecoveryTests` | Relay-only sessions with 150-second credentials surviving a second port that answers late or never, a lost release, a release lost entirely (437), a refused first allocation, minting while the relay is down, the family that worked, with a second port and without, each server's own name, and half an hour of repeated outages |
| `NatDemuxTests` | The first-byte split across all 256 values and a live session, the socket hook's three branches, zero allocation through a bound channel and through the indication fallback, in idle turns and in quiet route ticks |
| `NatReceiveFuzzTests` | An authentic check from port 0, every source that cannot send, and 20,000 hostile datagrams through the socket hook, none of which throws; the session carries on |
| `NatIceTests` | Priorities, credentials, the check format, answers under the wrong password, triggered and peer-reflexive checks and their bounds, consent timing, what the agent answers |
| `NatMatrixTests` | The NAT matrix below, fallback when a direct path breaks, and relayed sessions outliving seven credential rotations both ways |
| `NatPrivacyTests` | Relay-only with the service's stripping off, default options that never go direct, the privacy gate and relay-only gathering on their own, the leak probe, the status's types, no contact without a plan |
| `NatSignalingTests` | The control plane's shared transcript byte for byte, its recorded relay credentials, strict and bounded reading, the WebSocket client's upgrade, frames, close codes and abuse against a loopback server |
| `NatPortMappingTests` | NAT-PMP frames, mapping, renewal and deletion, a forged answer from another port, unusable gateways, UPnP validation and leases |
| `NatPortMappingLifecycleTests` | Mapping only once hosting runs, never for a relay-only agent or stack, a network that never started or a start that threw, deletion when the network stops; a join on a relay-only stack; UPnP descriptions in every order, pretty-printed and minified |
| `NatSessionTests` | Reconnecting after a socket's lifetime, a joiner closing its socket once connected and reopening only around a rotation or a stall, a host's rotations reaching a joiner whose socket is closed on relays that share no address, a joiner saying again what it gathered with its socket closed, a connected joiner coming back after a full room refused it, five joiners from one /24 within the room's limits for twenty minutes of rotations, each join failure, the service's notice and error frames, one host with three peers |
| `NatSocketTests` | Two real network threads on the socket hook over the game's UDP driver bound to loopback, joining through a relay and releasing it |
| `NatRestartTests` | A host that stopped and hosts again on the same agent answering the next joiner, relay-only and direct, and a player joining again on the same agent |
| `NatLongSessionTests` | On relays that give every allocation an address of its own: a joiner whose network drops for four seconds every 110 seconds for 35 minutes of rotations on one TURN port, and five joiners through half an hour of 150-second credentials, all staying connected |
| `NatRelayTargetTests` | A relay leaving an address that went silent for the one that came back, an allocation a crashed run left on the 5-tuple sending the next to another port, and credentials refused right after a working allocation (minted again at once) or for an hour (within five a minute and twenty an hour) |
| `NatTableQuotaTests` | One allocation's tables: a session's share of sixteen permissions and twenty-four paths, its own oldest or least recently used giving way; a claim two sessions share staying until both let go; thirty-two whole shares at once and no thirty-third; a pool of sixty-four unclaimed ports, eight per address, that never binds a channel; a channel number coming back to its own peer at once and to another only after the binding's lapse and the guard |
| `NatHostilePeerTests` | Hostile identities against a relay-only host, each within its shares and on relay addresses of its own: a flood from one identity, a spray over 300 ports, thirty-one identities naming sixteen addresses or twenty-four ports each, forty-eight identities churning through one /24, a shared relay address a hostile peer named first, thirty-two sessions kept warm with checks, and checks re-triggered every second from 744 ports. Each time the next honest joiner gets in, and a connected player, rotating its relay under warm hostile sessions on production credentials, never misses an exchange |

**The NAT simulator.** It runs over `VirtualDatagramNetwork`, which gained attachments, a router and a configurable
datagram limit. Sites and NAT boxes model RFC 4787's mapping and filtering behaviours:

- deterministic external ports;
- idle mapping expiry;
- hairpinning, allowed only where configured;
- blocked UDP.

A fake STUN and TURN server enforces the lifetimes a real one does. Its knobs fail requests, refuse the next
allocations or every channel bind, lose requests on their way in, forget every allocation, forbid peers, end
credentials, refuse every credential as a misconfigured key does, give every allocation a relayed address of its
own, and silence it. It answers a request on an allocation with 441 unless it carries the credential that made it.
The router can lose everything sent to one address and port, as a network blocking a port does. The fake signaling
room behaves as the service's, with its places, its per-prefix share, a host's goodbyes and withdrawn tickets.

| Host | Joiner | Result |
| --- | --- | --- |
| Full cone | Full cone | Starts on the relay, then both sides go direct |
| Address-restricted | Address-restricted | Starts on the relay, then direct |
| Port-restricted | Port-restricted | Starts on the relay, then direct |
| Full cone | Port-restricted | Starts on the relay, then direct |
| Port-restricted | Address-restricted | Starts on the relay, then direct |
| Port-restricted | Symmetric | Stays on the relay; no direct pair ever answers |
| Symmetric | Symmetric | Stays on the relay |
| Both behind one carrier-grade NAT that does not hairpin, each behind a home router | | Stays on the relay |
| Any | Network blocks UDP | The join fails with `UdpBlocked`, and no offer goes out |
| Either or both relay-only | | Works through the relay, never upgrades, and no candidate of the hidden side leaks |

A symmetric NAT facing a full-cone one can still go direct, through the peer-reflexive address its check reveals. That
is real behaviour, and the three-peer test relies on the host being port-restricted to keep its symmetric peer on the
relay.

**Mutation checks.** Each of these was broken on purpose and a test failed:

- integrity verification, and what follows it;
- a check's integrity, and an answer's integrity;
- both first-byte ranges, and the socket hook's split;
- the privacy gate, relay-only gathering, and the relay-only send guard;
- permission renewal, channel renewal, and the 438 retry;
- triggered checks, the upgrade, the consent lapse, and pruning across rotations;
- a status replaced on every turn, and a route tick that enumerates the pairs through the read-only interface;
- recovery: allocating again after a loss, a rotation's fallback, retransmitted releases, waiting for a release to
  settle, the second release after a 437, a 437 to any request, and minting while the relay is down, with and
  without the renewal timer following the same rule;
- server addresses: the family order, make before break keeping the family, and each server's own name;
- channels and permissions: retrying an unanswered refresh, taking ChannelData after lost answers, and batches sized
  to the nonce;
- signaling: closing once connected, the host's and the joiner's re-trickle on a rejoin, reopening around a change of
  relay and after a stall, asking again after a refused upgrade, close code 4403, and `TicketRevoked`;
- a connected joiner's reopenings held back by the reconnection window, with the window's end kept in the backoff
  deadline as it was, and in a field of its own while stalls still count against it;
- restarts: a stopped session's signaling staying done, nothing reset when a session starts, and gathering staying
  settled from the session before;
- refused credentials: minting at once after every refusal, a working allocation not starting the backoff over, and
  each fresh credential starting it over;
- server addresses that stopped answering: going back to the one that last worked whatever happened there, and a
  timeout not marking an address; and a 5-tuple an allocation left by a crash holds, retried in place;
- the indication fallback's in-place lookup;
- mapping only once hosting runs, and deletion on `Stop` and `Dispose`;
- dropping sources that cannot send, alone and with the check handler's own port-0 guard, which brings back the
  reviewed exception;
- the UPnP reader's position after an element's content;
- both relay-only defaults, and a relay-only stack making its session relay-only;
- route keeping: promoting a peer-reflexive candidate the remote signals, and skipping superseded relay candidates
  together with checking a pair again before a third probe. Each of the last two survives alone, since either keeps
  a connection off a relay the remote released.

## 13. Limits and later work

- **TURN over TCP and TLS** on 443, for networks that block UDP, is later work, as the plan says. Until then those
  networks report `UdpBlocked`.
- **Relay allocations are IPv4.** IPv6 host candidates are gathered and checked, but no relay pairs with them.
- **Unverified against Cloudflare's TURN service:**
  - channel bindings;
  - what credential expiry does to a live allocation;
  - relaying between two of its own allocations, which a session with both sides relay-only needs;
  - whether it answers a release, and a retransmitted one, as the fake server does (success, then 437), and whether
    a lost release leaves the 5-tuple taken until the allocation lapses;
  - whether port 53 or a second address is offered for UDP at all: the recorded credentials offer only port 3478, so a
    rotation there always breaks before it makes;
  - whether a new allocation's relayed address keeps the old one's IP, which a host's rotation relies on to reach a
    joiner whose signaling is closed (below).

  The design follows the RFCs and the control plane's contract, and falls back to Send indications if bindings are
  refused.
- **Rotation needs signaling, or a shared relay address.** A new relay candidate reaches the remote over signaling,
  and a connected joiner keeps its socket closed except around its own rotations and stalls. A host's new relay
  candidate therefore reaches such a joiner only through a check from the host's new allocation, which the joiner's
  relay admits only if it already permits that IP, as it does when the new relayed address keeps the old one's IP.
  Otherwise the joiner learns it when its path stalls, by reopening its socket, which the host answers by re-sending its
  candidates: the connection stalls for a few seconds once per host rotation. A session whose signaling stays down past
  a credential's lifetime keeps only its direct path.
- **A crashed run's allocation holds its 5-tuple.** The game's socket keeps its port, so after a crash the relay moves
  to another server address or port; with only one, as the recorded credentials offer, it waits for that allocation
  to lapse, up to its lifetime.
- **An address that went silent once is left alone for 30 minutes.** A brief outage on one port therefore turns that
  port's rotations into break before make until it has passed.
- **A vanished allocation is noticed at its next request.** If the server forgets an allocation (a restart, or an
  anycast route moving the 5-tuple), the client learns it from the 437 to its next permission, channel or allocation
  refresh, up to four minutes later, not from the stalled connection. A relay-only connection on it dies first.
- **Migration races.** Both sides of a connection follow a peer that moved, so in-flight packets on the old path can
  briefly pull it back. The transport cancels such a check when newer packets arrive on the current path, and every
  deterministic run converges. A real-socket run under heavy load can take longer.
- **Not implemented:** PCP, UPnP's `AddAnyPortMapping`, and IPv6 pinholes.
