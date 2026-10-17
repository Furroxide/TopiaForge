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
creating one; only then does the client keep its participant list (below) and its listener mutes (`MultiplayerVoice.md`,
section 2.5).

The host keeps at most twice `MaximumParticipants` records, connected or within their grace period, and never more
than the 256 a snapshot and a peer accept. A new identity that would exceed the limit takes the place of the
longest-departed record; if every record is connected, the join is refused as full. A record that goes, whether its
grace period ended, a newer player took its place or the host closed every connection, takes the rate limiters it left
on commands and objects with it, so they never outnumber the participants the host knows.

Each joined connection receives the participant list at most once per flush, however many joins, departures or quality
changes happened since the last one. A round-trip sample comes only from the echo of the latest clock probe, so a client
can neither steer its reported ping nor make the host resend the list at will.

**A client's list.** Every host numbers its players itself, so a participant id names a player only on the host that
gave it. A client therefore shows the list a host sent only beside the ids that host gave it, its own player's and this
player's, which mark the rows `IsHost` and `IsLocal`. A host sends its list after the snapshot, and a large world can
put a second or more between `JoinAccepted` and the list. In that window:

- **A join that finds the host that sent the list, with this player's record kept,** keeps the list, since that host
  gives the same ids and they still name the same people. A reconnect within the grace does, into an in-place
  replacement too.
- **Any other join withholds it.** The snapshot lists nobody, and names no local or host participant, until the host the
  join found sends its own list: a join to another host, to this host after it restarted or let the record expire, or
  after a join to it went unanswered, which counts as a lost record (`MultiplayerVoice.md`, section 2.5). So does a join
  to another process under the same identity, such as a second copy of the game sharing one data folder, that gives
  this player another id.
- **A lost link never withholds or clears it.** While the link is down the list stays as the host last sent it, and no
  row is this player's until a host accepts it again. Clearing it would end every speaker's voice stream at each outage.

So the `IsLocal` and `IsHost` marks always come from the host that sent the list they label, and a client that reaches
`Ready` shows that host's own list, which arrives before the `Commit` that ends the join's snapshot.

When the new host's whole answer fits in one frame, its list arrives in the same pump as `JoinAccepted`, so no snapshot
lists nobody: a subscriber sees the list of before replaced by the new host's at once, and an id it held may now name
someone else. Every list a client lets go, at a join that withholds it or on going back to solo play, therefore moves
`ParticipantListGeneration` on. Whatever keeps something per participant id watches it, as the voice service does
with its playback streams (`MultiplayerVoice.md`, section 3.4).

`ParticipantListTests` and `ParticipantListProviderTests` check each case over a slow link, and the generation and the
provider's voice streams over a fast one, where the whole answer arrives at once. `ParticipantListFuzzTests` checks
every snapshot a client shows against scripted hosts, two processes under one identity among them.

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
| `0x02` | host → client | `JoinAccepted`: session id, seed, epoch, participant, host participant, tick, step rate, next sequence, whether the host kept the player's record, opaque admission report, join generation |
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
| `0x20` | both | `SystemMessage`: epoch, join generation, channel, payload, for the runtime's own channels (see Runtime support) |
| `0x21` | both | `SystemCredit`: epoch, join generation, channel, messages and bytes the receiver's owner consumed |
| `0x30` | host → client | `VoiceRoster`: each speaker index, its participant, and whether the host muted it |
| `0x31` | client → host | `VoiceMutes`: whom the client's player does not want to hear, or everyone |
| `0x40`, `0x41` | both | `ClockSync`, `ClockSyncReply` |
| `0x42` | client → host | `ReplacementReceived` (epoch): a session replacement reached the player (see Runtime support) |

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

## Runtime support

TopiaForge's runtime drives the provider's engine through the internal SPI
([MultiplayerRuntimeIntegration.md](MultiplayerRuntimeIntegration.md)). The engine's part of it lives in
`Core/SessionEngine.System.cs`, `Core/SessionEngine.Runtime.cs` and `System/`:

- **System channels.** `OpenSystemChannel` opens one of the runtime's channels, one owner each. `SystemMessage` carries
  the epoch, the connection's join generation, the channel and a payload of at most 32 KiB, reliably and in order with
  the session's traffic. A host sends to joined connections, players still synchronizing included; a client sends to its
  host, and hears a host without a player of its own as `InternalSystemMessage.HeadlessHostSender`. Unknown kinds are
  left unclaimed and credited as taken, never a violation, so a minor version can add one; an oversized payload, or a
  system message before joining, is one. Sending inside a transaction is refused.
- **Streams and generations.** The host gives every accepted connection a join generation in `JoinAccepted`. A message
  stamped with another epoch or generation is dropped and counted. A rejoin within the grace period keeps the session and
  its epoch but restarts every stream: each side drops what its channels held from the old one, counts what its lost
  link never sent, and reports a new generation, so the channels' owners start again from a baseline.
- **Windows and credits.** A host may have 1,024 messages and 1 MiB outstanding to each player on each channel, and a
  player 64 messages and 64 KiB to its host: messages sent but not yet consumed by the receiving owner. The receiver
  reports what it consumed in `SystemCredit`, cumulative per stream. A host's message to several players goes to each one
  with room and names the others it skipped; a message to one peer that would pass its window is refused with
  `RateLimited`. A peer that passes its window, or credits what it was never sent, breaks the protocol, and so does a
  Ready player whose window stays full for two minutes while the host waits to send.
- **Rate.** A joined player keeps to 256 messages a second over all channels, after a burst of 256; its host allows
  twice that, a burst of 1,024 and 512 a second, so arrival bunching never trips an honest player, and ends a player that
  floods. Only messages for the current streams count; stale ones have their own allowance and never end their sender.
- **Readiness holds.** `HoldReadiness` keeps a client that has the host's snapshot `Synchronizing`, without
  acknowledging, until the last hold is released. A host's holds change nothing. The engine records when holds first
  applied since the client was last Ready, however many replacements followed, the latest progress (a snapshot, a host
  system message, or the runtime's report) and how many replacements arrived, which the live join bounds. A host ends a
  connection that has not acknowledged its snapshot `ReadyTimeoutMicros` (8 minutes, `InternalReadinessLimits.HostSeconds`)
  after the join's acceptance, or after the replacement that took a Ready player out of the session; a client reports
  each replacement's arrival in `ReplacementReceived`, and the first report since that replacement starts the host's wait
  again, once, since a replacement can wait behind a full link. Replacements while the player is still joining start
  nothing over, and neither do that player's reports. The wait is longer than a joining player's whole allowance. A host
  without the message skips it as an ignorable type and counts from the replacement alone, as every host does for a
  client that never reports.
- **The host's words.** A pending submission that a session end cancels fails with the engine's own text, never the
  host's message.
- **World sources.** The facade's `TryGetNetworkObjectId`, `TryGetEntity`, `TryGetAvatar` and `TryGetPose` ask the
  runtime's sources, on the owner thread only, and answer no until one is set. A source that throws answers no and is
  reported.
- **The frame's end.** `FlushOutbound` sends what is queued without receiving or stepping; a host keeps its downstream
  cadence.
- **The local name.** `SetLocalDisplayName` renames a hosting engine's own player; every joined player hears it at the
  next flush.
