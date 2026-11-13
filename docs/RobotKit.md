---
title: RobotKit
description: Build Robotopia robot, objective, dialogue, voice, and story features through safe SDK contracts.
---

# RobotKit

For separately spawned, scope-owned powered-down bodies, see [Persistent cosmetic bodies](PersistentBodies.md).

RobotKit is the optional V1 module for working with Robotopia's robots through typed entities,
movement, damage and death, targets, objectives, interaction, dialogue, voice input, and structured
brain queries. Robotopia and Unity native objects never cross its contract boundary.

## Add RobotKit

```sh
topiaforge mod add robotkit
topiaforge restore
```

This adds exact `TopiaForge.Mods.RobotKit` compile-time contracts and the
`io.github.furroxide.topiaforge.robotkit` runtime dependency together. Resolve required services
with `Context.RequireExtension<T>()`; use `Context.TryGetExtension<T>(out provider)` for an optional
integration.

## Service groups

| Contract | Purpose |
| --- | --- |
| `IRobotAgentService` | Discover robot types, find reachable positions, spawn robots, and map entity handles back to agents. |
| `IRobotPlayerEntitySource` | Optional live-player entity identity for native chase/target tracking, exposed compatibly through `TryGetPlayerEntity`. |
| `IRobotAgent` | Opaque entity identity, movement intent, gait, target, damage and death, and interaction options. |
| `IRobotObjectiveService` | Register named targets and run lifetime-owned go-to, follow, patrol, wander, flee, and reprogram objectives. |
| `IRobotConversationService` | Run bounded multi-turn dialogue with closed-set decisions. |
| `IPlayerDialogueInputService` | Typed text helpers and optional push-to-talk capture/transcription. |
| `IRobotBrainQueryService` | Asynchronous structured queries with typed output fields and stable failures. |
| `IRobotSceneEditorService` | Optional discovery and exclusive temporary edit leases for eligible native scene robots. |
| `IRobotEditTarget` / `IRobotEditLease` | Opaque scene target plus snapshot-backed transform, brain-mode, and autonomous-personality previews with conflict-safe restoration. |

Use the scene editor for eligible native scene robots; RobotKit-managed robots are not in `Targets`. `TryResolve`
turns a RobotKit robot into a target that allows only a temporary personality preview: move it and change its brain
through the routed `IRobotAgent` controls. A personality preview, or waking a robot's native brain, is refused with
`NotAuthoritative` on a guest's PC and in host code that runs on a guest's input, so a robot never runs text a guest
wrote.

Every scene edit is temporary and exclusive. A lease snapshots each reversible property once, restores changes in
reverse order, and leaves a property untouched if another system changed it after the preview. If the target comes under
RobotKit management, further previews fail. Restoring or disposing the lease then releases its resources without
applying the captured state.

`RobotPersonalityDraft` uses the verified `PersonalityAsset`/`LLMAgent` surface for autonomous behavior. Creator
conversations apply the same persona to their explicit `RobotConversationRequest`.

Use `IRobotAgentService` and its `IRobotAgent` handles on the Robotopia game thread. If work resumes
on a worker thread, queue the robot work with `Context.Scheduler.NextFrame` and check its result.
The service rejects off-thread calls with `TFSDK100` before it reads the scene or changes the owned robot list.
Start `FindReachableSpawnAsync` on the game thread as well.

Check provider availability and `Context.Runtime.UnavailableCapabilities` before exposing a feature
in Robotopia's UI. Commands report expected failures through `OperationResult<T>`.

## Custom enemy health

RobotKit does **not** expose a robot's hit points. `IRobotAgent` offers `ApplyDamage` and `Kill`, which
drive the native hurt reaction and ragdoll — they are feedback, not a health model you can read back.

A gamemode that needs enemies with their own durability tracks hit points mod-side and calls into RobotKit
only for presentation and defeat: subtract from your own value, call `ApplyDamage` so the robot visibly
reacts, and call `Kill` when your value reaches zero. `HeadPosition` gives you a hit zone to test against
for headshots. [`ZombieEnemy.cs`](../mods/TopiaForge.Zombies/ZombieEnemy.cs) is the worked example —
per-archetype health, damage source attribution, timed states, and defeat all live in the mod.

## Track the live player

RobotKit providers can expose the current player as a safe `IEntity`. Use the compatibility extension
on `IRobotAgentService`; it returns `false` with older providers and while no live player exists:

```csharp
var robots = Context.RequireExtension<IRobotAgentService>();
var spawned = robots.Spawn(new RobotAgentSpawnRequest(spawnPosition));
if (spawned.TryGetValue(out var robot)
    && robots.TryGetPlayerEntity(out var player)
    && player != null)
{
    robot.Chase(player);
}
```

The entity remains backed by Robotopia's moving player, so `Chase` does not need a per-frame
position rewrite. A scene transition or native player recreation can invalidate the old handle;
check `IsAlive`, call `TryGetPlayerEntity` again, and rebind the target when that happens. Keep a
position-based behavior as the graceful fallback when the optional capability is absent.

## NPCs in imported map worlds

Use the same RobotKit spawn service after the imported world's scene and navigation are ready.
Resolve it through the consuming `Context`, so the runtime retains that context's authority and lifetime.
For shared NPCs, spawn from the gamemode server controller. Brains run on the host; replicated bodies
stay dormant. Entity references, late joins, pose replication and consuming-scope cleanup are automatic.
Authors do not register a second replica factory or send spawn packets.

Preflight the required cast against `RobotTypes` in the loaded scene and retain its descriptors.
Pass the selected descriptor's `Id` as `robotTypeId`. An omitted type selects the default;
an explicit unavailable type returns `Unavailable`, without silently substituting another robot.
Discovery describes loaded native prefabs, not a promise that every named character exists in every map.
Robot01 and CopBot have navigated sampled imported-map routes. MuniciPal discovery and spawning
remain unqualified on build 2597: report it unavailable if absent, rather than relabeling the default robot.
The synchronous composed factory uses 2597's private catalog-reference collector. Its personality and
about dependencies are assumed to be distinct from body-part and voice-map resolution; one 2597 host
start is still needed to confirm it. A nonblank personality value
does not establish an inline prompt. The current synchronous path does not prepare missing catalog assets:
unresolved dependencies refuse discovery. Character data and symbolic material values pass through unchanged.
This bounded preflight does not establish that a native character's catalog assets have been prepared or
that a character is qualified on 2597.

Composed MuniciPal discovery is available only during `StartingMode` and `Running` for the current
live map world. Its native composed body has no `Health`: `ApplyDamage` returns `Unavailable` and `Kill`
despawns it. Native prefabs are rescanned every two seconds only while none has loaded; once one is
cached, a current composition ends the rescans. A validated catalogue with no MuniciPal entry backs off
from two seconds to at most thirty seconds; scene/world/session publications reset that delay.
Other refusals keep the ordinary two-second retry so temporarily incomplete data can recover.

Refusals appear at Info as `RobotKit composed discovery refused: <code>.`, at most once per code for the
current world/session. Codes identify unavailable scenes, host count, map root, entry budget, malformed scene,
missing scene identity, entity shape, asset references, ambiguous identity, absent character or native binding
failure. Reference-specific codes distinguish unexpected project catalogues, reference DTO shape,
body-part resolution, voice resolution, invalid collector results and unavailable catalog assets.
They contain no asset identifiers, native payloads, paths or session values. Check game compatibility
for `NativeBinding`; for structural codes check the imported world's supported composition data.
`CatalogAssetsUnavailable` comes from the game's own catalogue, so no map change can fix it.

`FindReachableSpawnAsync` automatically links cancellation to the consuming lifetime; an optional
caller token can end the search sooner. Check its result, then spawn on collision-clear ground at
the returned position. Check `Spawn` before issuing `MoveTo`.
Retain and dispose the returned agent when its behavior ends. World replacement and scope teardown retire
its handle. The owning code can still call `Despawn()` on the game thread to release that robot after
retirement. `Dispose()` always releases the handle where you are. `Despawn()` asks the host to remove
the robot: inside a guest's callback it can be refused, and the handle stays until you dispose it.
Repeated cleanup calls succeed with `false` after the handle has been released. A retired
client handle releases its local body without routing another robot command. An unreachable route needs
a bounded retry or an explicit unavailable-order state.

`HasReachedTarget` accepts a successful native walk completion as well as proximity within the
horizontal stop threshold (at least 0.1 metres). The adapter trusts successful native completion
even just outside the requested distance, without increasing the author's 0.75-metre stop distance.
Ignored results, faults and cancelled/replaced intents do not count as native arrival.
Reissuing the same destination preserves arrival; a changed destination starts a new intent.
The existing 0.5-metre release hysteresis allows pursuit to resume when the target moves away.
`IsMoving` remains true while a native walk is still pending, including while it settles near a target.

This spawn path does not import an entity overlay.

## Robot speech

Call `await agent.SayAsync(reply, cancellationToken)` on an agent from the owning host context.
RobotKit selects listeners within 20 metres using current-world validated presence, privately delivers
only the bounded line and its native voice fingerprint, and synthesizes locally on each eligible peer.
Native `AgentHead.Say` calls on these owned robots use the same route automatically. Guests cannot
originate NPC speech and their replicas remain dormant. Authors write no packet, recipient list or token plumbing.

Lines allow up to 512 plain-text characters. A new line replaces the previous one; cancellation,
despawn, scope disposal, hiding and world changes stop playback. Missing bindings return `Unavailable`;
retry a fresh line after recovery. Delivery is best effort: full windows, expired lines and late joins
do not replay speech. A successful await waits for every admitted listener's native playback completion (remote listeners
report theirs) and the one-second speech pace. `true` means those playback tasks completed; `false`
means no listener was admitted and the pace elapsed without synthesis. Sequential awaited lines do not
cut off their predecessor, including when the host is far away. Missing reports or listener loss fail
within the 15-second lifetime; task completion is not proof of audible sound. Native subtitle-only or
pre-recorded-clip overrides are outside this replicated speech contract.

See [the private speech contract](https://github.com/Furroxide/TopiaForge/blob/c43ed92e963b0f662996eb6649f412e1d9176485/docs/internal/RobotSpeech.md) for bounds, recovery and native acceptance checks.

## Deterministic Robotopia gameplay first

Remote brain text is untrusted presentation. Drive Robotopia state only from bounded, validated fields
or a closed set of decisions, and keep a deterministic local fallback for missing token, offline,
timeout, cancellation, malformed response, or an unavailable Robotopia binding.

Declare `network`, `remote-ai`, and `player-token` when a mod for Robotopia uses remote brain calls for
a player-facing feature. Add `microphone` and `speech-to-text` for voice capture. Features remain
opt-in and off by default until the Robotopia player enables them.

## Lifetime and testing

Spawned agents, conversations, voice captures, objective handles, and registrations are lifetime
owned. Dispose a handle only when the behavior ends before mod unload. Use `FakeModContext` plus the
RobotKit test fakes to cover unavailable providers, operation failure, cancellation during unload,
and leak-free reload.

See [Specialist modules](Modules.md#robotkit), [Privacy and capability disclosure](PrivacyAndCapabilities.md),
and the generated C# API reference.

### Native brains in multiplayer

RobotKit checks the origin of each spawn and routed control call, including calls made through a retained
host-owned handle. Guest origin includes a client process and host callbacks dispatched for a remote player,
such as remote UI controls, window dismissals and forwarded entity commands. Running inside a gamemode's
server controller does not remove that restriction. Nested synchronous callbacks retain it; a captured
continuation retains the forwarded origin when it returns to the owning thread.

- A host callback acting for a guest cannot spawn `RobotBrainMode.Autonomous`: `Spawn` returns
  `NotAuthoritative` without creating a robot or a replication record.
- A `Dormant` request in that callback still succeeds. Native talk is disabled, including the constructor's
  default native-talk policy; custom interactions are preserved. This normalization is unchanged.
- Direct client spawns retain defensive normalization to Dormant with native talk disabled. The canonical
  host body created from a guest's wire request uses the same safe settings, and every replica is Dormant.
- `SetBrainMode(Autonomous)` and native-talk-enabling `SetInteraction` return `NotAuthoritative` for clients
  and guest-origin host callbacks. Movement, appearance and other non-AI controls retain their normal
  ownership and lifetime rules.

Host local-player actions and host or solo calls outside guest-origin callbacks retain Autonomous brains
and native conversation support. There is no guest-autonomy opt-in. For a guest-triggered feature, request
Dormant explicitly and check the spawn result before using the handle. These rules cover RobotKit spawning
and routed agent controls.

#### Host decisions that start with a guest's click

Guest origin follows the work, not the code's role. Work that a guest-origin callback schedules with
`Context.Scheduler` (`NextFrame`, `After` or `Every`) or continues after an `await` keeps the guest's origin,
so it is refused in the same way. When the decision belongs to the host, such as a round that starts with
autonomous patrols once every player has pressed Ready, let the click record the request only, and act on
it from host code that the server controller registered when it started:

```csharp
private sealed class Round : IGamemodeController
{
    private readonly IRobotAgentService robots;
    private readonly IDisposable? timer;
    private bool readyRequested;

    public Round(IGamemodeSession session, IRobotAgentService robots)
    {
        this.robots = robots;
        // Registered while the controller starts, outside every guest callback.
        timer = session.Context.Scheduler.Every(TimeSpan.FromSeconds(0.1), StartWhenReady).Value;
    }

    // Wire this to the Ready button. It may run for a guest, so it only records the request.
    public void ReadyPressed() => readyRequested = true;

    private void StartWhenReady()
    {
        if (!readyRequested) return;
        readyRequested = false;
        if (!EveryPlayerReady()) return;
        var patrol = robots.Spawn(new RobotAgentSpawnRequest(PatrolStart, brainMode: RobotBrainMode.Autonomous));
        // Check the result before using the handle.
    }

    public void Dispose() => timer?.Dispose();
}
```

The host's own state decides whether the round starts and what it spawns. Take nothing from the guest's
click except the request itself. Register the timer from code that a guest's input did not start: a timer
registered inside a guest-origin callback keeps that guest's origin for as long as it runs.

The testing kit follows these rules for `FakeRobotAgentService` and its agents. Set `IsClient = true` to
model a guest process. Remote `FakePlayerDirectory` surface actions establish guest origin automatically;
`FakeUiSurface.DismissFromPlayer()` simulates a player closing the window, while `Hide()` models a host call.
Use `agents.RunAsGuest(() => ...)` for other forwarded host callbacks. Nested local surface actions cannot
remove the outer guest origin. Captured execution contexts retain it on the originating thread, and return or
exception cleanup restores the caller's scope. The thread that constructs the fake agent service owns guest
origin: `RunAsGuest` on another thread, and a spawn or native-brain call on another thread while a guest
scope is visible, throw `InvalidOperationException` instead of answering as the host. An async test must
resume on the constructing thread before it acts for a guest. `FakeModContext.Scheduler` carries guest origin
like the runtime: `NextFrame`, `After` and `Every` work scheduled inside a guest scope runs with that guest's
origin when the test advances the scheduler, and work scheduled outside one runs as the host. A continuation
after `await Scheduler.DelayAsync(...)` resumes on a pool thread, so a robot call there throws; schedule the
follow-up with `NextFrame` or `After` instead. Fake modal completions need an explicit `RunAsGuest` scope.
