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

## Threading and settle points

Every API call, callback and task completion runs on the thread that created the engine. Other threads may cancel a
token; the cancellation is queued and handled at the next settle point.

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
  nesting another canonical command.

A client runs owner-predicted handlers in a **prediction scope** with the same rules, except that only state and object
values change; events are dropped, and spawn, despawn, transfer and `Apply` return `NotAuthoritative`.

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

Caller cancellation completes the task with `Cancelled`, removes a submission that has not been sent yet, and
reconciles. A submission already on the wire still executes; the client simply stops waiting for it.

## Replication

- **States:** the host sends the latest committed bytes and version. `Immediate` states go out at the next flush;
  `Coalesced` states wait for the downstream boundary (`DownstreamRateHz`: 10 Hz in production, every pump in the rig)
  and respect `MaximumUpdatesPerSecond`. A flush that carries a result for a connection sends that connection every
  pending change first, so a confirmation never overtakes its effects.
- **Objects:** spawn and despawn are ordered messages, and a spawn carries the latest state at flush time. Changes are
  coalesced like states. A change for an object despawned in the same flush is dropped. Clients keep a bounded
  tombstone set per session so a despawned id can never come back.
- **Late join and replacement:** a joining connection receives `SnapshotBegin`, every state and object, and
  `SnapshotEnd`. The client applies the snapshot as an exact replacement — objects missing from it are despawned and
  notified — then reports `Ready` and sends `ReadyAck`.
- **Presentation events** go only to connections that have acknowledged `Ready` for the current session, and are
  selected per recipient with `PresentationTarget.Selects`. The host's own player receives them at the settle point
  after they commit. A solo command's events are therefore presented before its confirmation is observed, as they
  are on a remote client, where the events and the result arrive in one frame.
- **Unregistered ids:** a client keeps canonical values for states and object types it has not registered and adopts
  them when it registers.

## Participants and connections

The host maps each link's authenticated identity to a participant. A participant whose link closes is marked
disconnected at once, and objects it owned return to server ownership. Its record stays for a 30-second grace period,
so a returning identity keeps its participant id and submission sequence. `JoinAccepted` tells the client the next
sequence the host will accept, so a restarted client cannot collide with one the host has already processed.

A link that stalls without closing moves the client to `Reconnecting`; canonical state is frozen until the link
recovers or closes.

## Protocol

Frames travel on the link's reliable-ordered *Session* channel. The *Realtime* and *Voice* channels are reserved for
later workstreams. A frame is a sequence of envelopes `[type varint][length varint][body]`, at most 1 MiB + 64 KiB, and
a host flush ends with `Commit` in its last frame.

Types `0x01`–`0x3F` are critical: an unknown one is a protocol violation. Types `0x40`–`0x7F` are ignorable: an
unknown one is skipped by its length. Owner ids and wire ids are interned per connection and direction: a reference is
a varint, and `0` introduces a new string that takes the next index. Nobody maintains numeric ids.

| Type | Direction | Message |
| --- | --- | --- |
| `0x01` | client → host | `JoinRequest`: protocol version, display name, registered contracts, opaque content descriptor |
| `0x02` | host → client | `JoinAccepted`: session id, seed, epoch, participant, host participant, tick, step rate, next sequence |
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
| `0x30` | both | voice, reserved for WS2g |
| `0x40`, `0x41` | both | `ClockSync`, `ClockSyncReply` |

Submissions carry the session epoch. The host answers a submission from an earlier epoch, or one whose sequence does
not increase, with `Conflict`. Malformed frames, bound violations and unknown critical types end that connection with
`ProtocolMismatch`; the host never throws into its pump because of what a client sent.

Logs and diagnostics name participants, owners and wire ids — never addresses. Links expose an opaque identity, not an
endpoint.

## The rig on the engine

`MultiplayerTestRig` keeps its public API. Each node owns a `SessionEngine`, and `VirtualEngineTransport` connects them
with the production channel contract:

- per-direction FIFO;
- loss becomes a retry delay that also holds later frames (head-of-line);
- duplicates are discarded by sequence;
- a closed link drops what was in flight.

`Advance()` pumps every engine at the current tick — the end-of-tick flush — then moves the tick, delivers due frames,
and pumps again. `Flush()` advances until no frame is in flight and no engine has unsent or undelivered work.

Behaviour that changes on purpose:

- **Ordered delivery.** The four tests that asserted unordered delivery are rewritten for ordered semantics, and
  stale, duplicate and replay robustness moves to `EngineWhiteBoxTests`.
- **Subscriptions survive replacement.** A subscription made before `StartNewSession` sees the old objects despawn and
  the new ones spawn.
- **Stable node identity.** `MultiplayerTestNode.ParticipantId` reports the id the rig assigned, because a client
  learns its participant only when the host accepts it.
