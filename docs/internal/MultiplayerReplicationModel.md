# Multiplayer replication model — design record

Status: Implementing (WS3.1, WS3.4, WS3.5, WS3.10 and WS3.12 of the live-multiplayer plan) · Date: 2026-10-25 · Owner: multiplayer

This records the Unity-free rules and data structures that automatic replication is built on: realms and network
classes, the execution model's routing tables, the entity table, transform quantization, snapshot interpolation,
relevancy, the replication scheduler, motion ownership, scene object keys, the spawn ring, request limits, presentation
routing and the host's rules for players' poses. They are pure and deterministic, so the Unity side (`NetworkWorld`, the service decorators, the
internal SPI and the `ModRuntime` wiring, in a later slice) drives them and every peer reaches the same answers. How the
session engine itself works is recorded in [MultiplayerSessionEngine.md](MultiplayerSessionEngine.md).

## Shape

- **Source:** `src/TopiaForge.ModManager.Core/Multiplayer/`, namespace `TopiaForge.ModManager.Core.Multiplayer`.
- **Visibility:** every type is `internal`. Core already grants `InternalsVisibleTo` to `TopiaForge.ModManager`, the
  consumer, and to `TopiaForge.ModManager.Tests`. Nothing here is SDK surface, so no API baseline changes.
- **Dependencies:** the base class library and Core's own `ModManifest` and `SessionPhase`. No Unity, no sockets, no
  SDK assemblies. Participants are the runtime's dense participant slots (integers from 1); the runtime maps them to
  the session's opaque `ParticipantId` values. Vectors are Core's own `NetVector3` and `NetQuaternion`, which hold
  Unity's float components.
- **Threads:** no instance is thread-safe. The runtime drives each one from a single thread, normally the main thread.
- **Tests:** `tests/TopiaForge.ModManager.Tests/Multiplayer*Tests.cs`, run by
  `dotnet run --project tests/TopiaForge.ModManager.Tests -c Release -- --multiplayer-replication` and by the full
  suite. Golden vectors are in `tests/fixtures/multiplayer/replication/`.

### Determinism rules

- Time is integer microseconds on a monotonic clock, passed in by the caller. Nothing reads a wall clock.
- Randomness never appears in the model. Test sweeps use a seeded SplitMix64.
- Validation and limits use exact integer arithmetic: positions on the 1/512 m grid, credit in grid units,
  token buckets in micro-tokens.
- Floating-point work is a fixed sequence of IEEE-754 double operations (`+ - * /` and `sqrt`, all correctly rounded),
  so any platform, and any language, produces the same bits. Runtime code uses no trigonometry: the spawn ring's
  directions are written-out constants, and rotations interpolate with normalised lerp, not slerp.
- Rounding is half away from zero, computed exactly from truncation toward zero (see quantization). `Math.Round` is
  not used, because older runtimes misround values just below one half.

## Realms and network classes (WS3.1)

`ModNetworkClasses` maps a manifest's `EffectiveMultiplayerMode`. All five modes already parse, so there is no schema
gap.

| Manifest | `ModNetworkClass` | Runs offline | Runs on the listen host | Runs on a client |
| --- | --- | --- | --- | --- |
| No `multiplayer` block (implicit) | Shared | Yes | Yes | Yes |
| `session` | Shared | Yes | Yes | Yes |
| `client-local` | ClientLocal | Yes | Yes | Yes, on the peer that has it |
| `server-only` | ServerOnly | Yes | Yes | No |
| `standalone` | Standalone | Yes | No | No |

A manifest whose mode cannot be read (an unsupported schema or an unknown mode) is classified as standalone by
`Classify`: it can run offline but never takes part in a session. Validation rejects such manifests before they load,
so this is defence in depth. `TryFromManifest` reports the failure instead.

`NetRealm` is `Offline`, `ListenHost` or `Client`. `NetRealms.EvaluateTransition(from, to, phase, standaloneModEnabled)`
answers a realm change with a verdict and an effect. The rows are checked in order and the first that matches wins, so
a standalone mod is reported before a busy phase: the player hears the reason that will not go away by waiting.

| From | To | Session phase | Verdict | Effect |
| --- | --- | --- | --- | --- |
| Any | Same realm | Any | `Unchanged` | — |
| Online | `Offline` | Any | `Allowed` | `EndOnlineSession`: leaving is never refused |
| `ListenHost` | `Client`, or the reverse | Any | `RequiresLeave` | — |
| `Offline` | Online | Any, with a standalone mod enabled | `StandaloneRefused` | — |
| `Offline` | Online | Preparing, LoadingWorld, StartingMode, Stopping | `Busy` | — |
| `Offline` | `ListenHost` | Idle | `Allowed` | `StartAsHost` |
| `Offline` | `ListenHost` | Running | `Allowed` | `PromoteInPlace` ("Open to multiplayer") |
| `Offline` | `Client` | Idle | `Allowed` | `ReplaceLocalSession` |
| `Offline` | `Client` | Running | `RequiresLeave` | — ("Leave current game?", then ask again from Idle) |

`PromoteInPlace` is where `NetworkWorld` takes its scene census and baselines earlier spawns from the descriptors it
recorded at spawn time. A client that wants another server leaves (`Client` to `Offline`) and then joins from Idle. The
runtime applies these rules to every request to go online or to leave, as
[MultiplayerRuntimeIntegration.md](MultiplayerRuntimeIntegration.md#10-the-realm) records.

## Execution model (WS3.1 and WS3.4)

Every mod context carries a `ModNetProfile`: its network class and its `ModExecutionContext`.

| Context | Offline | Listen host | Client |
| --- | --- | --- | --- |
| Process | Runs | Runs | Runs |
| WorldOwner | Runs | Runs | Runs, loading the world deterministically |
| GamemodeServer | Runs | Runs (the factory runs here) | Never runs |
| Participant | Runs | Runs, for the host's own player | Runs, for the local player |

`ModNetProfile.RunsOn(realm)` requires both the class and the context to run there.

**Intent.** `IntentSource` records why code is running. `None` is scripted: timers, ticks, world events. Player intent
is `InputActiveFrame` (after the input sample), `UiCallback`, `OwnerCommand`, `InteractionHandler` and `MotionLease`.
The runtime's `IntentScope` (an `AsyncLocal`, Unity side) sets it; `IntentRules.IsPlayerIntent` classifies it.

**Routes.** `ExecutionRouting.Route(realm, profile, intent)` says where a world-mutating call takes effect:

| Context | Offline | Host, scripted | Host, intent | Client, scripted | Client, intent |
| --- | --- | --- | --- | --- | --- |
| Process | Canonical | Canonical | Canonical | Shadow | Forwarded |
| WorldOwner | Canonical | Canonical | Canonical | Shadow | Forwarded |
| GamemodeServer | Canonical | Canonical | Canonical | NotRun | NotRun |
| Participant | Canonical | Canonical | Canonical | Forwarded | Forwarded |

- **Canonical:** takes effect here; this is what every peer sees.
- **Forwarded:** sent to the host as a request, predicted locally, then adopted or rolled back.
- **Shadow:** a local no-op bound to the host's result with the same (mod, sequence), found through
  `NetworkEntityTable.TryFindByOrigin`, so nothing is duplicated.
- **NotRun:** the profile does not run in this realm (server-only and standalone mods on a client, standalone online).
- **LocalOnly:** every call from a `client-local` mod, in every realm. It runs only where it is installed, so nothing it
  does is replicated, forwarded or baselined.

The other rows of the plan's table:

| Rule | Offline | Listen host | Client |
| --- | --- | --- | --- |
| `TimeControl(realm, profile)` (Chronos freeze, slow, step) | `Apply` from any context | `Apply` from a GamemodeServer that is not client-local, otherwise `NotAuthoritative` | `NotAuthoritative`; the client applies the server's scale |
| `NativePause` | `FreezesWorld` | `LocalMenuOnly` | `LocalMenuOnly` |
| `LocalPlayerAction` (damage, heal, control) | `Local` | `OwnerAuthoritative` | `OwnerAuthoritative` |

Owner-authoritative means the player's own process applies it and the host plausibility-checks it. Time control takes
the whole profile, not just the context, because a client-local mod's gamemode server also runs on the host: its time
change would reach every peer, and client-local mods are never networked. A profile that does not run in the realm is
`NotAuthoritative` too.

## Network entity table (WS3.5)

`NetworkEntityTable` holds the live replicated entities:

- **Ids:** `NetworkEntityId` is a `uint`. The host allocates from 1 upward and never reuses an id, even across
  `Clear()` at a session change. Zero is reserved. Allocation past `uint.MaxValue` throws rather than wrapping.
- **Mirrored ids:** a client's `TryAdd` never moves the allocation counter. A table that mirrored a host's ids, even
  `uint.MaxValue`, still allocates its own from where it left off when this process hosts later, so one announcement
  cannot exhaust or steer them. A table used both ways skips any id that is live or tombstoned in it.
- **Peer input:** `TryAdd` refuses id zero (`InvalidId`) and a kind that is not a valid token (`InvalidKind`) with a
  result, never an exception. `NetworkEntityId.TryCreate`, `NetOwner.TryFromSlot` (zero is the server) and
  `NetSpawnOrigin.TryCreate` read the other announced fields off the wire the same way. The host's own `Allocate` still
  throws on a bad kind, which is a programming error.
- **Kind:** a lowercase token of at most 64 characters (`[a-z0-9][a-z0-9._-]*`), such as `creator.content`, telling peers
  which replica factory builds the entity.
- **Owner:** `NetOwner` is the entity's lifetime owner: the server, or the participant whose proxy scope spawned it and
  cleans it up on leave. Who simulates a body is separate: that is the ownership state machine.
- **Origin:** an optional `NetSpawnOrigin` (mod id, sequence). At most one live entity has a given origin, which is
  what a client's shadow binds to.
- **Tombstones:** a removed id is tombstoned, and `TryAdd` refuses it, so a stale spawn cannot resurrect it. The newest
  8,192 tombstones are kept, as in the session engine; older ones are forgotten.
- Helpers return ids in ascending order, so callers iterate the same way on every peer.

### Participant slots

Participants are dense slots that the runtime reuses once a participant leaves. Three structures keep state per slot,
and each relies on the runtime clearing a leaver before its slot goes to anyone else:

- `NetworkEntityTable`: remove or reassign the entities `CollectOwnedBy` reports for the leaver.
- `OwnershipStateMachine`: call `Disconnect` on every body's machine. It reclaims a body the leaver holds and forgets the
  leaver's cooldown, strike and last release.
- `PlayerRequestLimiter`: call `Forget`, so the newcomer starts with full buckets.

Otherwise the newcomer inherits the leaver's spawns, held body, cooldown or spent buckets. The per-client
`RelevancySet` and `ReplicationScheduler` are discarded with the client. A participant whose link closes has not left
yet: the engine keeps its id, and so its slot, for a 30 s grace period. Until it leaves for good the runtime calls only
`LinkClosed` for it, which takes back the bodies it holds and keeps its cooldowns, as the
[delivery contract](#delivery-contract) says. The machine accepts slots from 1 to the policy's `MaximumParticipants`
(16 by default, the plan's largest session) and keeps one cooldown entry per slot, so its cooldown table is bounded by
the participant count.

## Quantization and wire format (WS3.5)

| Field | Encoding | Range | Largest error in range |
| --- | --- | --- | --- |
| Position axis | 22-bit signed, 1/512 m steps | -4096 m to 4095.998046875 m | 1/1024 m (0.98 mm) |
| Rotation | Smallest-three: 2 index bits, three 10-bit codes | Unit quaternions | 0.28° (tested; the theoretical worst case is 0.2747°) |
| Velocity axis | 11-bit signed, 1/16 unit/s steps | -64 to 63.9375 m/s or rad/s | 1/32 per second |

**Axes.** `units = clamp(roundHalfAwayFromZero(value * scale))`. NaN encodes as zero; infinities clamp. Every grid
value decodes exactly to a float. `roundHalfAwayFromZero(v)`: let `t = trunc(v)`, toward zero and keeping the sign as
IEEE-754 does, and `d = v - t`, which is exact for every double; the result is `t + 1` if `d >= 0.5`, `t - 1` if
`d <= -0.5`, and `t` otherwise, so a zero result keeps the sign of `v`. An earlier rule took `d` from `floor(v)`, but
`v - floor(v)` is itself rounded for -0.49999999999999994, which then rounded to -1 instead of zero.

**Rotations**, in this exact order of double operations:

1. A non-finite input, or one whose `((x*x + y*y) + z*z) + w*w` is not positive, encodes as the identity, `0x7FDFF7FF`.
2. Divide each component by `sqrt` of that sum.
3. The largest component is the first with the greatest magnitude (ties go to the lowest index: x, y, z, w). If it is
   negative, the whole quaternion is negated, because q and -q are the same rotation.
4. Each other component `c`, in index order, becomes `code = roundHalfAwayFromZero(clamp(c * 1.4142135623730951, -1, 1)
   * 511) + 511`: codes 0 to 1022, with 511 exactly zero, so the identity round-trips exactly. Code 1023 is never
   written and decodes as 1022.
5. The packed value is `index | code0 << 2 | code1 << 12 | code2 << 22`.

Decoding computes each component as `((code - 511) / 511) / 1.4142135623730951`, and the largest as
`sqrt(max(0, 1 - ((a*a + b*b) + c*c)))`; each is then rounded to a float.

**Bit stream.** `NetBitWriter` packs least significant bit first: stream bit `i` is bit `i % 8` of byte `i / 8`, and each
value's lowest bit goes first. The last byte's spare bits are zero, which `NetBitReader.IsAtCanonicalEnd` checks. Reads
from untrusted input report failure instead of throwing, and a failed read consumes nothing.

**Transform record:** position x, y, z (22 bits each, stored as `units + 2^21`), rotation (32), a velocity flag (1),
then, if set, velocity x, y, z (11 bits each, stored as `units + 1024`). That is 99 bits (13 bytes) without velocity and
132 bits (17 bytes) with it. Angular velocity, as in a hand-back, uses the same 11-bit format in radians per second.

## Snapshot interpolation (WS3.5)

`SnapshotInterpolationBuffer` plays one replica back slightly in the past.

| Parameter | Value |
| --- | --- |
| Capacity | 20 samples, for streams of up to 30 Hz |
| Delay | the longer of the interval and the largest buffered gap, plus 3 × jitter, clamped to 80-350 ms |
| Playback rate | 0.9× to 1.1× real time; up to 2× while the render lags the sender's clock by more than 400 ms (the cap plus a 50 ms dead band), down to 0.5× while it is closer to that clock than 60 ms (the 80 ms floor less a 20 ms dead band) |
| Extrapolation cap | 250 ms past the newest sample |
| Timeline discontinuity | 1 s |

**Estimators** (integer, rounded when read, so a constant input settles exactly):

- *Offset:* the mean of `serverTime - localArrival`, gain 1/8.
- *Jitter:* RFC 3550's interarrival jitter, the mean absolute change in transit time, gain 1/16. Each change counts at
  most 1 s.
- *Interval:* the mean gap between successively newest samples, gain 1/8, starting at the nominal interval the caller
  gives (100 ms by default).
- *Largest gap:* the longest gap between successive buffered samples, or zero with fewer than two.

Every arrival except a duplicate updates the offset and jitter, late ones included, because lateness is exactly what
the delay must absorb. The delay covers the longest expected wait for the next sample, because with less there is no
next sample to interpolate towards. The mean interval alone is not enough: the scheduler's rotation makes the cadence
uneven (ten equal bodies sharing four sends a tick arrive 200 or 300 ms apart), and a lost sample doubles a gap, while
transit jitter is zero on a clean network. So the wait is the interval or the largest buffered gap, whichever is
longer, and a jitter margin goes on top.

**Capacity.** The buffer serves streams of up to 30 Hz (`MaximumStreamRateHz`), the fastest the runtime sends: a held
body's samples. Poses run at 20 to 30 Hz and snapshots at 10 Hz. The constructor refuses a nominal interval shorter than
1/30 s. The buffer must hold every sample from the one before the render to the newest:

- The render can sit the cap and the dead band, 400 ms, behind the estimated sender clock.
- That estimate is the mean transit, and the newest sample, the quickest to arrive, leads it by about half the jitter:
  some 160 ms when jitter just pins the delay at the cap at 30 Hz.
- Twenty samples at 30 Hz span 633 ms: those 560 ms, the interval before the render, and one sample to spare for the
  next arrival.

With fewer, a full buffer has to drop a sample the render has not reached yet, and the render later cuts straight
across it. A faster stream still plays, but its buffer then spans less than the delay.

**Render clock:** the playback offset is the render time minus the local time, and its target is `offset - delay`.
The first frame after a reset renders at the target. Each later frame moves the playback offset toward the target by
a slew percentage of the local time that passed (counting at most 1 s), and renders at `localNow + playbackOffset`.
The slew is normally 10%, so playback advances between 0.9 and 1.1 times real time. A sudden change in either estimate
is absorbed over time: a sample 700 ms late drags the offset estimate back 87.5 ms at once, which used to pause playback
for five frames and then run it at up to 1.47 times real time.

Two corrections bound how far the render drifts from the sender's clock as estimated here, `localNow + offset`:

- *Catch-up.* While the render lags that clock by more than the 350 ms cap plus a 50 ms dead band, the slew grows by
  one percent for each millisecond beyond the band, up to 100%, so playback runs at up to twice real time. A timeline
  step under the 1 s reset, a first sample that arrived late, or a fall in latency moves the offset estimate forward
  faster than 1.1× can follow. Inside the band the render catches up at the normal 1.1× at most, because under heavy
  jitter the offset estimate wanders around the cap and chasing it buys nothing.
- *Fall-back.* While the render is closer to that clock than the 80 ms floor less a 20 ms dead band, 60 ms, the slew
  grows by one percent for each millisecond below 60 ms, up to 50%, so playback slows to as little as half real time.
  A rise in latency moves the offset estimate back faster than 0.9× can follow, and below the floor the render soon
  passes the newest sample. Inside the band the render falls back at the normal 0.9× at most, because a stream whose
  target sits at the floor, as light jitter at 20 to 30 Hz leaves it, keeps the offset estimate's noise around the
  floor.

The gain is proportional, so a small excursion is corrected gently and a large one quickly, and the slew is back to
10% once the render is within bounds. Neither correction moves the render clock in a jump. The render time never moves
backwards between resets: a local clock that steps back holds it, and the playback offset follows. `CurrentDelayMicros`
reports `offset - playbackOffset`, the delay the last frame rendered at. It follows the target. It can sit up to either
dead band past the cap or under the floor without the render changing speed, go further while the render catches up
or falls back, and dip a little further still: the first late sample of a latency rise moves the offset estimate at
once, which no bounded slew can absorb in the same frame.

**Samples:**

- A sample with a time already buffered is a `Duplicate` and changes nothing.
- One older than the sample rendering has consumed (the newest at or before the last render time), or older than
  everything in a full buffer, is `Stale`. An out-of-order sample newer than that is inserted in order.
- A full buffer drops its oldest sample, unless the render still shows it. A render holding the oldest sample keeps it
  and drops the next one; a render between the oldest two keeps both and drops the third. Dropping a sample never moves
  the replica, and the render never has to jump forward to stay inside the buffer. The gap a dropped sample leaves
  between its neighbours does not count as the largest buffered gap, because it says nothing about the cadence. With
  the capacity above, a stream of up to 30 Hz with up to 400 ms of jitter only drops samples the render has passed,
  except while it catches up.
- If a sample's offset differs from the estimate by more than 1 s, the sender's timeline jumped: a newer sample restarts
  the buffer (`Reset`), and an older one is `Stale`.

**Output:** before the oldest sample the replica holds it (`Holding`); between samples, position lerps and rotation
nlerps along the shorter arc (`Interpolating`). Past the newest it extrapolates with the sample's velocity, or else the
slope of the last two samples (`Extrapolating`), and after 250 ms it stays where the cap left it
(`ExtrapolationCapped`). Rotation is held when extrapolating.

The Unity side should stamp arrival times on the network thread, so a main-thread stall does not read as jitter.

## Relevancy (WS3.5)

`RelevancySet` is one viewer's relevant entities. An entity enters at 150 m or less and leaves beyond 170 m, both
inclusive at the edge, compared as exact squared distances in grid units (150 m is 5,898,240,000 squared units). A
candidate marked always-relevant (the viewer's own avatar, a body the viewer holds) is relevant at any distance.
`Update` reports entries and exits in ascending id order; a member missing from the candidates leaves; `Remove` drops a
despawned entity without reporting it.

## Replication scheduler (WS3.5)

`ReplicationScheduler` picks one client's entity updates for each downstream tick.

1. `credit = min(credit, 0) + budget`. Unused budget does not carry over, but an overdraw does.
2. Each candidate's accumulator grows by its priority (1 to 1,000,000), except a body at rest that has had its three
   confirmations: its accumulator is held at zero and it costs nothing.
3. Candidates sort by accumulator, highest first. Ties go to the higher priority, then to the entity sent longest ago
   (by the scheduler's send count), then to the lower id.
4. While credit is positive, the first candidate is always sent, even if it alone overdraws; later ones are sent only if
   they fit. A sent entity's accumulator resets to zero.
5. Entities missing from the tick's candidates are forgotten.

**Rest suppression.** A body that comes to rest is sent three more times, so its final pose survives loss on the
unreliable channel, and then stops costing budget until it moves. `Refresh` re-arms the confirmations, for example
after a client resynchronises.

**Fairness and starvation.** Equal priorities take turns: ten entities sharing three sends a tick are each sent every
three or four ticks, exactly 90 times in 300 ticks. Unequal priorities share in proportion: priority 3 against
priority 1 sends three to one. No entity starves: it waits at most about the sum of its rivals' priorities divided by
its own, plus one tick, per send it competes for; the tests hold a priority-1 entity against three priority-1000 rivals
to 3,001 ticks. A tick spends at most its budget unless one update overdraws, and over any run the bytes sent stay
within the budget plus one update.

The default budget is 800 bytes a tick: the Realtime class's 64 kbit/s at the 10 Hz downstream rate. Once its entities
are known, a tick allocates nothing: the sort takes a comparison made once.

## Motion ownership (WS3.5)

`OwnershipStateMachine` is the host's authority over who simulates one body; `OwnershipClaim` is one client's view of
it. States are `Server`, `Requested` (client only), `Owned` and `ReleaseWindow`.

**Epochs.** Every change of owner increments a `uint` epoch, wrapping at the top, and every message names the epoch it
was sent under. A message naming another epoch is refused with `StaleEpoch`, and the reply carries the current epoch
and owner so the sender can retry. The host only compares epochs for equality; a client's claim also orders them, in
serial-number order across the wrap, so that it can ignore a report older than one it has heard.

| Event (host) | Server | Owned by the sender | Owned by someone else | Release window, sender's | Release window, someone else's |
| --- | --- | --- | --- | --- | --- |
| `Request` (current epoch) | `Granted`, epoch + 1, if in range and not yielding | `AlreadyOwned` | `Denied`: the first grab wins; from within the hold range, it contests the hold | `Granted`, no epoch change, if in range (a catch of one's own throw) | `Granted`, epoch + 1, if in range and not yielding (a steal) |
| `Stream` | `NotOwner` | Validated, with the range check | `NotOwner` | Validated, no range check | `NotOwner` |
| `BeginRelease` | `NotOwner` | `Releasing` if the body is in range of the holder: the window opens | `NotOwner` | `Releasing`, deadline kept | `NotOwner` |
| `Handback` | `NotOwner` | Validated, then `HandedBack`, epoch + 1 | `NotOwner` | Validated, then `HandedBack`, epoch + 1 | `NotOwner` |
| `LinkClosed` | `NoChange` | `Reclaimed` (`Disconnected`), epoch + 1 | `NoChange` | `Reclaimed` (`Disconnected`), epoch + 1 | `NoChange` |

A request checks the epoch, then the range, then yielding. One from beyond the hold range is `OutOfRange`, and one from
a yielding participant is `Yielding`; like `Denied` and `StaleEpoch`, they change nothing about the hold, though a stale
one for a free body can make the last releaser yield (see yielding below).

**Optimistic grant.** A client's `Grab` of a body it does not hold moves to `Requested` and starts simulating at once.
Its samples carry `StreamEpoch`, the epoch a grant will assign (the epoch the grab named, plus one), so they are
accepted the moment the host processes the request, before the client hears back. A refusal (`Denied`, `OutOfRange`,
`Yielding` or `StaleEpoch`) rolls the grab back. A grab of a body another player holds is simulated the same way: the
host grants it only if that player has thrown it, which the client cannot see, and a granted steal's first sample must
reach the host within the first-sample deadline, which a round trip can exceed. Ownership is checked by identity as well
as epoch, so a stream that guesses another player's epoch is still `NotOwner`.

**Validation.** All in exact grid units:

- *Speed:* a distance budget. Credit accrues at 64 m/s for the time since the last validated sample (at most 250 ms of
  it), capped at a quarter-second burst plus 0.5 m of tolerance: 8,448 units. A sample spends its displacement. Idle
  time cannot bank more than the burst, so it cannot fund a teleport.
- *Range:* the body must be within 100 m plus the tolerance of the participant's avatar, as the host has it, whenever
  the body is granted (a grab, a steal or a catch of one's own throw), for every sample and hand-back while it is held,
  and when it is thrown, where `BeginRelease` checks the body's last validated position against the holder's avatar.
  During the release window a thrown body has no range limit, but the speed budget still applies, so it moves away from
  where it was released no faster than that budget allows.
- A sample, hand-back or throw that fails a check is `Rejected`: the host reclaims the body at its last validated
  position. Escalation to a kick is the runtime's decision.

**Release window.** `BeginRelease` (a throw or a drop) keeps ownership for 1 s while the owner simulates the flight and
streams it. When the window closes, the owner hands back `{position, linear velocity, angular velocity}`. The position
passes the same checks; velocities longer than 64 m/s or 64 rad/s are scaled down, and non-finite ones become zero.
Without the range check on the throw, a holder could throw a body from anywhere, even straight after a grant, and then
stream it far from itself until the hand-back timeout.

**First sample.** A new owner must send an accepted sample within 250 ms of its grant. A client streams from the moment
it grabs, so a grant that hears nothing that long is abandoned: `Poll` reclaims it (`NoFirstSample`) well before the
1 s silence timeout. A sample that arrived before the grant was refused as stale and does not count, and neither does a
throw.

**Yielding.** An owner that loses the body by going quiet (no first sample, silence, or no hand-back) yields it: its
requests for that body are `Yielding` until its cooldown ends, so another player's request wins first. Before, a
participant could take the body back straight after every reclaim without ever streaming it, and the players beside it
never got it. Letting go and grabbing again would do the same, so more cases yield:

- A hand-back or a closed link before the first sample, which is going quiet by another route.
- The end of a *contested* hold by a hand-back, a failed validation or a closed link. A hold is contested once another
  participant within the hold range asks for the body and is denied, even one that is yielding itself, so a holder
  cannot hand back and grab again for the whole of another player's cooldown. A request from out of range does not
  count, so a player far away cannot make a holder wait.
- The end of a hold too short to contest. A grab, a sample and a hand-back that arrive together (or a grab and a sample
  rejected on purpose) end a hold before any other request can reach it while the body is held. The host remembers who
  last ended an uncontested hold by a hand-back, a failed validation or a closed link, and when (`LastReleaser`,
  `LastReleaseMicros`), until the body is granted again. A request for the free body that names another epoch, from
  another participant within the hold range, within 1 s after that release, is one the hold made stale: the releaser
  yields from then, at most once per release.

**Cooldowns.** Every participant has its own cooldown, one entry per slot, so one participant's yield never frees
another. A yield is a *strike* when the participant earned it itself:

- going quiet: no first sample, silence, no hand-back, or a hand-back or closed link before the first sample;
- failing validation;
- letting go, by a hand-back or a closed link, before the hold has lasted half the 1 s release window
  (`ShortHoldMicros`). A client's claim never does that: it holds a body the whole window, since it hands back only when
  its window closes, and the other half allows for its grab reaching the host late, after a retransmission, while its
  hand-back does not.

A strike costs 1 s, or twice the participant's last strike, at most 8 s (`MaximumYieldCooldownMicros`), when it comes
within 8 s after the participant's last cooldown ended. So a participant that keeps going quiet yields for 1, 2, 4 and
then 8 s, and one that behaves for 8 s after a cooldown starts again at 1 s. Any other yield is *taking turns*:
letting go of a hold that lasted at least half the release window, because another player contested it or a request it
made stale arrived. Taking turns costs 1 s and leaves the last strike as it was. Contests and late requests come from other
players, who could send them on purpose, so they never escalate anyone; going quiet, failing validation and letting go
early are the participant's own doing. `LastReleaseStrikes` says whether a late request after the remembered release
would make its yield a strike.

A grant clears nothing: the cooldown is over by then, and the strike is remembered for the next one. `LinkClosed`
keeps every cooldown and release, so a quick rejoin clears nothing; `Disconnect` forgets the participant's cooldown,
strike and release. A host reclaim makes nobody yield, and neither does the end of an uncontested hold that lasted the
release window by a hand-back after streaming or by a closed link, unless a request that hold made stale arrives
within the second after it.

**Reclaim.** `Poll` reclaims from a new owner that sent no first sample in time (`NoFirstSample`), from an owner that has
sent nothing for more than 1 s (`Silence`), or from one that has not handed back more than 1 s after its window closed
(`HandbackTimeout`), even if it is still streaming. `LinkClosed` reclaims at once from a participant whose link closed,
and `Disconnect` from one that left for good; both report `Disconnected`. The host can `Reclaim` for its own reasons,
such as a despawn. Every reclaim increments the epoch.

**Client view.** `OwnershipClaim` keeps what the host holds for its client as far as the host's answers have confirmed
it, plus the messages the client sent that no answer covers yet: its requests, and the throws and hand-backs between
them. Its `State` is the confirmed state with those messages replayed on top, each request predicted granted unless a
report says the host has moved past the epoch it names other than by granting it.

- Every request has a number, counting up per claim and across resets, which `Grab` returns with the epoch it names
  (`OwnershipRequest`). Only an answer settles a request: the one whose number it echoes. The throws and hand-backs sent
  before that request are applied first, as the host applied them, epoch check included; an earlier request that no
  answer named never reached the host, and settles as having changed nothing (`AnswerSkipped`); then the answer itself.
  A grant means the host holds the body for the client under the answer's epoch, and a refusal changes nothing on the
  host. Once no answer is due, the messages after the last request take effect as well, so the release window settles
  only on the last answer.
- An announcement records the owner and epoch and settles nothing, even one naming the client: the host may announce a
  grant before its answer, after it, after later answers, or not at all. A refusal records the owner and epoch it names
  too.
- `Poll`, called once a frame, takes a request unanswered for 2 s (`AnswerTimeoutMicros`) as lost (`AnswerTimeout`), as
  if a later answer had skipped it: a lost grab stops simulating and the player may grab again. An answer to a request
  the claim no longer holds, because it took the request as lost or was reset since, settles nothing. It is recorded
  like an announcement, and a grant among such answers is taken as the hold the host confirmed, so a body the host gave
  this client is never left unstreamed. `LastRecovery` and `LostRequests` record why the claim last stopped waiting.
- The claim never takes an epoch older than the newest it has heard, compared in serial-number order so the wrap is
  harmless, and any newer epoch ends the hold it had confirmed: a reclaim, a steal or the server taking a hand-back
  stops local simulation.
- A pending grab's samples carry the epoch it named plus one, whatever reports arrive meanwhile. The client's own
  hand-back moves its known epoch on by one, as the host's does, so picking the body straight up again names the epoch
  the host will have.
- A grab of a body the client does not hold waits until no answer is due, because an answer in flight may change the
  epoch the request must name. A catch of the client's own throw does not wait, but no more than 16 requests may be
  outstanding.
- `Reset(reason)` forgets whatever no answer will cover once the stream of replies breaks; see the delivery contract
  below.

So a catch refused behind a pending grab resumes the release window with the deadline the host kept, two catches in
one round trip settle where the host is, whichever is refused, and an announcement between two answers settles neither.
A grant under an epoch the client did not predict, as when its catch turns out to steal another player's throw, leaves
whatever the client sent under its guess ignored by the host, and the claim ignores it too. Two checks play a claim
against the host machine and require the two to agree, epoch included, whenever every reply has arrived:

- an exhaustive one over every sequence of up to five events inside one round trip (Alice's grabs, catches, throws and
  hand-backs, Bob's grabs, throws and hand-backs, and polls), with the replies in every order that keeps answers in
  order and announcements in order, and Alice's own grants also unannounced: 175,131 orders of 66,429 sequences. It
  plays the same sequences again with the host's inbox losing each of Alice's first three requests in turn, where the
  claim must agree once the answer timeout has passed: 63,078 orders of 51,318 sequences, 5,992 of them recovered by a
  later answer and 57,086 by the timeout;
- a lockstep sweep over 600 seeds with random delays, steals and failed validations, where the host announces each
  change at once or at the end of its frame, in full or only the newest, or leaves Alice's grant to its answer: 68,090
  comparisons, 9,759 of them inside a release window.

### Delivery contract

The claim agrees with the host only if its messages travel as follows. The runtime's `WorldReplication` system channel
provides the order; request numbers, the answer timeout and the resets below cover what it cannot promise.

- **One ordered channel, both ways, for everything but samples.** A client's requests, throws and hand-backs for a body
  reach the host in the order sent, and the host's answers and announcements for it reach the client in the order the
  host sent them. System channels give that: their messages ride the session channel, reliable and ordered with the
  session's own traffic, on the epoch-stamped `0x20` envelope.
- **Samples never travel on it.** They take the unreliable, sequenced Realtime lane, which the transport holds to each
  connection's own share (64 kbit/s) and where an older sample arriving after a newer one is dropped. So neither a
  sender's samples, 30 a second for a held body, nor a stalled host can push a request, throw or hand-back past the
  channel's bounds. A sample that is lost, or that overtakes its request, costs nothing: the machine refuses it as stale
  or spends a little more distance budget on the next one, and the 250 ms first-sample deadline spans seven samples at
  30 Hz.
- **Never through replicated object state.** Object and state replication coalesce changes and flush them ahead of the
  results queued before them. A claim fed from replicated object state therefore hears the newest owner before the
  answer to an older request: in the verification its epoch went from 2 back to 1, and its next grab was refused.
- **Answers settle; announcements inform.** Every request carries its number, and the host answers each request it
  handles exactly once, when it handles it, echoing the number, with `OnGranted` (granted, or already the requester's)
  or `OnRefused`, including a refusal by the request limiter. It announces every change of owner to every client at any
  later point: at once, at the end of its frame, or only the newest of a frame. It may leave a requester's own grant to
  the answer. FIFO delivery does not keep an announcement next to the answer it goes with, which is why only answers
  settle requests.
- **Epochs never move backwards** in the claim, as above, so a late or misordered report cannot undo a newer one.
- **A request can vanish on an open link.** A system channel drops new messages past its bounds (1,024 messages or
  1 MiB waiting; per sender once the channels budget each sender) and tells nobody: one dropped grab left a claim
  `Requested`, simulating and unable to grab, for good. Now the claim recovers by itself: a later answer shows the
  request lost at once, and otherwise `Poll` takes it as lost after 2 s. A lost catch usually costs the hold, because
  the host's hand-back timeout, a second after the window closes, comes first: the host takes the body back, the thrower
  yields, and the timeout then frees the claim. A shorter `AnswerTimeoutMicros` keeps the window instead, at the price
  of giving up on answers that are only slow.
- **What the runtime does when its channel statistics show a drop.** Each side reads its `WorldReplication` statistics
  after draining the channel every frame.
  - *On a client*, a drop may have taken an answer or an announcement, which the order alone cannot reveal, so the
    runtime resets every claim with `ChannelDropped`. That reset keeps the epoch guard, since the session goes on, and
    the next report or refusal brings the current epoch.
  - *On the host*, the runtime cannot answer a request it never received, and does not guess which one it lost: the
    requester recovers by itself, as above. A lost throw, sample or hand-back reads to the machine as a throw that never
    came, a gap in the samples or a late hand-back, which validation and the timeouts already handle. The host counts
    the drops for its diagnostics, without addresses. Once the channels budget each sender, a drop names its sender,
    and a sender that keeps overflowing its own budget is flooding, which the host's moderation can act on.
- **When the order breaks, the runtime resets the claim.** The engine drops what is in flight when a link closes, each
  side's system channels drop what they hold when the session is replaced or ends, and the host drops a client message
  stamped with an earlier session epoch. The runtime calls `OwnershipClaim.Reset` on every claim, with the reason:
  - `LinkClosed` when the link to the host closes;
  - `Reconnected` when the client reconnects or rejoins, before it handles anything from the new link;
  - `SessionReplaced` when the session is replaced or ends;
  - `ChannelDropped` when its own channel's statistics show a message dropped while the session goes on.

  A link that stalls and recovers (`Reconnecting`, then joined again) loses nothing and needs no reset; an answer the
  stall holds up past the answer timeout arrives late and settles nothing it should not. After a reset
  the claim simulates nothing, has nothing outstanding and names its last known epoch until it hears a report. Across
  links and sessions it takes the next report whatever its epoch, because a replaced session counts from zero again;
  the runtime then gives it the body's owner and epoch from the world baseline, as an announcement. Request numbers
  count on, so an answer from before the reset settles nothing.
- **The host's side.** When a participant's link closes, the host calls `LinkClosed(participant, now)` on every
  machine. A body the participant holds goes back to the server at once; the participant yields if the hold had no
  first sample yet or was contested, and otherwise the hold is remembered as a release, as a hand-back would be. The
  engine keeps the participant, and its slot, for a 30 s grace period, so its cooldowns and release stay: a quick
  rejoin clears nothing. Calling `Reclaim` instead, as this contract first said, made nobody yield, so a player that
  grabbed, never streamed, dropped its link and rejoined kept the body from everyone at round trips of 100 ms and more.
  `Disconnect` comes once the participant has left for good, before its slot is reused (see
  [participant slots](#participant-slots)).

### Limits

Ownership is a first-grab-wins lease, and some ways of keeping a body from others cannot be told apart from holding
it. The model bounds the ways that involve not holding it properly; the rest are for the host's moderation.

**Bounded.** Each of these makes the player yield, every participant has its own cooldown, and strikes double up to 8 s:

- *Going quiet:* a grab with no first sample (250 ms), one sample and then silence (1 s), a throw never handed back (a
  second after the window), or a hand-back or closed link before the first sample.
- *Letting go and grabbing again:* a hold another player in range was refused ends in a yield, and letting go of a hold
  shorter than half the release window is a strike, whether a contest or a late request catches it.
- *Holds too short to contest:* a grab, a sample and a hand-back in one burst, or a grab and a rejected sample. The
  other player's late request makes the griefer yield.
- *Dropping the link:* a closed link ends a quiet or contested hold as going quiet does, and the cooldown outlasts the
  rejoin.
- *Taking turns at griefing:* no player's yield frees another, and each one's repeated strikes cost it more.
- *Reach, speed and floods:* grabs, steals, catches and throws only from within 100 m; the distance budget; five grabs a
  second per player; 16 outstanding requests per claim; and the channels' bounds.

The tests hold these against an honest player using a real claim behind round trips of 50 to 500 ms. One griefer, in
each of the five quiet and short-hold loops: the honest player wins one of the first three grants and at least four in
10 s, and the griefer never wins more than one grant in a row plus one for each 200 ms of the honest player's round
trip. Two griefers taking turns, in each loop: the honest player holds the body within 6 s and at least five times in
20 s, where one shared cooldown let them win every grant. Three or four griefers taking turns, in each loop: at least
ten grants in a minute. One or two griefers dropping their links: at least four of its grants in 10 s. In every case
the honest player never strikes; each of its yields is taking turns.

**Not bounded.**

- *Holding properly.* A player that keeps streaming a body holds it for as long as it likes. The first grab wins; that
  is the rule, not a gap in it.
- *Colluding or taking turns.* Players can pass a body between them, throwing and stealing it or holding it in turn,
  and nobody yields, since each hold is a real one. Two honest players taking turns legitimately look the same.
- *Enough griefers.* A quiet hold ends within about 2 s of its holder stopping (a throw streamed through its window but
  never handed back), and a player that keeps striking yields 8 s each time. A team that can cover the cap between them,
  five or more taking turns at such holds, can still keep a body from the rest.
- *Turns on demand.* Any player in range can make a releaser or a holder take turns, by a late request or a denied grab,
  and so cost it a second. It cannot make anyone strike.
- *The host.* The host's own player is canonical and never limited.

**The remedy** is the host's moderation, already in the engine: a kick ends a player's session, and a ban refuses its
identity from then on (see [MultiplayerLiveSessions.md](MultiplayerLiveSessions.md#18-hosting)). The model gives the host
what it needs to spot a griefer, which the runtime should count per participant in the host's diagnostics, without
addresses:

- reclaim reasons by participant: `NoFirstSample`, `Silence`, `HandbackTimeout`, `ValidationFailed` and `Disconnected`;
- cooldowns and strikes (`CooldownOf`): a player whose strike sits at the 8 s cap keeps going quiet or letting go
  early, while in every griefing test the honest player's stays at zero;
- request-limiter refusals (`RateLimited`, `NotPermitted`, `QuotaExceeded`);
- channel drops by sender, once the channels count them that way.

## Scene object keys (WS3.5)

`SceneObjectKey.Compute` is FNV-1a 64 over the UTF-8 bytes of `scene NUL path NUL component`:

- The path joins `name#siblingIndex` segments, root first, with `/`. The sibling index is decimal.
- Every field escapes a backslash, slash, hash or NUL as `\\`, `\/`, `\#` or `\0`, so the text is unambiguous: a name
  containing `/` can never imitate a deeper path.
- A lone surrogate encodes as U+FFFD, as everywhere in the session protocol.

`SceneObjectKeyCensus.Build` keys a whole scene. A key that more than one object produces is excluded, every copy of
it: none is bound, rather than guessing which is which. Unique keys map to the object's census index.

## Spawn ring (WS3.5)

`SpawnSlotPlanner` places joining players around the world's readiness spawn point, which the host's own player keeps.

- Slots 0-7 lie 2.5 m out at 45° steps, starting along +Z and turning toward +X. Slots 8-23 lie 5 m out at 22.5° steps,
  offset by half a step to sit between the inner ones. Neighbours are at least 1.9 m apart, and 24 slots cover 16
  players with room for blocked ones.
- The ground probe is injected as a function that casts down from 2 m above the spawn height at the slot. The hit must be
  finite and within 1.5 m of the spawn height.
- The clearance probe is injected too, and checks that a standing player fits at the hit.
- The first free slot, in index order, that passes both wins. If none does, `TryPlan` returns false and the caller uses
  the spawn point itself, as single player does.

## Players' poses (WS3.10)

`PresencePlausibility` is the host's check of one player's poses, and `PresenceHealth` of its health; `PresencePolicy`
holds the rules, immutable. Presence drives them ([MultiplayerRuntimeIntegration.md](MultiplayerRuntimeIntegration.md),
section 20); `PresencePlausibilityTests`, in the presence suite (`--presence`), proves them.

- **Speed.** A distance budget on the grid, as motion ownership's: credit accrues at 30 m/s up to a two-second bank, each
  pose spends its distance from the last accepted one (horizontal and up), and falling spends a budget of its own at
  64 m/s. A pose may overdraw 256 units (half a metre) once: what it overdraws is owed, so no run of small overdraws adds
  up to a speed past the limit.
- **Bounds.** `PresenceBounds`, a box on the grid, compared exactly. `FromMeters` widens a box of metres by a margin
  sideways and below and a headroom above; `Everywhere` lets in every pose the wire can carry.
- **Teleports.** A pose past the budget stands only when flagged `Teleported`, and only within a `TokenBucket` of two
  that refills one every ten seconds; a standing teleport resets the budget at its landing.
- **Placements.** `ExpectPlacement` takes the first pose within 2 m of the host's placement, within ten seconds, as it.
- **Verdicts.** `Accepted`, `Ignored` (sent before the player heard the latest correction, within a second of it),
  `Corrected` (the pose is dropped, and the player goes back to the last accepted position under a new correction
  number, keeping the credit it had), or `Kick` at the fifth strike. One strike is forgiven for every ten seconds without
  one. A player still on an old correction number after the second is struck.
- **Flags.** An accepted pose loses `Away` when it moved more than half a metre, or turned more than about ten degrees
  (the dot of the two rotations below 0.9962, no trigonometry), and never carries `Teleported` on.
- **Freshness.** `IsFresh` holds for 2.5 seconds after the last accepted pose; `Restart` measures afresh from the next,
  keeping strikes and teleports, as a new world or a rejoin needs.
- **Health.** Finite, from zero to the maximum, a maximum of at most 1,000,000 that never changes after the first report,
  and a rise of at most a quarter of the maximum a second, plus what `AllowGain` allows. A refused value is replaced by
  the nearest allowed, and `Advance` raises health held back toward the report at that pace.

## Request limits (WS3.5)

`TokenBucket` counts micro-tokens (a millionth of a token), so a rate of `r` tokens a second refills `r` micro-tokens a
microsecond. It starts full, caps at capacity, ignores time that runs backwards, and takes a cost only when all of it is
there.

`PlayerRequestLimiter` applies a `PermissionPolicy` to remote players' forwarded requests. The host's own player is
canonical and never limited. For everyone else:

1. Every request spends a token first, so a player who floods forbidden requests is throttled too.
2. Then the target rule (`NotPermitted`).
3. Then, for spawns, the live-spawn quota (`QuotaExceeded`).

| Request | Own spawn | World (server-owned) | Another player's spawn | Burst | Per second |
| --- | --- | --- | --- | --- | --- |
| Spawn | — | — | — | 8 | 4 |
| SetTransform | Allowed | Refused | Refused | 60 | 30 |
| Destroy | Allowed | Refused | Refused | 8 | 4 |
| MotionGrab | Allowed | Allowed | Allowed | 10 | 5 |
| Interaction | Allowed | Allowed | Allowed | 40 | 20 |

Each player may own at most 64 live spawns. The policy is immutable; `WithRule`, `WithLimit` and
`WithMaximumLiveSpawns` return copies, so a gamemode's host policy can loosen or tighten it.

## Presentation routing (WS3.12)

`PresentationRoutingPolicy.Route(realm, profile, kind)` routes a mod context's own UI and audio calls. Only
gamemode-server code on the listen host is redirected, and only from a shared or server-only mod. Every other realm and
context renders locally, and so does every call from a client-local mod, because other players may not have it.

| Call from GamemodeServer on the host | Route |
| --- | --- |
| HUD surface | `MirrorToEveryone`: read-only, coalesced to at most 10 Hz |
| Toast, audio cue | `BroadcastToEveryone`: rate-limited and deduplicated |
| Window, tool, modal | `HostOnly` |

`RouteTargeted(realm, profile, targetIsLocalPlayer, kind)` routes `IPlayer.Ui` calls: the local player's UI renders
locally in every realm; the host sends another player's (`TargetPlayer`); a client may not drive another player's UI,
and neither may a client-local mod on the host (`Refused`).
Targeted windows, tools and modals stay interactive: the client sends back a control id, and the callback runs on the
host after checking it came from the targeted player.

**HUD coalescing.** `HudMirrorCoalescer` handles one surface, identified by a content fingerprint such as FNV-1a of its
text:

- The first change goes at once.
- Changes inside 100 ms of the last send wait, the newest replacing older ones, and go when the interval ends.
- Offering the value peers already have sends nothing and drops a waiting change.

So two sends are never under 100 ms apart, and peers always end on the latest value.

**Broadcasts.** `PresentationBroadcastLimiter` deduplicates and rate-limits by kind:

| Kind | Dedupe window | Burst | Per second |
| --- | --- | --- | --- |
| Toast | 2 s | 4 | 2 |
| Audio cue | 250 ms | 8 | 4 |

A duplicate costs no token; a rate-limited message is dropped and not remembered, so it can be sent later. The last 32
sends of each kind are remembered, more than the rate limits allow within either window.

## How the runtime drives it

The Unity side is expected to use the model like this:

- **Realm changes:** ask `NetRealms.EvaluateTransition` before hosting, promoting or joining, and act on its effect.
- **Routing:** give every mod context a `ModNetProfile`, and route each SDK call with `ExecutionRouting.Route` and the
  current intent source.
- **Host, each downstream tick, per client:** `RelevancySet.Update` from the client's avatar position, then
  `ReplicationScheduler.Schedule` over the relevant entities, then `NetQuantization.WriteTransform` for the chosen ones.
- **Client:** mirror spawns into a `NetworkEntityTable`, treating any refused `TryAdd` as a bad announcement. Feed each
  replica's `SnapshotInterpolationBuffer` as samples arrive, and sample it once a frame before mods run.
- **Motion leases:** the host keeps an `OwnershipStateMachine` per body that has been grabbed, with the session's
  largest participant count as the policy's `MaximumParticipants`, passes the requester's or holder's avatar position to
  `Request`, `Stream`, `BeginRelease` and `Handback`, calls `Poll` every tick, answers every request once with its
  number, and announces every change of owner. Each client keeps an `OwnershipClaim` for bodies its player grabs, sends
  the number `Grab` gives it with each request, and calls the claim's `Poll` once a frame. Requests, throws, hand-backs,
  answers and announcements travel on the `WorldReplication` system channel and samples on the unreliable lane, never
  through replicated object state, and the client resets its claims whenever that channel's order breaks: see the
  [delivery contract](#delivery-contract).
- **Requests:** check every forwarded request with `PlayerRequestLimiter.Evaluate` before running it.
- **Leaving players:** when a link closes, call `LinkClosed` for its participant on every machine; before a slot is
  reused, clear it as [Participant slots](#participant-slots) describes.
- **Diagnostics:** count reclaim reasons, cooldowns and strikes, limiter refusals and channel drops per participant, so
  the host can spot a griefer (see [Limits](#limits)), and a client's claim recoveries (`LastRecovery`, `LostRequests`).
- **Scene and spawns:** build a `SceneObjectKeyCensus` when the world loads or is promoted, and place joining players
  with `SpawnSlotPlanner`.
- **Players' poses:** keep a `PresencePlausibility` and a `PresenceHealth` per joined player on the host, check every
  pose and state with them, and act on each verdict.
- **Presentation:** route UI and audio with `PresentationRoutingPolicy` and the calling context's profile, keep one
  `HudMirrorCoalescer` per mirrored surface, and one `PresentationBroadcastLimiter` per host session.

## Golden vectors

`tests/fixtures/multiplayer/replication/generate.py` computes the vectors from the rules above, without the C# code.
`MultiplayerReplicationGoldenTests` and `MultiplayerMotionGoldenTests` replay them bit for bit, and fail if the folder
holds a vector file that neither replays:

- `quantization.json`: half-away-from-zero rounding on doubles, position and velocity axes, rotations with their decoded
  float bits, and whole transform records.
- `scene-object-key.json`: FNV-1a vectors and scene keys, with their preimages.
- `scheduler.json`: five scenarios, every tick's choices and the credit left.
- `token-bucket.json`: take decisions, including time running backwards.
- `ownership.json`: the hold range, quiet owners yielding, contested holds yielding, short holds yielding to stale
  requests, every participant keeping its own cooldown, strikes doubling up to the cap with the edge of their memory,
  letting go early striking while taking turns does not, closed links, and 300 seeded events that include closed
  links. Each call's outcome comes with the machine's credit, every participant's cooldown and last strike, the
  first-sample state, whether the hold is contested, and the remembered release with whether it would strike.
- `interpolation.json`: a late sample, an uneven cadence, jitter with a duplicate, stale samples and a timeline reset,
  a 900 ms timeline step, latency falling from 950 to 50 ms and rising from 50 to 950 ms, a 300 ms fall at 20 Hz that
  stays inside the dead band, and a resend older than the full buffer followed by a 100 ms rise at 30 Hz that falls
  back past the fall-back's dead band. Each arrival's result is recorded, and each frame's render time, mode, position,
  target delay and playback offset. Between them the scenarios reach every rule for a stale sample.

The client's `OwnershipClaim` has no vectors: it does no arithmetic of its own, and the exhaustive orderings, with and
without a lost request, and the lockstep sweep check it against the host machine, which the vectors pin.

Floats are written as float32 bit patterns and doubles as float64 ones. Regenerate with
`python -I tests/fixtures/multiplayer/replication/generate.py .` from the repository root; a diff after regenerating
means a format or a rule changed.

## Choices the plan leaves open

- **Client-local mods are never networked.** The plan says they run only on the peer that has them; the model also
  keeps their calls out of replication, forwarding and baselines in every realm (`LocalOnly`), because other peers
  may not have the mod. Time control and presentation routing take the whole profile for the same reason, so a
  client-local mod's gamemode server on the host controls nobody's time (`NotAuthoritative`), keeps its HUD and toasts
  local, and cannot target another player's UI. The other way to close that gap was to refuse going online while a
  client-local gamemode runs. The model does not, because such a mod can still run its gamemode for the host's own
  player and the rule needs no new input; the price is that nothing it does reaches the other players.
- **A standalone mod blocks going online from inside the game.** Admission refuses standalone mods on either side; the
  realm rules also refuse "Open to multiplayer" and joining while one is enabled, so the player hears it before any
  connection.
- **Busy phases.** A realm change towards online is refused as `Busy` while the session is preparing, loading, starting
  or stopping. Leaving is never refused.
- **Time control on the host** is allowed from a gamemode server that is not client-local; everything else gets
  `NotAuthoritative`. Single player keeps every context's control.
- **Interpolation delay.** "80 to 350 ms driven by observed jitter" becomes the longer of the observed interval and the
  largest buffered gap, plus three times the RFC 3550 jitter, clamped. Without a wait term a 10 Hz stream would
  extrapolate between every pair of samples. The interval alone was not enough either: ten equal bodies sharing four
  sends a tick arrive 200 or 300 ms apart, the delay settled at 246 ms, and 180 of 1,800 frames extrapolated by up to
  50 ms. The largest gap was chosen over a gap-variation estimate because it covers a repeating pattern exactly, here
  with a 300 ms delay and no extrapolation at all. Its memory is the 20 buffered samples, so a long pause raises the
  delay only until that gap leaves the buffer.
- **Playback rate.** The 0.9× to 1.1× bound applies to the playback offset as a whole, not just to the delay, because
  the offset estimate used to move the render clock without limit: playback ranged from a standstill to 2.06 times
  real time in the rough-network test. The price is a slower response when jitter starts: rendering no longer stops to
  wait, so more frames extrapolate at the onset (60 rather than 27 of 2,397 in that test), never further than before.
- **Catch-up and fall-back.** A move of the sender's clock larger than that slew can follow is corrected at a bounded
  rate rather than in a jump. Before, a full buffer held its render at the oldest sample instead, so the render clock
  jumped whenever that sample left. At 10 Hz with 50 ms of transit and 60 Hz frames, one frame ran 17.2 times real
  time after a 900 ms timeline step or a first sample 900 ms late, with the delay at 625 ms, and 12.5 times after a
  fall in latency from 950 to 50 ms; at 20 Hz a fall of 300 ms already ran 4.2 times real time, and one of 600 ms 16.2
  times. Now the timeline step and the late first sample peak at 1.94× and are caught up within two seconds, with the
  delay at most 469 ms; the fall from 950 to 50 ms peaks at 1.73× with the delay at most 451 ms; the 300 ms fall at
  20 Hz stays within 0.9× to 1.1×, its delay peaking at 369 ms inside the dead band below; and no frame in any of them
  extrapolates. Treating such a move as a discontinuity and re-anchoring, the other fix the review allowed, was not
  chosen: a re-anchor moves the replica in one frame, which is the jump being removed. The fall-back goes beyond the
  review. Without it, a rise in latency from 50 to 950 ms left the render ahead of the samples, extrapolating, for 536
  frames, nine seconds and more; once it started where the render passed the sender's clock, 231 frames; now that it
  starts 20 ms under the 80 ms floor, 195. The 2× and 0.5× limits and the one percent per millisecond gain are this
  record's choices. A full buffer keeps the render's segment rather than its oldest sample, because dropping a sample the render
  is between moved the replica as well.
- **Catch-up dead band.** Catching up as soon as the delay passed the cap chased noise. Under heavy jitter the target
  sits at the cap, and the offset estimate, an average with gain 1/8, wanders around it. With 250 ms of uniform jitter
  at 10 to 30 Hz, 12 to 14% of frames ran faster than 1.1×, peaking at 1.40×. With 400 ms, 19 to 25% did, peaking at
  1.62×. Meanwhile the delay sat past the cap anyway. So catching up now waits for the cap plus 50 ms, and its gain
  counts from there, so the rate never jumps. 50 ms is about 2.7 standard deviations of the offset estimate when
  uniform jitter just pins the delay at the cap (250 ms at 10 Hz: the estimate keeps a quarter of the jitter's 72 ms
  deviation, 19 ms). A 25 ms band let that jitter through at 1.22×; 75 or 100 ms smooth heavier jitter further but
  let a genuine step fall 25 or 50 ms further behind before catching up speeds up. With 50 ms, over five 60 s seeds
  at each rate:
  - with 250 ms of jitter no frame runs faster than 1.10×, and once the jitter is learnt the delay stays between 220
    and 386 ms;
  - with 400 ms, at most 0.5% of frames do, peaking at 1.37×;
  - with 800 ms, more than the cap can hide, catching up is genuine, and 4 to 7% of frames run faster than 1.1×.

  The price is that under heavy jitter the delay sits up to the band past the cap, where it used to be chased.
- **Fall-back dead band.** The fall-back used to start only once the render had passed the estimated sender clock, so
  after a latency rise the delay could sit under the 80 ms floor at 0.9×. A rise from 50 to 150 ms at 30 Hz took it to
  38 ms, with 20 frames extrapolated. Falling back faster from the floor itself kept that dip to 66 ms, but it chased
  noise, as catching up from the cap had: light jitter at 20 and 30 Hz pins the target at the floor, and the offset
  estimate wanders a few milliseconds either side of it. Over five 60 s seeds, once settled, 30 Hz with 40 ms of jitter
  ran 901 of 17,100 frames slower than 0.9×, down to 0.87×; with 30 and 50 ms, 523 and 500; 20 Hz with 30 ms, 296. So
  falling back faster now waits for the floor less a 20 ms dead band, and its gain counts from there. None of those
  streams has a frame outside 0.9× to 1.1×, and the rough-network test, which fell back for 12 frames at down to 0.78×,
  now dips to 64 ms and stays inside them too. The price is a deeper dip after a genuine rise: 50 to 150 ms at 30 Hz
  dips to 52 ms, ends 44 frames under the floor within 0.72 s, and 14 frames extrapolate; at 20 Hz, 56 ms, 49 frames
  and 23. It cannot stay at the floor: the first late sample moves the offset estimate 12.5 ms at once, and the
  estimate then falls faster than 0.9× can follow. A steeper gain kept the dip to 76 ms but hit 0.5× at once; the one
  percent per millisecond gain matches the catch-up's.
- **Capacity.** The plan gives 8 samples. At 30 Hz, the held-body rate, eight span 233 ms, less than the 350 ms cap, so a
  full buffer dropped samples the render had not reached and the render cut straight across them. A body circling 5 m
  out, one turn every 2 s, under 300 ms of jitter rendered 132 mm off its path on average and 670 mm at worst, where the
  chord between neighbouring samples sags 6.9 mm. Sixteen, the verification's figure, still cut across a dropped
  sample now and then: the worst error was 27 mm in ten seeds. The newest sample leads the offset estimate by about half
  the jitter, which sixteen leave no room for. Twenty cover it: the mean error is 4.6 mm and the worst 6.9 mm, the
  chord's own sag, at 300 and 350 ms of jitter.
- **Velocity scale.** 1/16 m/s per step gives ±64 m/s, above the Gravity Gun's default 35 m/s hold speed and 22 m/s
  throw; faster values clamp. Angular velocity uses the same format in rad/s.
- **Scheduler tie-breaking and rest confirmation.** Ties go to priority, then send order, then id. Id order alone
  favoured low ids: in the rotation test, entity 1 was sent 100 times in 300 ticks where its fair share is 90. Send
  order alone breaks proportional shares (3:1 priorities came out 2:1), which the priority key restores. A resting body
  is confirmed three times rather than acknowledged, because the model has no access to acknowledgements; `Refresh`
  re-arms it.
- **Ownership numbers.** Silence and hand-back timeouts are 1 s; speed is 64 m/s with a 250 ms burst; hold range is
  100 m, the Gravity Gun's largest configurable hold distance. A new owner's first sample is due within 250 ms, about
  seven samples at 30 Hz. A cooldown is 1 s, and strikes double it up to 8 s, which is also how long after a cooldown
  ends a strike is remembered. A claim gives up on an answer after 2 s. The machine takes slots up to 16, the plan's
  largest session. The release window lifts the range check but not the speed budget, and a throw must start within
  range.
- **Quiet owners yield after silence too.** The reported problem was a participant re-taking a body straight after each
  reclaim without ever streaming it. Yielding after a grant that lapses without a first sample stops that, but one
  sample per grant would then hold a body just as well, so yielding also follows a silence or hand-back timeout. It is
  a fixed refusal for the cooldown rather than a queue, because the host answers each request as it arrives: during
  the cooldown any other player's request wins, and after it the first request wins again.
- **Letting go of a contested body yields too.** Letting go sidestepped the timeouts: a hand-back and a fresh grab
  every tick won 201 of 201 grants against a player beside the body, and so did one sample per grant, or a sample
  rejected on purpose. So a hand-back before the first sample counts as going quiet, and a hold that another player
  within range was refused yields when it ends with a hand-back or a failed validation; in each of those loops the
  griefer now never wins twice in a row. A request from out of range does not contest, so nobody can make a holder
  wait from afar. A denied request from a yielding participant does contest: leaving such requests out let a holder
  hand back and grab again uncontested for the whole of the other player's cooldown, up to 20 grants in a row. A
  streamed hold nobody else asked for ends without a cooldown, so a player can put down a body nobody wants and pick it
  up again. Two players taking turns can still keep a body from a third, as two players taking turns to hold it
  legitimately can; see [Limits](#limits).
- **Holds too short to contest yield too.** Those loops were tested with an honest player that named the host's live
  epoch, which a real client cannot know. With a real claim behind a 200 ms round trip or more, a griefer sending a
  grab, a sample and a hand-back together every 200 ms (within the limit of five grabs a second) won 50 of 50 grants.
  Its hold ended before any request could reach it, so nothing contested it, and the honest player's grabs all arrived
  naming an epoch the griefer had used up. A free body used to refuse a stale request without noting who asked. Now a
  stale request from another player in range, within the cooldown after an uncontested release, makes the releaser
  yield from then. That is the honest player's claim of the hold, arriving late. Over ten seconds at round trips of 50
  to 500 ms, for all five griefing loops, the honest player wins one of the first three grants and at least four in
  all. The griefer wins at most one more grant in a row for each 200 ms of the honest player's round trip, the time the
  stale grab takes to arrive. With the cooldown off, the griefer still keeps the body from a player 200 ms or more away.
  A grab from out of range, or the releaser's own, does not count. Each release makes its owner yield at most once, from
  the stale request rather than from the release, so the honest player's retry, a round trip later, still finds the
  body free.
- **Every participant has its own cooldown.** One cooldown slot, which each new yield took over, let two griefers free
  each other. Two griefers bursting 100 ms apart, 20 ms from the host, never streaming, won 34 of 34 grants in 10 s at
  every round trip from 50 to 500 ms; sending one sample a grant, 10 of 10; and a grab, a sample and a hand-back, 300
  of 300 over a minute at 200 ms and more. The machine now keeps one entry per participant slot, so the table is
  bounded by the participant count, and one participant's yield never frees another.
- **Strikes double; taking turns does not.** Separate cooldowns alone left two griefers sending one sample a grant
  holding the body between them: each holds it for the silence timeout, and the other's 1 s cooldown is always over by
  then. So repeats cost more. But the honest player in those tests yields after nearly every hold too, because the
  griefers' denied grabs contest it, and contests and late requests can be sent on purpose by anyone in range: doubling
  those would let a griefer drive an honest player's cooldown up. So only a participant's own failures are strikes:
  going quiet, failing validation, and letting go before the hold has lasted half the release window, which no claim
  does. The whole window would be the claim's own minimum; a retransmitted grab reaches the host up to a few hundred
  milliseconds late while the hand-back may not, so half of it is the line. The first rule tried doubled a repeat
  within as long again as the last cooldown; it missed the one-sample griefers, whose holds outlast a second, and still
  gave them every grant. Remembering a strike for the 8 s cap catches them. Now two griefers in every loop give the
  honest player the body within 6 s and at least five times in 20 s at every round trip, and without the doubling two
  one-sample griefers keep it. The honest player never strikes in any of these tests. The 8 s cap bounds what a player
  loses to a run of genuine lapses.
- **A closed link ends a hold as going quiet or letting go would.** The contract had the host call `Reclaim` when a
  link closed, which makes nobody yield. A griefer that grabbed, never streamed, dropped its link 200 ms later and
  rejoined 50 ms after that kept the body from the honest player at every round trip from 100 to 500 ms: 0 of 39
  grants in 10 s. `LinkClosed` yields for a hold with no first sample or a contested one, and remembers any other as a
  release, so a request it made stale can still make it yield. Against one or two such griefers the honest player now
  gets at least four grants in 10 s, and with one it wins one of the first three.
- **Requests are numbered, and a claim gives up on a lost one.** The runtime's system channels drop new messages past
  their bounds without telling the sender, and one dropped grab left a claim `Requested`, simulating and unable to grab,
  30 s later and for good. A timeout alone would let an answer that was only slow settle the request sent after it, so
  every request carries a number the host's answer echoes. A late answer then settles nothing, a grant among them is
  taken as the host's hold, and an answer to a later request shows a lost one at once. The 2 s timeout is long for any
  playable round trip, and too long to save a lost catch, which the host's hand-back timeout reclaims first;
  `AnswerTimeoutMicros` can be shortened for that.
- **Client claim.** The review asked that answers be matched to requests and that the release window settle only on
  the last answer. The claim also replays the throws and hand-backs sent before each answer as the host applied them,
  because a lockstep sweep against the host found one more way to disagree with it: a grant that arrived after a
  refusal of an earlier request had rolled the claim back. The replay keeps the host's epoch check, which a catch needs
  when, unknown to the client, it steals another player's throw under an epoch the client's samples did not guess; the
  sweep's steals exercise that, and dropping the check fails it. A grab of a body the client does not hold waits for
  outstanding answers, so the epoch it names is current and the claim never predicts more than one grant at a time. A
  catch does not wait; the 16-request limit is far more than a player can throw and catch within one round trip.
  The third check found that an ordered channel does not keep an announcement next to its answer. A host that answers
  at once and announces at the end of its frame can deliver Granted, Refused, OwnerChanged, Refused, and the claim then
  let the announcement settle the second catch. So only answers settle requests now. It also found the three gaps the
  delivery contract closes: a claim fed from replicated object state took an epoch back, a closed link stranded a
  request forever, and nothing said when to start again. Hence the epoch guard, `Reset`, and the contract itself. The
  fourth found the gap the contract could not close by itself: a request dropped on a link that stays open. Hence the
  request numbers and the answer timeout above.
- **Steals.** "First grab wins" applies to a held body. A body in its thrower's release window can be caught by another
  player, which the plan's ownership tests call a steal; the thrower can also catch its own throw without an epoch
  change. A grab of a held body is simulated like a grab of a free one. For one round it was sent unsimulated, so a
  granted steal's first sample came a round trip after the grant; at 300 ms or more the grant lapsed and the stealer
  was made to yield. The other fix, a longer first-sample deadline for steals with no yield, would have let a stealer
  hold a body unstreamed for as long as its round trip, which a client can stretch by answering clock probes late, so
  it was not chosen. Steals now hold at round trips from
  50 to 500 ms. The price is that a grab of a body its owner still holds is simulated for a round trip before the
  denial rolls it back.
- **Stale grabs.** A grab naming an old epoch is refused even if the body is now free, and the reply carries the current
  epoch for an immediate retry.
- **Request limits.** The plan names the mechanism but not the numbers; the table above is the conservative default. It
  allows players to move and remove only their own spawns, and to grab and interact with anything.
- **Presentation limits.** The 2 s and 250 ms dedupe windows and the toast and cue rates are this record's choices.
- **Spawn ring geometry.** 8 slots at 2.5 m and 16 at 5 m, a 1.5 m step limit and a 2 m probe height are this record's
  choices.
