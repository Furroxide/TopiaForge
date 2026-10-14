# Multiplayer NAT traversal — design record

Status: Implementing (WS7 of the live-multiplayer plan, game-side connectivity) · Date: 2026-10-14 · Owner: multiplayer

This records how two players reach each other across the Internet: STUN, a TURN relay, connectivity checks on the
game's own socket, signaling through the control plane, and best-effort port mapping. The transport it carries is
recorded in `MultiplayerSecureTransport.md`, the network thread and its socket in `MultiplayerLiveSessions.md`, and the
control plane's API in `ControlPlaneApi.md`.

The code lives in `mods/TopiaForge.Multiplayer/Nat/`, inside the provider only, and every type is internal. Its
namespace is `TopiaForge.Multiplayer.NatTraversal`: a `TopiaForge.Multiplayer.Nat` namespace would hide the vendored
BouncyCastle subset's own `Nat` class from the code that uses it.

Nothing calls this layer yet. The host and join flows will, through the interface in section 11. Until then no
player-facing behaviour changes, and an agent that is never started contacts nothing.

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

- **Reading.** Each read takes one datagram. Transport datagrams pass through. STUN and ChannelData go to the agent,
  and the thread sees a dropped datagram, so they count against its turn. ChannelData or a Data indication from this
  side's TURN server is unwrapped. Its contents go to the transport from a relayed path, or back through the agent
  when they are STUN themselves.
- **Writing.** A send to a relayed path is wrapped for the TURN server; any other send goes straight out.
- **Size.** The hook reads into its own 2,048-byte buffer, since a Data indication carrying a full 1,200-byte transport
  datagram is about 1,250 bytes. What reaches the transport is never larger than 1,200 bytes.
- **Allocation.** Over a bound channel, neither direction allocates. A test replays a captured ChannelData datagram
  and sends 20,000 times each way and measures zero bytes. The same test measures zero bytes for 20,000 turns with
  nothing due, and for each route tick that sends nothing. The status object is replaced only when a field changes.

**Relayed paths.** A peer reached through this side's own allocation is a `TransportPathKind.Relayed` path, which
relay-only allows. Its 23-byte key is a type byte (2), the allocation's number (4 bytes), the peer's address as the
relay sees it, in IPv6 form (16), and its port (2). UDP paths use type 1, so the two never collide. The allocation
number keeps one peer reached through two allocations, as during a rotation, on two paths.

**Transport hooks.** The NAT layer asks the endpoint three things, all by path, so it never holds a connection:

- `UsageOf(path)` counts the connections on a path, stalled and validating ones included;
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

`StunClient` runs transactions sans-I/O. A response matches by transaction id and by the path the request went to,
and only once its owner accepts it, typically after checking integrity. A response it refuses is dropped, and the
request keeps retransmitting as though nothing came. At most 256 run at once.

| Timing | Initial RTO | Transmissions | Final wait | Settled by |
| --- | --- | --- | --- | --- |
| Gathering (Binding, Allocate) | 500 ms | 4 | 4 RTO | 5.5 s |
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
  capacity (508), unsupported, server error, or malformed. None names an address.

| Lifetime | Server keeps it | Client renews it |
| --- | --- | --- |
| Allocation | Its LIFETIME, 600 s by default, clamped to 30–3,600 s | A minute before it lapses |
| Permission (one per peer address) | 300 s | Every 240 s while wanted, up to eight per request |
| Channel binding (one per peer address and port) | 600 s | Every 480 s |
| The NAT mapping in front of the client | Varies | A Binding indication after 15 quiet seconds |

- **Channels.** Numbers come from `0x4000`–`0x4FFF`, in order, and are never reused within an allocation. That range
  is what RFC 8656 allows, and its first byte is RFC 7983's ChannelData band.
- **Before a binding.** A peer is bound the first time it is sent to or heard from. Data waits for the binding's answer
  and goes as Send indications meanwhile. Nothing goes to a peer without an installed permission. A permission stays
  usable while its refresh is in flight. One that keeps failing lapses on both sides together.
- **Refusals.** A refused batch of permissions is retried one address per request, so one forbidden peer cannot block
  the rest. A refused binding leaves that peer on Send indications.
- **Release.** Stopping sends a zero-lifetime Refresh, once.

**Credentials from the control plane.** `IceServerSet` parses `POST /v1/turn/credentials` in Cloudflare's `iceServers`
shape:

- **Servers.** Only `stun:` and UDP `turn:` URLs are kept; TCP and TLS relays are later work. Hosts must be DNS names,
  as the service promises.
- **Credential.** Exactly one entry carries `username` and `credential`, each 1 to 512 printable ASCII characters.
- **Bounds.** Members the client does not know are ignored. The answer is at most 16 KiB, with at most eight servers
  and 16 URLs each.

**Rotation.** Credentials last ten minutes, and their expiry ends the allocation (`ControlPlaneApi.md`, section 7). The
agent mints the next one 90 seconds before:

- **Make before break.** When the set offers another UDP server address, a second port or another resolved address,
  the new allocation comes up there while the old one carries on.
- **Break before make.** With one address there can be only one allocation per 5-tuple, so the old one is released
  first and the new one starts 500 ms later, after the release has reached the server.
- **Moving over.** Either way, the new relay candidate is trickled to every session. Each connection on the old
  allocation moves once a pair through the new one has answered.
- **Cleaning up.** The old allocation is released once no connection runs through it. Its pairs, and the remote relay
  candidates the remote replaced, are forgotten a while later, so a long session's pairs stay bounded.

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

Relay-only gathers the allocation alone (section 9).

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
  8445 pair priority. Ordinary checks go out one every 50 ms, and only after the punch.
- **Triggered checks.** A verified check from the remote triggers one back on its pair at once, even before the punch.
  That is what opens both NATs when the two sides check together. An unknown source becomes a peer-reflexive
  candidate, paired only with the base it arrived on.
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
| Sessions per host | 32, as the signaling room allows |

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

The route is evaluated every 200 ms. The status reports it as relayed or direct, never as an address.

## 8. Signaling

`SignalCodec` speaks the grammar in `ControlPlaneApi.md`, section 8.

- **Writing.** Frames this side sends have exactly the members the service expects. They match the service's recorded
  transcript byte for byte, and the shared fixture is replayed too once it is in the tree.
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
| The socket could not be opened or kept | `SignalingFailed` |

**A host.** Each offer gets a session with fresh credentials, an answer once gathering has settled, and a punch of 250
ms. A session whose peer never connected, or whose connection has been gone 30 seconds, ends.

**Reconnection.** A peer's socket lives at most fifteen minutes, so it is opened again when its lifetime ends (4411),
at once, and after a lost connection or a rate limit, with backoff from one second doubling to 30, at most six times in
ten minutes. A replaced socket (4409), an ended session (4410) or a refused upgrade is final. The host keeps a peer's
session across its reconnections, since the peer id comes from the ticket.

**The WebSocket client.** `WebSocketSignalingClient` opens `GET /v1/signal/{serverId}`:

- **Origin.** An https origin makes the socket wss. Plain ws is accepted only to a loopback address, for a control plane
  running on this machine during development.
- **Credential.** The host token or join ticket travels in the `Authorization` header, never in the URL.
- **Loops.** Its own receive and send loops run apart from the agent. Both queues are bounded at 256 frames.
- **Frames.** A binary frame, or one over 8 KiB, closes the socket. Every failure becomes a close reason, never an
  exception or a message.

The game's Mono runs `ClientWebSocket` over TLS 1.2 (`MultiplayerMonoSpike.md`, section 2), but that was measured
against two public echo servers, not yet against the control plane's Worker. Everything outside this one class
depends only on `ISignalingClient`, and the tests run the real client against a loopback server.

## 9. Privacy

The hard rule: no candidate, peer or observed address reaches mod code, `MultiplayerSessionSnapshot`, logs,
diagnostics or exceptions.

**Relay-only** is the player's Hide my IP, which stays on unless the player allows a direct connection for one
session (`MultiplayerPrivacyText.md`). It holds in layers, and each layer has its own test. A relay-only agent:

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
for nothing. Starting one needs a session plan, and only the control plane's answers can build one. Port mapping
starts only from a host session.

## 10. Port mapping

Best effort, only while hosting, and never for a relay-only host:

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
  - The description is read with DTDs refused and no external resolution.
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
// INetworkFactory.Start(port, stack), NAT-aware:
var nat = NatPlumbing.Wrap(new UdpSocketDriver(port), new NatAgentOptions { RelayOnly = !settings.AllowDirect }, clock);
var thread = new NetworkThread(name, nat.Socket, stack, clock);   // the stack's TransportOptions.RelayOnly matches
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
- Disposing either sends goodbyes and releases the relay. Stopping the network does the same for whatever is still
  running.

What the flows still own:

- minting credentials with the join ticket or host token;
- listing local addresses;
- turning each `NatFailure` into player-facing words;
- the rule that a link from the server browser or an invite uses this layer, while LAN and direct links connect as
  before.

## 12. Tests

| Suite | What it proves |
| --- | --- |
| `NatStunTests` | RFC 5769's samples, tampering, hostile datagrams, nothing behind integrity, transaction timing and matching |
| `NatTurnTests` | The 401 and 438 flows, failures by cause, forged answers, 25 minutes of permissions, channels and refreshes probed every five seconds, data before a binding, release, keepalives, the `iceServers` shape |
| `NatDemuxTests` | The first-byte split across all 256 values and a live session, the socket hook's three branches, zero allocation through a bound channel, in idle turns and in quiet route ticks |
| `NatIceTests` | Priorities, credentials, the check format, answers under the wrong password, triggered and peer-reflexive checks and their bounds, consent timing, what the agent answers |
| `NatMatrixTests` | The NAT matrix below, fallback when a direct path breaks, and relayed sessions outliving seven credential rotations both ways |
| `NatPrivacyTests` | Relay-only with the service's stripping off, the privacy gate and relay-only gathering on their own, the leak probe, the status's types, no contact without a plan |
| `NatSignalingTests` | The control plane's transcript byte for byte, strict and bounded reading, the WebSocket client's upgrade, frames, close codes and abuse against a loopback server |
| `NatPortMappingTests` | NAT-PMP frames, mapping, renewal and deletion, a forged answer from another port, unusable gateways, UPnP validation and leases, no mapping for relay-only hosts |
| `NatSessionTests` | Reconnecting after a socket's lifetime, each join failure, the service's notice and error frames, one host with three peers |
| `NatSocketTests` | Two real network threads on the socket hook over the game's UDP driver bound to loopback, joining through a relay and releasing it |

**The NAT simulator.** It runs over `VirtualDatagramNetwork`, which gained attachments, a router and a configurable
datagram limit. Sites and NAT boxes model RFC 4787's mapping and filtering behaviours:

- deterministic external ports;
- idle mapping expiry;
- hairpinning, allowed only where configured;
- blocked UDP.

A fake STUN and TURN server enforces the lifetimes a real one does. Its knobs fail requests, forbid peers, end
credentials and silence it. The fake signaling room behaves as the service's.

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
- a status replaced on every turn, and a route tick that enumerates the pairs through the read-only interface.

## 13. Limits and later work

- **TURN over TCP and TLS** on 443, for networks that block UDP, is later work, as the plan says. Until then those
  networks report `UdpBlocked`.
- **Relay allocations are IPv4.** IPv6 host candidates are gathered and checked, but no relay pairs with them.
- **Unverified against Cloudflare's TURN service:**
  - channel bindings;
  - what credential expiry does to a live allocation;
  - relaying between two of its own allocations, which a session with both sides relay-only needs.

  The design follows the RFCs and the control plane's contract, and falls back to Send indications if bindings are
  refused.
- **Rotation needs signaling.** A new relay candidate reaches the remote over signaling. A session whose signaling
  stays down past a credential's lifetime keeps only its direct path.
- **Migration races.** Both sides of a connection follow a peer that moved, so in-flight packets on the old path can
  briefly pull it back. The transport cancels such a check when newer packets arrive on the current path, and every
  deterministic run converges. A real-socket run under heavy load can take longer.
- **Not implemented:** PCP, UPnP's `AddAnyPortMapping`, and IPv6 pinholes.
