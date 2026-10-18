---
title: Multiplayer API preview
description: Build and deterministically test server-canonical TopiaForge mods for Robotopia before live transport ships.
---

# Multiplayer API preview

TopiaForge does not ship live multiplayer yet. `TopiaForge.Mods.Multiplayer` freezes the future-facing public
contract, generated wire format, real-game solo provider, and deterministic multi-peer test rig before a supported
live transport exists. A V6 mod that omits `multiplayer` is shared implicitly: admission requires the identical package
on every peer, and `"mode": "standalone"` keeps a mod out of sessions. Replicated state, commands, and objects remain an
explicit module opt-in, not a claim that arbitrary local mutations can be synchronized safely.

`IMultiplayerSession` is a stable owner-scoped facade, not one match object. Bind the generated contract once for the
mod lifetime. Its registrations survive sequential sessions; `Snapshot.Id` changes and `CurrentSessionToken` cancels
when a match is replaced, while a new session starts from each replicated state's immutable declared default.

The architecture preserves the normal TopiaForge boundary: one BepInEx runtime sits behind Unity-free, owner-scoped
contracts, deterministic launcher/profile tooling, and optional specialist modules. Multiplayer identities and roles
therefore live in this module, not on `IModContext`, `IEntity`, `PlayerSnapshot`, or `IGamemodeSession`.

## Authority and sides

Shared state is server-canonical. An owning client may predict a declared command for responsiveness, but the logical
server authenticates its sender, checks ownership and bounds, validates the command, and accepts or rejects it.
Generated code buffers presentation effects until acceptance and providers reconcile confirmed state. A listen host
contains logical client and server sides in one process without running a command twice.

`MultiplayerExecutionSide` describes logical sides; `MultiplayerProcessKind` describes whether presentation systems
exist. A headless dedicated server has no local participant, input, UI, or ordinary audio. Process-local entity IDs,
scene instance IDs, files, configuration, clock samples, and local player state never become network identities.

## Add the module

```sh
topiaforge mod add multiplayer
topiaforge restore
topiaforge mod sync multiplayer
```

This adds the stable contract, source generator, provider dependency, Manifest V6 metadata, and generated contract
lock. Resolve the provider through the existing extension boundary:

```csharp
var multiplayer = Context.RequireMultiplayer();
```

Do not add sockets, Unity Netcode types, raw RPC numbers, transport objects, or separate client/server assemblies to a
mod. The same class and generated contract run on the appropriate logical side.

To start a new mod from a working contract instead, scaffold the `multiplayer` template:

```sh
topiaforge new mod example.counter --template multiplayer --name "Counter" --author "You" --license AGPL-3.0-or-later
```

It shares a host-owned count through one replicated state and one owner-predicted command with explicit rate and
payload limits, and its tests play two peers, a late joiner, a new session and solo play on the test rig. Run
`topiaforge restore` and then `topiaforge mod sync multiplayer` before you pack it.

## Generated authoring model

Mark one top-level partial class with `[MultiplayerContract(Id = "...")]`; the explicit ID is a permanent wire identity
and therefore never follows class or namespace renames. Payload DTOs are concrete classes with compiler-generated
parameterless constructors and auto-properties or public fields, with
deterministic primitive members; every string or array has `[NetworkBound]`. The generator emits bounded codecs,
namespaced stable IDs, a schema descriptor, state/command/object/event registration, and bound-session typed proxies.

```csharp
[MultiplayerContract(Id = "example.score")]
public partial class ScoreMod : TopiaForgeMod
{
    [ReplicatedState("round")]
    private ReplicatedState<RoundState> round =
        new ReplicatedState<RoundState>(new RoundState());

    protected override void OnLoad()
    {
        // The generated contract is already bound; there is nothing to wire up here.
    }

    [MultiplayerCommand("add-score", Prediction = PredictionMode.Owner)]
    private OperationResult<RoundState> AddScore(
        MultiplayerCommandContext command,
        AddScoreRequest request)
    {
        if (request.Amount < 1 || request.Amount > 100)
            return OperationResult<RoundState>.Failure(
                ModErrorCode.InvalidArgument,
                "Score increments must remain within the canonical bound.");

        var updated = round.Update(current => OperationResult<RoundState>.Success(
            new RoundState { Score = current.Score + request.Amount }));
        if (updated.TryGetValue(out var state))
            EmitShowScore(command, new ScoreFlash { Score = state.Score });
        return updated;
    }

    [PresentationEvent("score-flash")]
    private void ShowScore(ScoreFlash value) =>
        Context.Ui.ShowToast("Score: " + value.Score);
}
```

TopiaForge binds the generated contract after it attaches `Context` and before `OnLoad` runs, so a contract needs no
binding code. A mod that still calls `BindMultiplayer` for the same session gets a harmless success; mods compiled
before automatic binding keep binding themselves.

To run code once per session, implement `partial void OnMultiplayerSessionStarted(MultiplayerSessionSnapshot
snapshot)`, `OnMultiplayerSessionEnded(MultiplayerSessionSnapshot snapshot)`, or both. Started runs on the first
`Ready` snapshot of each session. Ended runs with the last snapshot seen for it when it ends or another session
replaces it. Both run on every peer, so check `snapshot.HasWorldAuthority` before spawning canonical objects. Contracts
without either hook carry no session tracking at all.

Each presentation event also gets `Emit...To` and `Publish...To`, which take a `PresentationTarget` such as
`PresentationTarget.Only(player)` or `PresentationTarget.AllExcept(player)`.

Ordinary commands receive the transport-authenticated `SenderId`; they must validate authorization against canonical
session state. Only replicated-object handlers receive `ReplicatedObjectCommandContext`, whose `TargetObjectId` and
`SenderOwnsTarget` are computed by the provider for the exact canonical object being mutated.

Unsupported native or polymorphic payloads, recursive graphs, unbounded strings/arrays, invalid handler signatures,
duplicate IDs, and generated proxy-name collisions fail compilation. Owner-predicted command and object handlers also
fail compilation when they touch process-local context/services, mutate arbitrary fields, hide work behind author
helpers/getters/constructors, or use random/clock APIs; `command.CreateRandom()` is the prediction-safe source of
random numbers. A warning flags a handler without prediction that drives local UI, input, player, audio, or Unity
state, because only the host would see it. Each code is explained in the
[diagnostics reference](Diagnostics.md#multiplayer-contract-diagnostics). Refresh the checked-in contract lock after
changing a contract:

```sh
topiaforge mod sync multiplayer
```

The command builds the mod, reads generator-owned metadata, and atomically writes the canonical wire IDs and schema
hashes. `topiaforge pack` independently rebuilds and refuses a missing, stale, or edited lock, so protocol changes are
reviewable without making authors maintain hashes by hand.

## Bandwidth budget

A contract's declared client-to-server ceiling is the sum of `MaximumPerSecond` x `MaximumPayloadBytes` across its
commands and replicated objects. TopiaForge budgets **256 kbit/s (32 KiB/s) per connection in each direction**, with a
short burst allowance reserved for join, reconnect, and session-replacement snapshots. `TFMP014` warns when a
contract's declared client-to-server total exceeds that budget. It is a warning, not an error: the attribute defaults
(`MaximumPerSecond = 30`, `MaximumPayloadBytes = 16 KiB`) are permissive placeholders, and most contracts want far
less than either. Declare the rate and payload your commands actually need.

The budget is not part of the wire format. It is never encoded, never included in a schema digest, and never written
to a contract lock, so tightening a declaration to satisfy `TFMP014` changes lock identity only through the
`MaximumPerSecond` and `MaximumPayloadBytes` values you changed.

The other direction is not checked. `[ReplicatedState]` and `[PresentationEvent]` declare no rate, and no contract
declaration expresses participant fan-out, so a provider - not the generator - owns the server-to-client ceiling.
Keep replicated state small and presentation events infrequent; the compiler cannot tell you when you have not.

The generator also owns a wire-format revision that is embedded in every schema digest, descriptor, and contract-lock
entry. Authors never set or copy it. Any change to the generated encoder's byte layout must bump this revision and the
global TopiaForge multiplayer protocol together; a revision bump deliberately changes lock identity even when an
author's DTO shape is unchanged.

## Primary API and provider SPI

Ordinary mods use the five contract-declaration attributes (`MultiplayerContract`, `ReplicatedState`,
`MultiplayerCommand`, `ReplicatedObject`, and `PresentationEvent`), the `NetworkBound` payload constraint,
`ReplicatedState<T>`, generated `BindMultiplayer` and typed proxies, command contexts, replicated-object handles,
confirmations, and session snapshots. The public codec, definition,
registration, raw wire-type, and contract-descriptor surfaces exist so generated consumer code and independently
shipped providers can meet at an assembly boundary. They are marked as advanced generator/provider SPI and hidden
from ordinary IntelliSense. Calling those low-level members directly is not the supported authoring path; doing so
bypasses generator validation and makes the mod responsible for the same bounds, identity, lifecycle, and routing
invariants that generated code normally supplies.

## State, objects, commands, and presentation

- `ReplicatedState<T>` is persistent for the current session and included in snapshots for late join/reconnect.
- `[ReplicatedObject]` turns one transactional `(context, state, input)` handler into a registered wire type plus typed
  `Spawn...Object`, `TryGet...Object`, `Get...Objects`, and `Subscribe...Objects` proxies. Clients reconstruct objects
  through their own generated codecs and handlers; snapshots never carry server delegates.
- `IReplicatedObject<TState,TInput>` represents a server-created object with server ownership or one predictive owner;
  `IsSpawned` and typed change notifications make canonical despawn explicit.
- `MultiplayerCommandContext.SenderId` is transport-authenticated; command payloads never supply their own sender.
- command results are confirmed canonically and report stable `ModErrorCode` failures;
- presentation events are transient accepted effects, deliberately separate from snapshot state. Every peer registers
  the generated bounded codec, while only presentation-capable peers attach the local handler; and
- `MultiplayerSessionSnapshot` exposes readiness, participants, tick, session seed, logical sides, and process kind.

## Session contract for live play

The contract also carries what live sessions need. It is additive, so existing mods compile and load unchanged.

- `command.CreateRandom(salt)` returns a `DeterministicRandom` (xoshiro256** seeded through SplitMix64) built from
  the session seed, the authenticated sender, and the sender-scoped `command.Sequence`. A predicted handler and the
  server's canonical run draw identical values; never seed from `Tick` or wall-clock time. The sequence of values is
  frozen for protocol major version 1.
- `PresentationTarget` adds `Only(participant)` and `AllExcept(participant)` to the `Everyone`, `Sender`, and
  `Others` audiences, both for events emitted from a command and for direct `PublishPresentation` calls.
- `SubscribeTick` delivers canonical fixed steps on the main thread, only where this process holds world authority:
  the host, or single-player play.
- `IReplicatedObject.Apply` changes an object's state transactionally from server code outside an input handler,
  such as a tick subscription.
- `[ReplicatedState]` and `[ReplicatedObject]` accept `Delivery` (`Coalesced` by default, or `Immediate`) and
  `MaximumUpdatesPerSecond` (zero uses the provider's snapshot rate).
- `session.Players` is an `IPlayerDirectory` of `IPlayer` handles. Each handle carries identity, host status, and the
  player's key, which is stable on one host and unlinkable across hosts. Each handle also reads replicated position,
  aim, and health, and routes `Damage`, `Heal`, `Teleport`, per-player `Input`, and per-player `Ui` to the process
  that owns the player. Single-player play has exactly one player, so player-aware code needs no separate path.
- Participants report `IsHost`, a bucketed `Quality`, `PingMilliseconds` rounded to 10 ms, and `PlayerKey`.
  Snapshots report `HostParticipantId`, `TryGetParticipant`, a `Reconnecting` state, and an `EndReason` once ended.

The test rig runs the session engine, so ticks, sequences, seeds, targeted events, and `Apply` behave there as they
will in a live session. The provider runs the same engine in solo sessions. In both, player actions and replicated
presence report `Unavailable` rather than pretending to succeed.

## How sessions work

A mod binds once and keeps the same `IMultiplayerSession` for as long as it runs. Sessions come and go underneath it,
and the snapshot says which one is current, whether this process holds world authority, and who is playing.

- **Solo play.** The game starts in a solo session. This process holds world authority, its player is the host and the
  only participant, and the snapshot is `Ready` from the start. Commands run once, inline, and their confirmations
  report `WasPredicted` as false. A command's presentation events reach the player before its confirmation does.
- **Hosting.** Opening the game to others promotes the running solo session in place. The session id, every replicated
  value and every object stay as they are, and a player who joins receives them. The host keeps world authority, and
  its player stays the host participant. When hosting stops, every other player's session ends and the host plays on
  alone in the same session.
- **Joining.** Joining another host replaces this process's session with the host's. Nothing changes until the
  connection to the host is up, so a join that fails leaves solo play as it was. Once the link is up the snapshot names
  a placeholder session and `HasWorldAuthority` is false; it takes the host's own session id when the host accepts the
  join. The state goes from `Connecting` through `Synchronizing` to `Ready`, which it reaches only once all the host's
  values and objects have arrived. A participant id names a player only on the host that gave it, so the snapshot lists
  nobody until the host's own list arrives, which it does before `Ready`.
- **What replicates.** What a contract declares, and nothing else: `[ReplicatedState]` values, `[ReplicatedObject]`
  objects, commands with their confirmations, and presentation events, which reach players only once the host commits
  them. Unity objects, files, configuration and a mod's own fields stay in their process. Replicating the world itself,
  such as scene props, spawned entities and players' avatars, is later work.
- **Session replacement.** Whenever a session gives way to another, a mod sees the same steps. `CurrentSessionToken` is
  cancelled, every replicated value returns to its declared default, every object despawns with a notification, and
  the snapshot names the new session. `OnMultiplayerSessionEnded` runs with the old session's last snapshot, then
  `OnMultiplayerSessionStarted` runs with the new session's first `Ready` one. Joining, a host that restarts its
  session, and leaving a joined session all take these steps; leaving, however it happens, lands in a fresh solo
  session.
- **Reconnecting.** When the connection to the host goes quiet, the snapshot reports `Reconnecting` and canonical
  values hold still. A connection that recovers carries on. One that drops is tried again every two seconds for 30
  seconds: meanwhile the snapshot reports `Connecting` and still lists the session's players, though none of them as
  this process's own, and submissions still waiting for the host complete as cancelled. The host keeps a player's
  place, participant id included, for 30 seconds after the connection drops, so a reconnect in that window resumes the
  same session as the same player, with the host's current values, and the session hooks do not run. The participant
  list is kept throughout, unless an attempt was lost after it reached the host but before the host's answer came back:
  the host may have restarted meanwhile, so the snapshot then lists nobody until the host's own list arrives, before
  `Ready`. A player who cannot get back in time returns to solo play.

What a mod sees at each step:

| Step | `State` | `HasWorldAuthority` | Session hooks |
| --- | --- | --- | --- |
| Solo play | `Ready` | Yes | Started, for the solo session |
| Opening the session to others | `Ready` | Yes | None: the session goes on |
| Another player joins this host | `Ready` | Yes | None: the participant list grows |
| Joining another host | `Connecting`, `Synchronizing`, then `Ready` | No | Ended for the solo session, Started for the host's |
| The connection to the host goes quiet | `Reconnecting` | No | None |
| The connection drops and comes back in time | `Connecting`, `Synchronizing`, then `Ready` | No | None: the session goes on |
| The host restarts its session | `Synchronizing`, then `Ready`; the host stays `Ready` | Unchanged | Ended, then Started |
| Leaving, a kick or ban, the host stopping, or the reconnect window running out | `Ready`, in a new solo session | Yes | Ended for the host's session, Started for the solo one |

Live sessions on a LAN or at a direct address already exist in the provider, behind a developer switch
(`TOPIAFORGE_LIVE_MULTIPLAYER=1`) until the in-game interface for hosting and joining arrives. That interface, Internet
play through invite codes and a relay, and joining from the launcher are later work.

## The player's own actions

In a session, scripts run on the host and a player's own actions run where that player is. The runtime tells the two
apart by itself, with no code or attribute from you, so that world replication, a later step, can send a joined player's
own spawns to the host instead of making them twice. Offline nothing changes: every call takes effect at once, as it
always has.

- **What counts as the player's action:** your mod's Update and LateUpdate in a frame where one of its `Context.Input`
  actions is pressed, released or held; a `Context.Ui` callback that the player's click, choice, typing or
  confirmation raises; a command the player runs, or that such code runs; and a `Context.Interactions` handler.
- **What keeps it:** everything that code awaits, and everything it schedules through `Context.Scheduler` (`NextFrame`,
  `After`, and each run of `Every`), including what a background task it started hands back through `Context.Scheduler`.
- **What never has it:** a timer your mod's scripted code scheduled, such as one scheduled at load; FixedUpdate; later
  frames; other mods' code; and anything after the game starts or stops hosting, or joins or leaves a session.

Code that looks like the player's action but counts as a script, with its fix:

| If your code | It counts as a script because | Do this instead |
| --- | --- | --- |
| Reads input in FixedUpdate, or in a timer scheduled at load | Only Update and LateUpdate carry the frame's input | Read input in Update or LateUpdate; the build warns about a press read in FixedUpdate ([TF1009](Diagnostics.md#tf1009)) |
| Polls a `PendingOperation<T>` in Update and acts when it completes | The frame it completes in usually has no input of yours | `await` the call in the code that started it, rather than polling |
| Calls a service another mod published | The player's action belongs to your mod, so the other mod's code sees none | Run the other mod's command with `Context.Commands.TryExecute` |
| Subscribes to Update from a click, to act for the player every frame | Frame subscribers are scripts | Schedule the work with `Context.Scheduler.Every` or `NextFrame` from the click |
| Reads Unity's input or runs a Unity coroutine directly, as an `unsafe-native` mod can | The runtime sees neither | Use `Context.Input`, `Context.Scheduler` and `await` |

One case goes the other way: firing a kit control's event from code, such as `Button.onClick.Invoke()`, counts as the
player's click. Call your handler directly instead.

A frame with active input counts as the player's for your whole Update and LateUpdate, so keep scripted work, such as a
timed spawn, in a timer rather than in your input handling.

## What solo cannot tell you

Solo play runs everything once, inline, in one process, with one player and no network. A mod can pass every solo test
and still go wrong in a live session, because solo play never shows these:

| Blind spot | What happens in a live session | How to see it |
| --- | --- | --- |
| Latency | A client's command confirms a round trip later, and its presentation events arrive with the confirmation, not before it. Code that waits on a confirmation waits that long. | Rig conditions with latency, or `WithDownstreamRate(10)` for the live snapshot rate |
| Prediction rollbacks | An owner-predicted handler runs on the client first, then again on the host. When the host refuses it or reaches another result, the client's values roll back to the host's. | A rig client whose command the host refuses, or whose frames `rig.Network` holds back |
| Authority on a client | A client holds no world authority. Spawning, despawning, `Apply` and updates outside a predicted handler return `NotAuthoritative`, and tick handlers do not run. Code that spawns whenever it loads spawns on every player's process. | A rig remote client, and a hook that checks `snapshot.HasWorldAuthority` |
| Late join | A player who joins later receives every replicated value and object, but no presentation event published before it was `Ready`, and nothing a mod kept in its own fields. | `AddRemoteClient` after play has started |
| Reconnects | A dropped connection cancels the submissions still waiting for the host, although the host may have run some of them, and the values come back from the host's snapshot. | `Interrupt` and `Resume`, `Disconnect` and `Reconnect` |
| Session replacement | Values return to their defaults, objects despawn and tokens are cancelled, so handles and values kept from the old session go stale. | `StartNewSession` |
| Several players | Commands from several players interleave, and every limit applies to each sender. A handler must check canonical state rather than assume it. | Several rig clients |

The [test rig](#test-before-transport) shows each of these deterministically, in a unit test, on the same session
engine the provider runs. To see them in the game, two machines on one LAN can host and join each other with the
developer switch. Running several copies of the game on one machine needs a separate folder for each copy;
`topiaforge dev --peers`, which will set those up and join the copies into one session, is later work.

## Test before transport

`MultiplayerTestRig` in `TopiaForge.Mods.Testing` runs the same session engine as the provider, connected by a
deterministic virtual transport instead of sockets. It supplies standalone, remote-client, listen-server, and headless
dedicated-server roles. The virtual transport models the live session channel, which is reliable and ordered: a
delayed, lost, or duplicated frame holds later frames behind it rather than letting them overtake it.

- **Conditions.** `MultiplayerNetworkConditions` sets latency and cadences of loss, duplication, and delay.
  `WithDownstreamRate` adds the live 10 Hz coalescing, and `WithBandwidth` limits each link's byte rate.
- **Scripted faults.** `rig.Network` holds and releases frames, partitions a client, and stalls a process. It can
  also lose a frame for good, which breaks the connection, or replay one, which the channel discards.
- **Connections.** `Disconnect(client)` cancels that client's pending work and releases the objects it owned.
  `Reconnect(client)` installs an exact canonical snapshot before `Ready`, so tests can cover despawns that happened
  while the client was offline. `Interrupt` and `Resume` take a client through `Reconnecting`, and `Kick` ends its
  session. `Kick` and `StartNewSession` throw if called inside a command or tick handler, before they change anything.
- **Sessions.** `StartNewSession(id)` keeps registrations and subscriptions and cancels the old session token. It also
  resets replicated state to its declared default, despawns old objects, and resynchronizes connected clients before
  `Ready`.
- **Accounting.** `Traffic(node)` reports the bytes and frames a node sent and received.
  `CreateOwnerSession(node, modId)` adds a second mod's session to a node.

A session becomes `Ready` only after its canonical snapshot is installed. A client receives presentation events only
after it acknowledges `Ready`, so an event published while it synchronizes is dropped, never delivered late.

Use the real-game provider for ordinary smoke tests in Robotopia. It hosts a solo session: the local player is the
host, canonical work runs once and inline, and a command's events are presented before its confirmation is observed.
It exercises generated serialization and registration without claiming that a live network is present.

Two build-checked first-party examples keep the intended author experience honest:

- `samples/multiplayer/TopiaForge.Multiplayer.CounterSample` covers snapshot state, owner-predicted commands,
  canonical rejection, typed submission, and accepted presentation events.
- `samples/multiplayer/TopiaForge.Multiplayer.DroneSample` covers generated replicated-object type registration,
  typed spawning/discovery, bounded state/input codecs, and predictive ownership. It spawns its drone from
  `OnMultiplayerSessionStarted`, and only when the snapshot holds world authority, so each session the process hosts
  gets one drone and a joining player spawns none.

## Security boundary

Every client command is bounded and rate-limited by the provider and revalidated by the server. Matching package or
content hashes is compatibility and integrity evidence, not anti-cheat. TopiaForge mods remain trusted in-process
code. Live transport, dedicated Robotopia hosting, redistribution, and a genuinely headless Robotopia build remain a
later provider milestone and require the evidence in the
[multiplayer hosting feasibility gate](MultiplayerHostingFeasibility.md).
