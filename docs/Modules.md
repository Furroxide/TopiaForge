---
title: Specialist modules
description: Add optional V1 creator, robot, world, time, prompt, and multiplayer contracts safely.
---

# Specialist modules

Core services are always available on `IModContext`. Specialist features ship as Unity-free
contract packages paired with runtime providers. Add both sides atomically with:

```sh
topiaforge mod add <module>
topiaforge restore
```

The first command updates the exact SDK package reference and the canonical root manifest
dependency. The analyzer reports `TF1004` when a module contract assembly is referenced without a
matching key in `dependencies` or `optionalDependencies`; descriptions and `x-*` metadata cannot
spoof that declaration.

| Module | Add command | Main contracts | Runtime dependency |
| --- | --- | --- | --- |
| RobotKit | `topiaforge mod add robotkit` | `IRobotAgentService`, objectives, targets, dialogue, voice, brain queries | `io.github.furroxide.topiaforge.robotkit` |
| Worlds | `topiaforge mod add worlds` | `IWorldGamemodeService`, world content, pause actions, shops, sessions | `io.github.furroxide.topiaforge.worlds` |
| Chronos | `topiaforge mod add chronos` | `ITimeControlService`, time leases, drivers, turn scheduler | `io.github.furroxide.topiaforge.chronos` |
| Creator Content | `topiaforge mod add creatorcontent` | Catalog registrations, creator sessions, project library, mutation safety, F5 host routing | `io.github.furroxide.topiaforge.creatorcontent` |
| Prompts | `topiaforge mod add prompts` | `IPromptOverrideRegistry`, override leases, conflict diagnostics | `io.github.furroxide.topiaforge.prompts` |
| Multiplayer | `topiaforge mod add multiplayer` | Sessions, participants, replicated state/objects, commands, prediction, presentation events | `io.github.furroxide.topiaforge.multiplayer` |

## Resolve a provider

Required providers use `Context.RequireExtension<T>()`. Optional integrations use
`Context.TryGetExtension<T>(out provider)`. Resolution is dependency-scoped: a mod cannot discover
private providers from unrelated packages, and only assemblies declared in `apiAssemblies` form a
compile-time dependency surface.

Provider selection is deterministic. Singleton contracts reject duplicates; multi-provider
contracts return providers in normalized identity order through `Context.Extensions.GetAll<T>()`.
An optional dependency that is absent or fails validation does not block unrelated mods.

The service template contains both sides of this pattern. The provider registration below comes
from the compiled scaffold and is released automatically with its lifetime:

<!-- topiaforge-snippet path="templates/mod/service/{{TYPE_NAME}}Mod.cs" -->

## RobotKit

RobotKit exposes robots as typed `IRobotAgent` entities. A mod can spawn a standard robot, observe
movement, assign objectives and targets, run dialogue, request voice input, and perform
structured brain queries. Provider availability and operation results let a mod degrade cleanly
when a particular Robotopia binding is unavailable.

Remote dialogue and voice features are opt-in. Declare every network, remote inference, player
token, microphone, and speech-to-text capability that the player-facing behavior exposes. Keep a
deterministic local fallback.

## Worlds

Worlds owns definitions, menu entries, scene transitions, and one current `WorldSession`.
Register worlds and gamemodes with returned `IWorldRegistration` handles; do not build a parallel
scene coordinator. The `gamemode` and `world` templates demonstrate lifetime-owned registration
and session-aware teardown.

## Chronos

Chronos coordinates freeze, slow motion, player exemption, driver-based scaling, bounded stepping,
and turn scheduling. Every effect is a lease, so several mods compose without last-writer-wins
state and prior state is restored as leases are released.

## Creator Content

Creator Content authenticates namespaced catalog registrations, owns bounded reversible creator sessions, stores
local visual event projects, and routes one shared configurable F5 action to the highest-priority eligible host.
Factories run through the registering mod's own safe asset/entity services. Explicit reversible native adapters use
the separate owner-bound `ICreatorSceneAdapterRegistry`; the provider wraps their targets, bounds discovery, validates
safe duplicate recipes, and enforces exclusive temporary edits. Arbitrary native scans, cross-package loading, and
custom graph callbacks are rejected. See [Sandbox](Sandbox.md).

## Prompts

Prompts registers replacements by stable prompt id. Priority and normalized provider identity
select a deterministic winner, and `GetConflicts()` exposes competing registrations for
diagnostics. Keep the returned handle only when you need early release.

`WellKnownPromptIds.GlobalRobotDirective` is the shared, optional directive slot for robot inference. The Prompts
provider appends its effective value to native Robotopia planning, while RobotKit appends the same live value to
structured brain and conversation requests. The directive augments the prompt; it never replaces personality,
grounded facts, action schemas, or structured-output requirements. Registrations remain owner-bound and changes take
effect dynamically, so unloading the consumer restores normal planning without restarting either provider.

## Multiplayer

Multiplayer is a stable API preview with a generated contract, standalone loopback provider, and deterministic
multi-peer test rig. The add command keeps the mod on Manifest V5, pins all three multiplayer components to the same
release, and adds multiplayer metadata; removing
the module leaves a valid standalone V5 manifest. Shared state is
server-canonical with optional owner prediction. See [Multiplayer API preview](Multiplayer.md) and
[Manifest V5](ManifestV5.md). Live transport is not part of TopiaForge 1.0.

Advanced native interop is deliberately not a specialist safe module. Read
[Advanced interop](UnityInterop.md) before adding that separate package.
