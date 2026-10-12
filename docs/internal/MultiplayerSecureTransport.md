# Multiplayer secure transport — design record

Status: Implementing (WS2e of the live-multiplayer plan) · Date: 2026-10-13 · Owner: multiplayer

This records the datagram transport that carries a session between two peers. It is sans-I/O: it owns no sockets,
threads or clocks. The I/O layer (WS2f) hands it datagrams, the current time and paths, and sends what it produces.
The code lives in `mods/TopiaForge.Multiplayer/Transport/`, inside the provider only, and every type is internal.
The cryptography it uses is recorded in `MultiplayerTransportCrypto.md`.

WS2e landed in two pull requests:

1. **Core** (sections 1 to 9): packets, the handshake over datagrams, protected packets, recovery, the session
   stream, datagram channels, timers and the outbound budget.
2. **Paths and shaping** (sections 10 to 12): path validation for migration and relay-to-direct upgrade, the traffic
   classes with the shared uplink and its shedding, and the unauthenticated info query.

## 1. The I/O boundary

- `TransportPath` is an opaque path the I/O layer creates: a direct UDP endpoint, a LAN endpoint, or a relay channel.
  Its key bytes stay inside the transport and network layers; they never reach the engine, mods, logs or diagnostics.
- `TransportEndpoint` is one socket's worth of state:
  - `Receive(path, datagram, now)` takes one inbound datagram;
  - `Poll(now)` runs timers and the pacer;
  - `TryDequeueDatagram` yields outbound datagrams from a buffer pool, which the caller returns once sent;
  - `NextTimeoutMicros` says when `Poll` must run again;
  - `Connect` starts an outbound connection, and `TryAccept` yields inbound ones once their handshake completes;
  - `SetInfo`, `QueryInfo` and `TryReceiveInfo` run the info query (section 12).
- Randomness (connection ids, ephemeral keys, cookie secrets, path challenges, query ids) comes from an injected
  source, so a test with a seeded source reproduces every byte.
- Time is engine microseconds from a monotonic clock, as everywhere in the session engine.

## 2. Datagrams

Every datagram is at most 1,200 bytes. The game's Mono cannot set the don't-fragment flag through managed sockets
(`MultiplayerMonoSpike.md`, section 1), so the transport never relies on path MTU discovery.

The first byte classifies a datagram, compatible with RFC 7983 so STUN and TURN can share the socket:

| First byte | Datagram | Handling |
| --- | --- | --- |
| `0x00`–`0x03` | STUN | Passed to the NAT layer (WS7) |
| `0x40`–`0x4F` | TURN ChannelData | Passed to the NAT layer (WS7) |
| `0xE0` | Initiation | Handshake message 1, padded to exactly 1,200 bytes |
| `0xE1` | Retry | Stateless cookie challenge |
| `0xE2` | Response | Handshake message 2 |
| `0xE3` | Completion | Handshake message 3 |
| `0xE4` | InfoQuery | A browser's question, padded to exactly 1,200 bytes |
| `0xE5` | InfoReply | The host's answer |
| `0xE8` | Protected | Authenticated, encrypted frames |
| Anything else | — | Dropped |

## 3. Handshake

The handshake is `NoiseHandshakeXX`. Its prologue is the ASCII label `TopiaForge transport 1` followed by the
initiator's connection id, so the transcript authenticates the id the initiator chose.

| Datagram | Layout |
| --- | --- |
| Initiation | `E0`, version `1`, initiator connection id (8), cookie length (1), cookie (0 or 16), Noise message 1, zero padding to 1,200 bytes |
| Retry | `E1`, initiator connection id (8), cookie (16) |
| Response | `E2`, initiator connection id (8), Noise message 2. Its encrypted payload carries the responder's connection id and initial stream window. |
| Completion | `E3`, responder connection id (8), Noise message 3. Its encrypted payload carries the initiator's initial stream window. |

- **Connection ids** are 8 random bytes, one chosen by each side, QUIC-style. A packet names the receiver's id, so a
  relayed peer and a direct peer can never collapse into one, and a connection survives a change of path
  (section 10). The responder's id travels encrypted and authenticated.
- **Retransmission.** The initiator retransmits the Initiation with exponential backoff from 250 ms, capped at 2 s.
  The responder answers a duplicate Initiation (same initiator id and ephemeral key) with the Response it cached, and
  never retransmits on its own.
- **Confirmation.** The initiator becomes established on the Response, and the responder on the Completion. The
  responder sends a PING as soon as it is established. The initiator resends the Completion, with the same backoff,
  until the first authenticated packet from the responder confirms it arrived. A duplicate Response also prompts a
  Completion resend, and a duplicate Completion prompts the responder to send a packet.
- **Windows.** Each side's initial stream window travels in its encrypted payload. A window that cannot hold one
  maximum frame and its length prefix could never deliver that frame, so the handshake fails.
- **Round trip.** Each side times its handshake message against the reply and takes that as its first RTT sample,
  unless the message went more than once (Karn's rule). The first probe timeout then rests on a measured round trip
  rather than the 250 ms default.
- **Pinning.** `Connect` may name the responder's expected static key; a different key ends the attempt.
- **Timeout.** A handshake that has not completed after 5 s is abandoned on either side.
- **Version.** An unknown version is dropped. The info query (section 12) reports the version before a join.

### Abuse controls

- An Initiation must be exactly 1,200 bytes. Every reply is smaller than the datagram it answers, so the
  transport can never amplify traffic toward a spoofed source.
- **Rate limits.** A source may start 10 handshakes a second, and the endpoint 200 in total. Excess Initiations are
  dropped before any Diffie-Hellman work.
- **Cookies.** `CookieGuard` answers an Initiation with a stateless Retry when the endpoint is under load: when it
  already holds 64 half-open handshakes, or when recent Initiations exceed half the global rate. It can also be set
  to always demand a cookie.
  - A cookie is the first 16 bytes of HMAC-SHA256 over, in this order: the version byte, the two-minute time slot (8
    bytes, little-endian), the initiator's connection id (8 bytes, little-endian) and the path key. It is keyed by a
    secret that rotates every two minutes; the previous secret stays valid for one slot.
  - The Retry echoes the initiator's random connection id, so an off-path attacker cannot forge one. An initiator
    accepts at most one Retry per attempt.
- **Capacity.** At most 256 half-open handshakes and a configured number of established connections, 64 by
  default. Half-open handshakes do not count against the connection limit, so when several complete at once, the
  endpoint turns away each one past the limit with CLOSE (`Overloaded`). The engine's admission decides who may join a
  session; this cap only bounds transport state.
- **Paths.** Handshake messages must come from the path the handshake started on, even genuine ones. Protected packets
  may arrive on any path the endpoint allows (section 10).
- **Reporting.** A responder's half-open handshake is unauthenticated traffic, so its failures are counted in the
  endpoint's statistics rather than reported one by one. An initiator's failure is always reported.

## 4. Protected packets

`E8`, the receiver's connection id (8), the packet number (8, little-endian), then the frames sealed with
ChaCha20-Poly1305 and a 16-byte tag. The 17 header bytes are the associated data, and the packet number is the
explicit nonce of the direction's `CipherState`.

- Packet numbers start at zero in each direction and never repeat. A retransmission travels in a new packet.
- The receiver checks its `ReplayWindow` before decrypting and records the number only after the packet
  authenticates, so a forgery never moves the window. Anything that fails is dropped silently.
- Each packet carries at most 1,167 bytes of frames (1,200 minus 33 bytes of header and tag).

## 5. Frames

Frames are a type byte and varint fields. Every frame except ACK, CLOSE, PADDING and the two path frames asks for an
acknowledgement. PADDING and the path frames only probe: a packet of nothing else never moves a connection.

| Type | Frame | Fields |
| --- | --- | --- |
| `0x00` | PADDING | — |
| `0x01` | PING | — |
| `0x02` | ACK | largest acknowledged, ack delay (µs), range count, first range, then gap and length per range (QUIC encoding, at most 32 ranges) |
| `0x03` | CLOSE | reason code |
| `0x10` | STREAM | offset, length, bytes of the session stream |
| `0x11` | WINDOW | the highest stream offset the receiver accepts |
| `0x20` | DATAGRAM | channel, sequence, length, bytes |
| `0x30` | PATH_CHALLENGE | 8 random bytes |
| `0x31` | PATH_RESPONSE | the 8 bytes of the challenge it answers |

An unknown frame type, a truncated frame or a field out of bounds is a protocol error: the receiver sends CLOSE and
the connection ends.

## 6. Recovery

Recovery follows RFC 9002.

- **Acknowledgement.** `AckTracker` keeps the received packet numbers as at most 32 ranges. It acknowledges
  immediately after two ack-eliciting packets, or within 25 ms of the first. ACKs ride along with outgoing data
  whenever there is any.
- **RTT.** `RttEstimator` keeps the latest, smoothed and minimum round trip and its variation. It starts from an
  initial 250 ms until its first sample, which normally comes from the handshake (section 3). It subtracts the
  peer's reported ack delay, capped at 25 ms, when that stays above the minimum.
- **Loss.** `LossDetector` declares a packet lost when one three packet numbers newer has been acknowledged, or when
  it is older than 9/8 of the larger of the smoothed and latest round trip. A probe timeout of smoothed RTT plus
  the larger of 4 × variation and 1 ms, plus the 25 ms maximum ack delay, doubles after each expiry and sends up to
  two probes.
- **Retransmission.** Lost STREAM ranges go back in the queue and travel in new packets. A lost WINDOW resends the
  current value. DATAGRAM frames are never retransmitted.
- There is no congestion window yet. The outbound budget (sections 8 and 11) limits the rate.

## 7. Channels

The engine's three channels map onto two mechanisms.

- **Session (reliable, ordered).** One byte stream per direction. Each engine frame becomes a varint length followed
  by its bytes. STREAM frames carry ranges of the stream, which is how a frame of up to the engine's 1,122,304-byte
  maximum spans datagrams.
  - **Flow control.** The receiver advertises with WINDOW the highest offset it accepts: its consumed offset plus
    2 MiB. It keeps received bytes in 16 KiB chunks and copies a frame out only when the reader takes it, so it never
    reserves memory ahead of the bytes themselves and holds at most its window. A sender that passes the window ends
    the connection with CLOSE (`FlowControl`). A receiver sends WINDOW once its reader has consumed a quarter of the
    window.
  - **Frame lengths.** Each frame's length is checked as soon as it arrives. One longer than the largest frame, or
    not a minimal varint of at most five bytes, is a protocol error.
  - **Process cap.** The endpoint refuses to buffer beyond 32 MiB across all connections. A packet whose stream bytes
    would pass the cap goes unacknowledged, and the sender retransmits it once there is room. A connection holding
    less than an equal share of the cap is always admitted, so peers that fill the cap cannot stall the others; the
    endpoint therefore never holds more than twice the cap.
  - **Backlog.** The sender's backlog of unacknowledged stream is capped at 16 MiB. Exceeding it closes the connection
    with CLOSE (`Overloaded`) rather than growing without bound. Once a connection drains or closes, its send buffers
    are released at once; only received frames stay readable.
  - Session frames may be queued during the handshake; they leave once the peer's window is known.
- **Realtime and Voice (unreliable, sequenced).** Each frame is one DATAGRAM frame with a per-channel sequence. The
  receiver delivers only frames newer than the last one it delivered on that channel. A frame must fit one packet
  (1,153 bytes of payload); a larger one is refused when sent, as is any datagram before the handshake completes.
  Up to 64 frames per channel wait for their class's share and the pacer, and up to 128 received datagrams wait for
  the reader; past either limit the oldest is dropped.

## 8. Timers and the outbound budget

| Timer | Value |
| --- | --- |
| Keepalive | A PING after 1 s without sending, which also refreshes NAT bindings |
| Stall | 3 s without an authenticated packet: the link reports `Stalled` |
| Idle timeout | 10 s without an authenticated packet: the connection closes |
| Handshake timeout | 5 s |
| Path validation | Up to 3 challenges, a probe timeout apart and at least 250 ms |
| Close drain | 3 probe timeouts after sending CLOSE |

- **Budget.** Each connection paces its outbound traffic with a token bucket: 256 kbit/s sustained (32 KiB/s),
  bursting to 512 kbit/s for up to 2 s. ACK, CLOSE and path packets are exempt. Probes skip the gate, so an exhausted
  budget never delays loss recovery, but they are charged, so they cannot raise the rate. This is the provider-side
  downstream ceiling `MultiplayerTransportOptions.md` requires; section 11 divides it between the traffic classes and
  adds the host-wide uplink.
- **Output.** The endpoint queues at most 1,024 datagrams for the I/O layer. Past that it drops new ones, as a
  congested link would, and recovery resends what mattered.
- **Graceful close.** `Close` stops new sends. The connection keeps retransmitting until the session stream is fully
  acknowledged, or the idle timeout passes, then sends CLOSE. A received CLOSE ends the connection at once; frames
  already delivered stay readable.

## 9. Privacy

- Paths are opaque keys. The engine sees a peer identity, the lowercase hex of the peer's static key, and never an
  address.
- **Relay-only.** With `RelayOnly` set, the player's "hide my IP" choice, the endpoint uses only relayed and LAN paths.
  It never connects, probes or queries over a direct Internet path, and drops every transport datagram that arrives
  from one, so nothing it sends ever goes there. STUN and TURN still reach the NAT layer, which needs them to reach
  the relay.
- Diagnostics name connections by a counter, never by path or address.
- No frame carries a free-text message, so nothing a mod or a player typed can travel outside the session stream.
- Keys follow the hygiene rules of `MultiplayerTransportCrypto.md`. A connection zeroes both directions' keys when it
  closes, and a draining connection keeps them only to answer stray packets with CLOSE. Disposing the endpoint ends
  every connection and zeroes the static key and both cookie secrets. A rotating cookie secret is zeroed as it is
  discarded.

## 10. Paths

A connection sends on one validated path. Authenticated packets count from any path the endpoint allows, but the
connection moves only after validating the new path, as QUIC does. A PATH_CHALLENGE with eight random bytes goes out
there, padded to a full datagram, which also shows the path carries 1,200 bytes. A PATH_RESPONSE echoing those bytes,
arriving on any path, proves the peer receives there.

- **The peer moves.** When the peer's newest packet with more than probing frames arrives from another path, as when
  its NAT rebinds, the connection validates that path and moves once it is answered. A newer packet from the current
  path cancels the check.
- **Upgrades.** `ProbePath` tries a path this side found, as when a relayed connection finds a direct one (WS7). The
  connection moves when the peer answers; the peer then sees this side's traffic arrive on the new path and follows
  the same way.
- **Answers.** A PATH_RESPONSE goes back on the path its challenge arrived on, never larger than the datagram that
  carried the challenge. A datagram earns one answer however many challenges it packs, so the answers never outweigh
  it; a correct peer sends one challenge per packet.
- **Limits.** At most two paths are tried at once, each with up to three challenges, after which it is abandoned and
  the connection stays where it was. A stray copy of a genuine packet can therefore draw at most three challenges
  toward its source.
- **After a move** the RTT estimate starts again from the validation's own round trip, and diagnostics report
  `PathChanged` by connection number.
- Connection ids do not change on a move. An observer on both paths could link them; the threat the design answers is
  the session's peers and mods, which never see a path at all.

## 11. Traffic classes and the uplink

Each connection's 256 kbit/s budget (section 8) is shared by three classes:

| Class | Share |
| --- | --- |
| Voice | Capped at 96 kbit/s, the host's downstream rate. A client's endpoint sets 32 kbit/s upstream. |
| Realtime | Capped at 64 kbit/s |
| Session | No cap of its own: whatever the others leave, and at least 96 kbit/s |

- **Classes.** Voice and Realtime have token buckets two datagrams deep. A datagram waits in its channel's queue
  until its class may send it. Session takes the rest of the pacer's budget, so it uses the whole connection while the
  datagram classes are idle. Packet overhead comes out of every class, so under full datagram load Session's payload
  runs a little under its share.
- **Uplink.** All connections share a host-wide budget, 4,000 kbit/s by default, with a tenth of a second of burst.
  The connection's own pacer gates first, then the uplink. The poll order rotates, so no connection always meets a
  spent uplink last.
- **Shedding.** While connections keep finding the uplink spent, the shedding level rises one step a second: level 1
  halves voice, level 2 stops it, and level 3 also asks the engine to lower its delta rate (WS3 connects
  `SheddingLevel` to the engine). Three quiet seconds lower it a step. A class at a rate of zero sends nothing at all,
  whatever its bucket still holds.

## 12. Info query

Before joining, a browser asks a host about its session without a handshake.

| Datagram | Layout |
| --- | --- |
| InfoQuery | `E4`, version, query id (8, random), zero padding to 1,200 bytes |
| InfoReply | `E5`, the responder's version, query id (8), info length (2, little-endian), info (at most 1,188 bytes) |

- A host answers only while `SetInfo` has given it info. The info is public and travels unauthenticated, so it must
  never name an address; WS2f decides its content.
- A reply to a query of another version carries the responder's version and no info, so the browser can say why it
  cannot join.
- Queries share the handshake's budgets: 10 a second per source and 200 per endpoint.
- The browser keeps at most 64 queries outstanding, for 5 s each, and accepts a reply only from the path the query
  went to and with its id. Replies queue for `TryReceiveInfo` with the measured round trip.
- A query's expiry is one of the endpoint's timers, so a browser with no connections still wakes to forget it, and a
  reply is checked against the lifetime when it arrives, however rarely the endpoint is polled.

## 13. Tests

- **Same bytes.** `VirtualDatagramNetwork`, in the test project, carries the endpoints' own datagrams
  deterministically. Each node is reachable directly, through a relay and on the LAN, and its direct address can be
  rebound as a NAT does. The network models latency, jitter, loss, duplication, partitions, unreachable paths and the
  1,200-byte limit, and polls each endpoint exactly when its next timer falls due. With seeded random sources, two
  runs exchange identical datagrams at identical times, and a poll that finds nothing to do fails the test.
- **Codecs and parts.** `TransportCodecTests` covers the datagram layouts, the handshake payloads and every frame,
  including malformed input. `TransportComponentTests` checks `AckTracker`, `RttEstimator` and `LossDetector` against
  the RFC 9002 arithmetic, plus the streams, the pacer, the datagram channels, `CookieGuard` and `HandshakeLimiter`.
- **Handshake.** `TransportHandshakeTests` covers keys and transcript binding, pinning, the loss of each message,
  cookies (under load, on demand and bound to the path), one Retry per attempt, budgets and capacity, timeouts,
  messages from another path, undersized windows, and replies that never outweigh what provoked them.
- **Delivery and lifecycle.** `TransportDeliveryTests` and `TransportLifecycleTests` cover delivery under loss,
  duplication and reordering, of frames up to the engine's 1,122,304-byte maximum, within the outbound budget. They
  also cover flow control under loss, the process cap and its fair shares, sequenced channels, probes for tail loss
  and the backlog cap. Then keepalive, stall and idle timeout, graceful close, each protocol violation, and replayed,
  forged and misaddressed packets. `RawTransportPeer` speaks the protocol by hand to send what a correct peer never
  would.
- **Paths, classes and info.** `TransportPathTests` covers NAT rebinding, relay-to-direct upgrade, unanswered probes,
  relay-only privacy, stray copies, echoed challenges, response sizes, and a datagram packing four challenges.
  `TransportShapingTests` measures the class caps, Session's share under datagram load and alone, a rate of zero, and
  three connections sharing a small uplink fairly while it climbs every shedding level, voice first.
  `TransportInfoTests` covers the query round trip, sizes, matching, a version mismatch, the rate limits, and expiry
  without a poll.
- **End to end.** `TransportEngineTests` runs the provider's own session engine, host and client, over the transport
  on a lossy network: the client joins, a command round-trips, and leaving closes both connections normally.
- **Fuzzing.** `TransportFuzzTests` sends 4,000 seeded hostile datagrams against a live session, over every kind of
  path: random bytes, typed random bytes, forged info replies, and captured datagrams flipped, cut, extended or
  replayed. Oversized ones are included. The session must stand, stay on its paths and still deliver. A seeded run
  must also repeat every byte.
