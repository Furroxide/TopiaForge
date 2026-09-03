---
title: Multiplayer API preview
description: Build and deterministically test server-canonical TopiaForge mods for Robotopia before live transport ships.
---

# Multiplayer API preview

TopiaForge remains standalone-only. `TopiaForge.Mods.Multiplayer` freezes the future-facing public contract,
generated wire format, real-game loopback provider, and deterministic multi-peer test rig before a supported live
transport exists. Ordinary V5 mods keep standalone behavior when `multiplayer` is omitted; multiplayer support is an explicit
module and manifest opt-in, not a claim that arbitrary local mutations can be synchronized safely.

`IMultiplayerSession` is a stable owner-scoped facade, not one match object. Bind the generated contract once for the
mod lifetime. Its registrations survive sequential sessions; `Snapshot.Id` changes and `CurrentSessionToken` cancels
when a match is replaced, while a new session starts from each replicated state's immutable declared default.

The architecture preserves the normal TopiaForge boundary: one BepInEx runtime sits behind Unity-free, owner-scoped
contracts, deterministic launcher/profile tooling, and optional specialist modules. Multiplayer identities and roles
therefore live in this module, not on `IModContext`, `IEntity`, `PlayerSnapshot`, or `WorldSession`.

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

This adds the stable contract, source generator, provider dependency, manifest multiplayer metadata, and generated contract
lock. Resolve the provider through the existing extension boundary:

```csharp
var multiplayer = Context.RequireMultiplayer();
```

Do not add sockets, Unity Netcode types, raw RPC numbers, transport objects, or separate client/server assemblies to a
mod. The same class and generated contract run on the appropriate logical side.

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
        var binding = BindMultiplayer(Context.RequireMultiplayer());
        if (!binding.TryGetValue(out var lease))
            throw new InvalidOperationException(binding.ErrorMessage);
        Context.Lifetime.Track(lease);
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

Ordinary commands receive the transport-authenticated `SenderId`; they must validate authorization against canonical
session state. Only replicated-object handlers receive `ReplicatedObjectCommandContext`, whose `TargetObjectId` and
`SenderOwnsTarget` are computed by the provider for the exact canonical object being mutated.

Unsupported native or polymorphic payloads, recursive graphs, unbounded strings/arrays, invalid handler signatures,
duplicate IDs, and generated proxy-name collisions fail compilation. Owner-predicted command and object handlers also
fail compilation when they touch process-local context/services, mutate arbitrary fields, hide work behind author
helpers/getters/constructors, or use random/clock APIs. Refresh the checked-in contract lock after changing a contract:

```sh
topiaforge mod sync multiplayer
```

The command builds the mod, reads generator-owned metadata, and atomically writes the canonical wire IDs and schema
hashes. `topiaforge pack` independently rebuilds and refuses a missing, stale, or edited lock, so protocol changes are
reviewable without making authors maintain hashes by hand.

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

## Test before transport

`MultiplayerTestRig` in `TopiaForge.Mods.Testing` supplies standalone, remote-client, listen-server, and headless
dedicated-server roles. It can drive deterministic latency, loss, duplication, and reordering; late joins and
reconnects; accepted/rejected prediction; rollback/replay; ownership; and stale input. A session becomes `Ready` only
after its canonical snapshot is installed. `Disconnect(client)` cancels that client's pending work and updates
participant connectivity; `Reconnect(client)` installs an exact canonical state/object snapshot before returning to
`Ready`, so tests can cover ownership release and despawns that occurred while the client was offline. Transient
presentation events are dropped before `Ready`, deduplicated under reliable retries, and scoped to one connection
generation. `StartNewSession(id)` exercises the stable-facade contract: registrations remain active, the old session
token cancels, replicated state resets to its declared default, old objects/subscriptions retire, and connected
clients receive the replacement snapshot before `Ready`.

Use the real-game loopback provider for ordinary Robotopia smoke tests. It executes server-canonical work once in the
standalone process and exercises generated serialization and registration without claiming that a live network is
present.

Two build-checked first-party examples keep the intended author experience honest:

- `samples/multiplayer/TopiaForge.Multiplayer.CounterSample` covers snapshot state, owner-predicted commands,
  canonical rejection, typed submission, and accepted presentation events.
- `samples/multiplayer/TopiaForge.Multiplayer.DroneSample` covers generated replicated-object type registration,
  typed spawning/discovery, bounded state/input codecs, and predictive ownership.

## Security boundary

Every client command is bounded and rate-limited by the provider and revalidated by the server. Matching package or
content hashes is compatibility and integrity evidence, not anti-cheat. TopiaForge mods remain trusted in-process
code. Live transport, dedicated Robotopia hosting, redistribution, and a genuinely headless Robotopia build remain a
later provider milestone and require the evidence in the
[multiplayer hosting feasibility gate](MultiplayerHostingFeasibility.md).
