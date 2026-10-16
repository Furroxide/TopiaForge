# Multiplayer session engine — design record

Status: Implementing (WS2a/WS2b of the live-multiplayer plan) · Date: 2026-10-12 · Owner: multiplayer

This records how the Unity-free session engine behaves, so the test rig, the solo provider and the live transport
agree on one set of rules. The public contract it implements is `IMultiplayerSession` in
`src/TopiaForge.Mods.Multiplayer`; this document covers what the contract leaves to the provider.

## Shape

- **Source:** `src/TopiaForge.Multiplayer.Engine/`, namespace `TopiaForge.Multiplayer.Engine`, every type `internal`.
- **Hosts:** `TopiaForge.Multiplayer.Engine.props` links the source into `TopiaForge.Mods.Testing.dll` (the rig) and,
  from WS2c, `TopiaForge.Multiplayer.dll` (the provider).
- **Compile check:** `TopiaForge.Multiplayer.Engine.csproj` (`IsPackable=false`) compiles the same files on their own,
  and is the assembly the engine white-box tests reach through `InternalsVisibleTo`. It is neither a package nor a
  release harness, so the counts stay at 11 and 7.
- **Dependencies:** the base class library and the `TopiaForge.Mods.Multiplayer` SDK only. No Unity, no sockets, no
  third-party code.

One engine instance is one process's view of one session line. Facades (`EngineSessionFacade`, one per owner mod) give
mods an `IMultiplayerSession`; every facade of an engine shares its snapshot, clock and tables.

## Roles

| Role | Sides | Local participant | Links |
| --- | --- | --- | --- |
| Host, interactive (solo or listen host) | Client and Server | The host player | Zero or more accepted |
| Host, headless (rig dedicated server) | Server | None | Zero or more accepted |
| Client | Client | Assigned by the host | One |

Solo is a host that has accepted no links. Opening a solo session to other players accepts links on the same engine,
so nothing restarts.

### Role changes

One engine serves a process for its whole life, so the registrations, facades and subscriptions mods hold stay valid
as the process joins other hosts and comes back. To mods, each change looks like the session replacement they already
handle: the current session's token is cancelled, values return to their declared defaults, objects despawn, and the
snapshot names the new session.

| Call | From | To |
| --- | --- | --- |
| `SwitchToClient(placeholder)` | An interactive solo host with no connections | A client waiting for `Connect`, in `Connecting` |
| `SwitchToSolo(start)` | A client, connected or not, its session ended or not. A connected client says `Leave` first. | A fresh solo session, `Ready` |
| `CloseConnections(reason, message)` | A host with connections | The same session, played on alone |

- **Closing connections** ends each client's session with the reason. Departed players leave the participant list at
  once rather than after their grace period, and the host's values and objects stay.
- **Returning to solo automatically.** `EngineOptions.AfterClientSessionEnds` decides what a client does when its
  session ends: rejected, kicked, ended by the host, or failed on a protocol error. The provider sets it to go back to
  solo play, so mods see one more replacement rather than an ended session. Without it, as in the rig, the session
  ends as before.
- **Limits.** None of the three runs inside a transaction. A host serving clients must close them before joining
  elsewhere.
- **Join policies** see the link a request came over (`JoinRequestContext.Link`). Every link exposes a
  `ChannelBinding` that both ends share and nobody else knows: the secure transport's handshake hash, or a value the
  rig's virtual transport invents per link. A provider's policy checks proofs bound to it, such as a password proof,
  without knowing the link's concrete type.

## Threading and settle points

Every API call, callback and task completion runs on the thread that created the engine. Other threads may cancel a
token; the cancellation is queued and handled at the next settle point. So is a cancellation on the owner thread that
fires in the middle of an engine operation, such as a codec decoding an inbound frame, so it never interrupts that
operation.

A **settle point** is the end of an outermost engine operation: a top-level API call, one processed inbound frame, one
fixed step, or the end of `Pump`. At a settle point the dispatcher drains, in this order:

1. replicated-object notifications (spawned, changed, despawned);
2. replicated-state notifications;
3. the session snapshot notification;
4. presentation events;
5. command and input confirmations.

Callbacks may call the engine again. Their work runs as new top-level operations and queues further notifications
behind the current ones, so the drain ends only when the queue is empty. Confirmations complete synchronously, so an
`await` continuation on the main thread runs at the settle point that completed it.

**State notifications report the visible value.** At each settle point a state notifies its subscribers once if its
visible `(version, bytes)` differs from what it last reported. A prediction that the host later confirms unchanged
therefore notifies once, and a rollback notifies once. Object notifications are canonical only.

## Time and steps

`Pump(nowMicros)` drives the engine. The host runs fixed steps at `StepRateHz`: production uses 20 Hz and the rig uses
its 60 ticks per second, so one rig tick is one step. Step `k` is due at `k * 1_000_000 / StepRateHz` microseconds,
computed per step so 60 Hz never accumulates rounding error. A pump runs at most four overdue steps and skips the rest.

The tick is a step counter that keeps running across session replacement. Only the host runs `SubscribeTick` handlers.
A client estimates the host tick from the tick stamped on each frame and the elapsed time since it arrived.

## Transactions

Canonical work runs inside a `TransactionScope`: a command, an object input, one tick handler, or a top-level host call
such as `Update`, `Apply`, `SpawnObject` or `PublishPresentation` made outside any scope.

- State and object changes apply to the scope's working values, and each `Update` or `Apply` increments the version.
- `SpawnObject`, `DespawnObject`, `TransferOwnership` and events are **deferred to commit**. A spawned handle is
  visible inside the scope; if the scope aborts it reports `IsSpawned == false` and nobody is told about it. This is
  the "spawn command" pattern that the loopback provider rejected.
- A handler that returns a failure or throws aborts the scope. Every touched value is restored and buffered events are
  discarded. `OperationCanceledException` maps to `Cancelled`; any other exception maps to `Unknown`.
- Registrations and `SubmitAsync` are rejected with `InvalidState` inside a scope, which also stops a handler from
  nesting another canonical command. Replacing the session, removing a participant and ending the session throw
  `InvalidOperationException` inside a scope, before they change anything.
- `PublishPresentation` inside a command buffers the event like `Emit`, and `Sender` and `Others` are relative to the
  participant who sent the command.

A client runs owner-predicted handlers in a **prediction scope** with the same rules, except that only state and object
values change; events, whether emitted or published, are dropped and report success, and spawn, despawn, transfer and
`Apply` return `NotAuthoritative`.

## Reconciliation

A client keeps a confirmed and a current copy of every state and object. Submissions take sender-scoped sequences from
one counter shared by commands and object inputs, so prediction and the canonical run see the same `Sequence`, seed and
random stream.

The host stamps each flush to a connection with a `Commit` carrying the highest submission sequence it has processed
for that connection. Everything the flush contains — results, deltas, spawns — applies before the client reconciles,
and reconciliation happens only at a `Commit`:

1. current ← confirmed for every state and object;
2. pending predicted submissions with a sequence above the acknowledged one replay in submission order;
3. confirmations whose results arrived complete at the settle point.

A submission at or below the acknowledged sequence is already part of the confirmed values, so it never replays, even
if its result has not arrived. That makes a cumulative confirmation idempotent, which the rig asserted with
`highestCanonicalConfirmationSequence`.

A flush can span several frames, and the client may pump between them. Until the flush's `Commit` arrives its
confirmed values are only partly updated, so no reconciliation runs, whatever asked for it.

Caller cancellation completes the task with `Cancelled` at once. A submission that has not been sent is withdrawn and
its prediction rolls back. One already on the wire still executes, so its prediction keeps replaying until the commit
that covers it; the client only stops waiting for the result.

Sequences keep counting across session replacement and reconnects, so a late result from an earlier session never
matches a later submission. When a connection or session goes away, a submission whose result already arrived completes
with that result, because the host executed it; the rest complete as `Cancelled`.

## Replication

- **States:** the host sends the latest committed bytes and version. `Immediate` states go out at the next flush;
  `Coalesced` states wait for the downstream boundary (`DownstreamRateHz`: 10 Hz in production, every pump in the rig)
  and respect `MaximumUpdatesPerSecond`. A flush that carries a result for a connection sends that connection every
  pending change first, so a confirmation never overtakes its effects.
- **Objects:** spawn and despawn are ordered messages, and a spawn carries the latest state at flush time. Changes are
  coalesced like states. A change for an object despawned in the same flush is dropped. Clients keep a bounded
  tombstone set per session so a despawned id can never come back. When an object despawns, the client cancels its
  unsent inputs; sent ones wait for the host's result, because an input that despawned its own target succeeded. Inputs
  for a type whose registration was disposed fail with `NotFound` on the host and run no handler.
- **Update-rate history:** the per-connection record of when a rate-limited value was last sent ends with the value:
  on despawn, on unregistration, and for everything at session replacement.
- **Late join and replacement:** a joining connection receives `SnapshotBegin`, every state and object, and
  `SnapshotEnd`. The client applies the snapshot as an exact replacement — objects missing from it are despawned and
  notified — then reports `Ready` and sends `ReadyAck`.
- **Presentation events** go only to connections that have acknowledged `Ready` for the current session, and are
  selected per recipient with `PresentationTarget.Selects`. The host's own player receives them at the settle point
  after they commit. A solo command's events are therefore presented before its confirmation is observed, as they
  are on a remote client, where the events and the result arrive in one frame.
- **Unregistered ids:** a client keeps canonical values for states and object types it has not registered and adopts
  them when it registers. Unregistering a state keeps its canonical value the same way, so registering it again
  restores it.
- **A value that does not decode** with the client's codec ends the session as a protocol mismatch, whether it arrives
  in a delta or a snapshot or is the canonical value a late registration adopts — a state no less than an object, which
  behaved differently before. When that happens partway through a snapshot or an adoption, the rest of it is dropped
  too, so nothing of the host's lands in the solo session the provider returns to.

## Participants and connections

The host maps each link's authenticated identity to a participant. A participant whose link closes is marked
disconnected at once, and objects it owned return to server ownership. Its record stays for a 30-second grace period,
so a returning identity keeps its participant id and submission sequence. `JoinAccepted` tells the client the next
sequence the host will accept, so a restarted client cannot collide with one the host has already processed. It also
says whether the host kept the player's record, within the grace or across an in-place replacement, rather than
creating one; only then does the client keep its listener mutes (`MultiplayerVoice.md`, section 2.5).

The host keeps at most twice `MaximumParticipants` records, connected or within their grace period, and never more
than the 256 a snapshot and a peer accept. A new identity that would exceed the limit takes the place of the
longest-departed record; if every record is connected, the join is refused as full. A record that goes, whether its
grace period ended, a newer player took its place or the host closed every connection, takes the rate limiters it left
on commands and objects with it, so they never outnumber the participants the host knows.

Each joined connection receives the participant list at most once per flush, however many joins, departures or quality
changes happened since the last one. A round-trip sample comes only from the echo of the latest clock probe, so a client
can neither steer its reported ping nor make the host resend the list at will.

A link that stalls without closing moves the client to `Reconnecting`; canonical state is frozen until the link
recovers or closes.

## Protocol

Frames travel on the link's reliable-ordered *Session* channel. Voice datagrams travel on the unreliable, sequenced
*Voice* channel (`MultiplayerVoice.md`, part 2), and *Realtime* is reserved for a later workstream. A frame is a
sequence of envelopes `[type varint][length varint][body]`, and a host flush ends with
`Commit` in its last frame. A sender ends a frame once a message takes it past 64 KiB, so a frame is at most 64 KiB, one
1 MiB payload and 8 KiB for that message's envelope and fields.

Types `0x01`–`0x3F` are critical: an unknown one is a protocol violation. Types `0x40`–`0x7F` are ignorable: an
unknown one is skipped by its length. Owner ids and wire ids are interned per connection and direction: a reference is
a varint, and `0` introduces a new string that takes the next index. Once a table holds 4,096 strings, new ones travel
inline after the reference `4097` and take no index. Nobody maintains numeric ids.

| Type | Direction | Message |
| --- | --- | --- |
| `0x01` | client → host | `JoinRequest`: protocol version, display name, registered contracts, opaque content descriptor |
| `0x02` | host → client | `JoinAccepted`: session id, seed, epoch, participant, host participant, tick, step rate, next sequence, whether the host kept the player's record, opaque admission report |
| `0x03` | host → client | `JoinRejected`: end reason, message, opaque admission report |
| `0x04`, `0x05` | host → client | `SnapshotBegin` (epoch), `SnapshotEnd` |
| `0x06` | client → host | `ReadyAck` (epoch) |
| `0x07` | host → client | `ParticipantsChanged` |
| `0x08` | host → client | `SessionEnded` (reason) |
| `0x09` | host → client | `SessionReplaced` (session id, seed, epoch) |
| `0x0A` | host → client | `Commit` (tick, acknowledged submission sequence) |
| `0x0B` | client → host | `Leave` |
| `0x10`, `0x11` | both | `Command` / `CommandResult` |
| `0x12`, `0x13` | both | `ObjectInput` / `ObjectInputResult` |
| `0x14` | host → client | `StateDelta` |
| `0x15`–`0x17` | host → client | `ObjectSpawned`, `ObjectChanged`, `ObjectDespawned` |
| `0x18` | host → client | `PresentationEvent` |
| `0x20` | both | `SystemMessage`, reserved for WS3 |
| `0x30` | host → client | `VoiceRoster`: each speaker index, its participant, and whether the host muted it |
| `0x31` | client → host | `VoiceMutes`: whom the client's player does not want to hear, or everyone |
| `0x40`, `0x41` | both | `ClockSync`, `ClockSyncReply` |

Submissions carry the session epoch. The host answers a submission from an earlier epoch, or one whose sequence does
not increase, with `Conflict`. Malformed frames, bound violations and unknown critical types end that connection with
`ProtocolMismatch`; the host never throws into its pump because of what a client sent.

Logs and diagnostics name participants, owners and wire ids — never addresses. Links expose an opaque identity, not an
endpoint.

## The rig on the engine

`MultiplayerTestRig` keeps its public API. Each node owns a `SessionEngine`, and `VirtualEngineTransport` connects them
with the production channel contracts:

- **Session:** per-direction FIFO; loss becomes a retry delay that also holds later frames (head-of-line); duplicates
  are discarded by sequence.
- **Realtime and Voice:** one datagram per frame; loss is final; a datagram older than one already delivered on its
  channel is dropped, duplicates included; nothing waits behind session frames; a path that is down loses datagrams
  rather than holding them; a datagram the link cannot carry in time is shed and counted, never queued: one whose lane
  is already more than three ticks behind, or, while the session lane is that far behind, one past its channel's share
  of the link (Voice 96 and Realtime 64 of every 256), as the real transport drops datagrams first
  (`MultiplayerVoice.md`, section 2.8).
- **Every channel** shares the link's rate: datagrams serialize in their own lane, as the real transport writes them
  first in every packet, but their bytes push later session frames back. Both lanes keep time in millionths of a tick
  and round only the tick a frame is delivered in, so a small frame costs its bytes rather than a whole tick. A closed
  link drops what was in flight.
- **Reading:** a link returns waiting datagrams before session frames and holds at most 128 of them, dropping the
  oldest, as the real connection does.

`Advance()` pumps every engine at the current tick — the end-of-tick flush — then moves the tick, delivers due frames,
and pumps again. `Flush()` advances until no frame is in flight and no engine has unsent or undelivered work.

Behaviour that changes on purpose:

- **Ordered delivery.** The four tests that asserted unordered delivery are rewritten for ordered semantics, and
  stale, duplicate and replay robustness moves to `EngineWhiteBoxTests`.
- **Subscriptions survive replacement.** A subscription made before `StartNewSession` sees the old objects despawn and
  the new ones spawn.
- **Stable node identity.** `MultiplayerTestNode.ParticipantId` reports the id the rig assigned, because a client
  learns its participant only when the host accepts it.
