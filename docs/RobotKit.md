---
title: RobotKit
description: Build Robotopia robot, objective, dialogue, voice, and story features through safe V1 contracts.
---

# RobotKit

RobotKit is the optional V1 module for working with Robotopia's robots through typed entities,
movement, health, targets, objectives, interaction, dialogue, voice input, and structured brain
queries. Robotopia and Unity native objects never cross its contract boundary.

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
| `IRobotAgent` | Opaque entity identity, body/health state, movement intent, gait, target, damage, and interaction options. |
| `IRobotObjectiveService` | Register named targets and run lifetime-owned go-to, follow, patrol, wander, flee, and reprogram objectives. |
| `IRobotConversationService` | Run bounded multi-turn dialogue with closed-set decisions. |
| `IPlayerDialogueInputService` | Typed text helpers and optional push-to-talk capture/transcription. |
| `IRobotBrainQueryService` | Asynchronous structured queries with typed output fields and stable failures. |

Every operation either uses a cheap `Try...` query, returns `OperationResult<T>`, or returns
`Task<OperationResult<T>>` with lifetime cancellation. Check provider availability and
`Context.Runtime.UnavailableCapabilities` before exposing a feature in Robotopia's UI.

## Deterministic Robotopia gameplay first

Remote brain text is untrusted presentation. Drive Robotopia state only from bounded, validated fields
or a closed set of decisions, and keep a deterministic local fallback for missing token, offline,
timeout, cancellation, malformed response, or an unavailable Robotopia binding.

Declare `network`, `remote-ai`, and `player-token` when a Robotopia mod uses remote brain calls for
a player-facing feature. Add `microphone` and `speech-to-text` for voice capture. Features remain
opt-in and off by default until the Robotopia player enables them.

## Lifetime and testing

Spawned agents, conversations, voice captures, objective handles, and registrations are lifetime
owned. Dispose a handle only when the behavior ends before mod unload. Use `FakeModContext` plus the
RobotKit test fakes to cover unavailable providers, operation failure, cancellation during unload,
and leak-free reload.

See [Specialist modules](Modules.md#robotkit), [Privacy and capability disclosure](PrivacyAndCapabilities.md),
and the generated C# API reference.
