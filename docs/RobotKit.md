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
| `IRobotSceneEditorService` | Optional discovery and exclusive temporary edit leases for approved native and RobotKit-managed robots. |
| `IRobotEditTarget` / `IRobotEditLease` | Opaque scene target plus snapshot-backed transform, brain-mode, and autonomous-personality previews with conflict-safe restoration. |

The scene editor is additive: existing `IRobotAgent` and `IRobotAgentService` implementations do not gain new members.
Every native edit is temporary and exclusive. A lease snapshots each reversible property once, restores changes in
reverse order, and leaves a property untouched if another system changed it after the preview. `RobotPersonalityDraft`
uses the verified `PersonalityAsset`/`LLMAgent` surface for autonomous behavior; Creator conversations apply the same
persona to their explicit `RobotConversationRequest`.

Every operation either uses a cheap `Try...` query, returns `OperationResult<T>`, or returns
`Task<OperationResult<T>>` with lifetime cancellation. Check provider availability and
`Context.Runtime.UnavailableCapabilities` before exposing a feature in Robotopia's UI.

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
remain unqualified: report it unavailable if absent, rather than relabeling the default robot.

`FindReachableSpawnAsync` automatically links cancellation to the consuming lifetime; an optional
caller token can end the search sooner. Check its result, then spawn on collision-clear ground at
the returned position. Check `Spawn` before issuing `MoveTo`.
Retain and dispose the returned agent when its behavior ends; world replacement or scope teardown
must retire it. An unreachable route needs a bounded retry or an explicit unavailable-order state.

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

Guest-origin robot spawns use `RobotBrainMode.Dormant` with native talk disabled on the host and every replica.
A guest's `SetBrainMode(Autonomous)` or native-talk-enabling `SetInteraction` returns `NotAuthoritative`.
Movement, appearance and other non-AI controls remain available within the usual ownership and lifetime rules.
Host-owned, offline and trusted server operations retain Autonomous brains and native conversation support.
