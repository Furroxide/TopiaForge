---
title: RobotKit
description: Build Robotopia robot, objective, dialogue, voice, and story features through safe SDK contracts.
---

# RobotKit

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
