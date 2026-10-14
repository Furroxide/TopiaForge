# Multiplayer replication model — design record

Status: Implementing (WS3.1, WS3.4, WS3.5 and WS3.12 of the live-multiplayer plan) · Date: 2026-10-14 · Owner: multiplayer

This records the Unity-free rules and data structures that automatic replication is built on: realms and network
classes, the execution model's routing tables, the entity table, transform quantization, snapshot interpolation,
relevancy, the replication scheduler, motion ownership, scene object keys, the spawn ring, request limits and
presentation routing. They are pure and deterministic, so the Unity side (`NetworkWorld`, the service decorators, the
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
recorded at spawn time. A client that wants another server leaves (`Client` to `Offline`) and then joins from Idle.

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
  leaver's yield cooldown.
- `PlayerRequestLimiter`: call `Forget`, so the newcomer starts with full buckets.

Otherwise the newcomer inherits the leaver's spawns, held body, cooldown or spent buckets. The per-client
`RelevancySet` and `ReplicationScheduler` are discarded with the client.

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
| Capacity | 8 samples |
| Delay | the longer of the interval and the largest buffered gap, plus 3 × jitter, clamped to 80-350 ms |
| Playback rate | the playback offset moves by at most 10% of elapsed time, so playback runs between 0.9× and 1.1× |
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

**Render clock:** the playback offset is the render time minus the local time, and its target is `offset - delay`.
The first frame after a reset renders at the target. Each later frame moves the playback offset toward the target by
at most 10% of the local time that passed (counting at most 1 s), and renders at `localNow + playbackOffset`, so
playback advances between 0.9 and 1.1 times real time. A sudden change in either estimate is absorbed over time: a
sample 700 ms late drags the offset estimate back 87.5 ms at once, which used to pause playback for five frames and
then run it at up to 1.47 times real time.

The render time never moves backwards between resets, and with a full buffer it never falls behind the oldest sample;
either floor moves the playback offset with it. That is the one case where playback can run faster than 1.1×: a stream
too fast for 8 samples to span the delay (faster than about 23 Hz at the 350 ms cap) is held at the oldest sample.
`CurrentDelayMicros` reports `offset - playbackOffset`, the delay the last frame rendered at, which follows the target.

**Samples:**

- A sample with a time already buffered is a `Duplicate` and changes nothing.
- One older than the sample rendering has consumed (the newest at or before the last render time), or older than
  everything in a full buffer, is `Stale`. An out-of-order sample newer than that is inserted in order.
- A full buffer evicts its oldest sample.
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

The default budget is 800 bytes a tick: the Realtime class's 64 kbit/s at the 10 Hz downstream rate.

## Motion ownership (WS3.5)

`OwnershipStateMachine` is the host's authority over who simulates one body; `OwnershipClaim` is one client's view of
it. States are `Server`, `Requested` (client only), `Owned` and `ReleaseWindow`.

**Epochs.** Every change of owner increments a `uint` epoch, wrapping at the top, and every message names the epoch it
was sent under. A message naming another epoch is refused with `StaleEpoch`, and the reply carries the current epoch
and owner so the sender can retry. Epochs are only compared for equality.

| Event (host) | Server | Owned by the sender | Owned by someone else | Release window, sender's | Release window, someone else's |
| --- | --- | --- | --- | --- | --- |
| `Request` (current epoch) | `Granted`, epoch + 1, if in range and not yielding | `AlreadyOwned` | `Denied`: the first grab wins | `Granted`, no epoch change, if in range (a catch of one's own throw) | `Granted`, epoch + 1, if in range and not yielding (a steal) |
| `Stream` | `NotOwner` | Validated, with the range check | `NotOwner` | Validated, no range check | `NotOwner` |
| `BeginRelease` | `NotOwner` | `Releasing` if the body is in range of the holder: the window opens | `NotOwner` | `Releasing`, deadline kept | `NotOwner` |
| `Handback` | `NotOwner` | Validated, then `HandedBack`, epoch + 1 | `NotOwner` | Validated, then `HandedBack`, epoch + 1 | `NotOwner` |

A request checks the epoch, then the range, then yielding. One from beyond the hold range is `OutOfRange`, and one from
a yielding participant is `Yielding`; like `Denied` and `StaleEpoch`, they change nothing.

**Optimistic grant.** A client's `Grab` moves to `Requested` and starts simulating at once. Its samples carry
`StreamEpoch`, the epoch a grant will assign (its last known epoch plus one), so they are accepted the moment the host
processes the request, before the client hears back. A refusal (`Denied`, `OutOfRange`, `Yielding` or `StaleEpoch`)
rolls the client back to `Server`. Ownership is checked by identity as well as epoch, so a stream that guesses another player's epoch is still
`NotOwner`.

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

**Yielding.** An owner that loses the body by going quiet (no first sample, silence, or no hand-back) yields it for 1 s:
its requests for that body are `Yielding` until the cooldown ends, so another player's request wins first. Before,
a participant could take the body back straight after every reclaim without ever streaming it, and the players beside
it never got it. A grant to the yielding participant after its cooldown clears the cooldown, and so does `Disconnect`.
Only the participant that last went quiet is remembered. A failed validation, a disconnect or a host reclaim makes
nobody yield.

**Reclaim.** `Poll` reclaims from a new owner that sent no first sample in time (`NoFirstSample`), from an owner that has
sent nothing for more than 1 s (`Silence`), or from one that has not handed back more than 1 s after its window closed
(`HandbackTimeout`), even if it is still streaming. `Disconnect` reclaims at once. The host can `Reclaim` for its own
reasons, such as a despawn. Every reclaim increments the epoch.

**Client view.** `OwnershipClaim.OnOwnerChanged` follows the host's broadcasts: losing the body (a reclaim, a steal, or
the server taking a hand-back) stops local simulation, and hearing its own grant confirms a pending grab. A grant
notice that arrives after the client already handed back does not restart simulation. If the host refuses a catch of
the client's own throw as out of range, the refusal still names the client as owner, and the claim resumes the release
window with its original deadline, as the host does, even if the client threw again while waiting.

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
- **Motion leases:** the host keeps an `OwnershipStateMachine` per body that has been grabbed, passes the requester's or
  holder's avatar position to `Request`, `Stream`, `BeginRelease` and `Handback`, and calls `Poll` every tick; each
  client keeps an `OwnershipClaim` for bodies its player grabs.
- **Requests:** check every forwarded request with `PlayerRequestLimiter.Evaluate` before running it.
- **Leaving players:** before a slot is reused, clear it as [Participant slots](#participant-slots) describes.
- **Scene and spawns:** build a `SceneObjectKeyCensus` when the world loads or is promoted, and place joining players
  with `SpawnSlotPlanner`.
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
- `ownership.json`: the hold range, quiet owners yielding, and 300 seeded events, each call's outcome with the
  machine's credit, cooldown and first-sample state.
- `interpolation.json`: a late sample, an uneven cadence, and jitter with a duplicate, stale samples and a timeline
  reset, each arrival's result and each frame's render time, mode, position, target delay and playback offset.

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
  with a 300 ms delay and no extrapolation at all. Its memory is the 8 buffered samples, so a long pause raises the
  delay only until that gap leaves the buffer.
- **Playback rate.** The 0.9× to 1.1× bound applies to the playback offset as a whole, not just to the delay, because
  the offset estimate used to move the render clock without limit: playback ranged from a standstill to 2.06 times
  real time in the rough-network test. The price is a slower response when jitter starts: rendering no longer stops to
  wait, so more frames extrapolate at the onset (60 rather than 27 of 2,397 in that test), never further than before.
- **Velocity scale.** 1/16 m/s per step gives ±64 m/s, above the Gravity Gun's default 35 m/s hold speed and 22 m/s
  throw; faster values clamp. Angular velocity uses the same format in rad/s.
- **Scheduler tie-breaking and rest confirmation.** Ties go to priority, then send order, then id. Id order alone
  favoured low ids: in the rotation test, entity 1 was sent 100 times in 300 ticks where its fair share is 90. Send
  order alone breaks proportional shares (3:1 priorities came out 2:1), which the priority key restores. A resting body
  is confirmed three times rather than acknowledged, because the model has no access to acknowledgements; `Refresh`
  re-arms it.
- **Ownership numbers.** Silence and hand-back timeouts are 1 s; speed is 64 m/s with a 250 ms burst; hold range is
  100 m, the Gravity Gun's largest configurable hold distance. A new owner's first sample is due within 250 ms, about
  seven samples at 30 Hz, and an owner that went quiet yields for 1 s. The release window lifts the range check but not
  the speed budget, and a throw must start within range.
- **Quiet owners yield after silence too.** The reported problem was a participant re-taking a body straight after each
  reclaim without ever streaming it. Yielding after a grant that lapses without a first sample stops that, but one
  sample per grant would then hold a body just as well, so yielding also follows a silence or hand-back timeout. It is
  a fixed refusal for the cooldown rather than a queue, because the host answers each request as it arrives: during
  the cooldown any other player's request wins, and after it the first request wins again. Only the last quiet owner
  is remembered, so two players taking turns to go quiet could still hold a body, as two players taking turns to hold
  it legitimately can.
- **Steals.** "First grab wins" applies to a held body. A body in its thrower's release window can be caught by another
  player, which the plan's ownership tests call a steal; the thrower can also catch its own throw without an epoch
  change.
- **Stale grabs.** A grab naming an old epoch is refused even if the body is now free, and the reply carries the current
  epoch for an immediate retry.
- **Request limits.** The plan names the mechanism but not the numbers; the table above is the conservative default. It
  allows players to move and remove only their own spawns, and to grab and interact with anything.
- **Presentation limits.** The 2 s and 250 ms dedupe windows and the toast and cue rates are this record's choices.
- **Spawn ring geometry.** 8 slots at 2.5 m and 16 at 5 m, a 1.5 m step limit and a 2 m probe height are this record's
  choices.
