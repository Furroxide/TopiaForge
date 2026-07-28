---
title: Zombies worked example
description: Learn how the first-party wave-survival gamemode composes safe TopiaForge services into a complete Robotopia experience.
---

# Zombies: a complete safe-SDK gamemode

Zombies is the first-party worked example for building a full Robotopia gamemode without importing
Unity, game assemblies, reflection, or native object types into consumer code. It composes Worlds,
RobotKit, Chronos, named input, physics, player health, audio, diagnostics, configuration, and
TopiaForgeUi behind the same public contracts available to community mods.

The result is more than a spawning demo: it has an escalating wave loop, four infected archetypes,
custom health, headshots, charged piercing fire, combo scoring, a between-wave economy, recruitable
allies with loyalty, deterministic stand-down mechanics, optional live conversations, accessibility
settings, retained game-over actions, and clean restart/exit behavior.

## Player loop

1. Worlds starts a Zombies session in the configured arena and waits until the active gameplay scene,
   player, and RobotKit services are ready.
2. A short preparation timer leads into a wave budget. Reachable spawn searches place infected robots
   outside the player's minimum safety radius while an alive cap keeps pressure bounded.
3. The SDK zapper supports quick shots, charged piercing shots, headshots, knockback, ragdoll reactions,
   score chains, and credits.
4. Between waves, FIELD REQUISITIONS pauses the world and sells run-scoped hull, weapon, combo, and
   uplink upgrades.
5. The uplink can issue a deterministic nearby stand-down or influence one targeted robot. Converted
   allies acquire hostile targets, fight, gain or lose loyalty, and can defect if neglected.
6. At zero integrity, the run freezes behind a persistent game-over window. Restart restores a clean
   session; return-to-menu requires confirmation and owns cancellation through the scene transition.

The HUD displays the effective bindings registered with the input service. Defaults are:

| Action | Default | Availability |
| --- | --- | --- |
| Fire / charge zapper | Primary mouse button | Active wave |
| JACK IN targeted robot | `E` | Active wave; uplink enabled |
| Broadcast stand-down | `Q` | Active wave; enough charge |
| FIELD REQUISITIONS | `B` | Preparation and inter-wave phases |
| Voice capture | Hold `V` | Optional live conversation with voice enabled |

Text and voice modes switch through an explicit button in the JACK IN window, preserving keyboard
focus traversal while the push-to-talk action remains available under UI focus.

Live brain and voice features are disabled by default. The deterministic shooter, broadcast, shop,
and ally systems remain fully playable offline. Even when live conversation is enabled, remote text is
presentation: bounded local decisions, archetype resistance, disposition thresholds, and the ally cap
remain authoritative.

## How the pieces fit

| Source | Responsibility | Pattern to reuse |
| --- | --- | --- |
| [`ZombiesMod.cs`](../mods/TopiaForge.Zombies/ZombiesMod.cs) | Config migration, service discovery, and gamemode registration through `GamemodeHost<T>` | Keep the entry point thin; let the SDK own session wiring rather than hand-writing it. |
| [`ZombiesController.cs`](../mods/TopiaForge.Zombies/ZombiesController.cs) | Session dependencies, construction, command surface, and idempotent teardown | Keep the coordinator's public surface small and make every acquired resource visible at construction or disposal. |
| [`ZombiesController.Loop.cs`](../mods/TopiaForge.Zombies/ZombiesController.Loop.cs) | Phase dispatch and scaled/unscaled clock selection | Keep the frame callback as orchestration; move feature rules into named methods and models. |
| [`ZombiesController.Waves.cs`](../mods/TopiaForge.Zombies/ZombiesController.Waves.cs) and [`ZombiesController.Enemies.cs`](../mods/TopiaForge.Zombies/ZombiesController.Enemies.cs) | Wave budgets, bounded spawning, pursuit, attacks, and roster cleanup | Bound work per frame and drive every asynchronous SDK call with `PendingOperation<T>`. |
| [`ZombiesController.Session.cs`](../mods/TopiaForge.Zombies/ZombiesController.Session.cs) and [`ZombiesController.Health.cs`](../mods/TopiaForge.Zombies/ZombiesController.Health.cs) | Scene/player readiness, restart/return transitions, and exact native-health restoration | Capture host state before mutation and restore the captured value on every exit path. |
| [`ZombiesController.Combat.cs`](../mods/TopiaForge.Zombies/ZombiesController.Combat.cs) | Input, ray targeting, damage, reactions, combo score, and credit awards | Resolve opaque SDK entity handles back to stable RobotKit agents. |
| [`ZombiesController.Uplink.cs`](../mods/TopiaForge.Zombies/ZombiesController.Uplink.cs) | Deterministic influence, live-conversation resolution, ally caps, and fleeing | Treat remote output as untrusted input to an engine-owned state machine. |
| [`ZombieEnemy.cs`](../mods/TopiaForge.Zombies/ZombieEnemy.cs) | Per-enemy health, timed states, loyalty, targeting, and defeat | Put entity-local rules in a model instead of growing the frame loop. |
| [`ZombiesShopController.cs`](../mods/TopiaForge.Zombies/ZombiesShopController.cs) | Wallet, catalog UI, purchase limits, and pause lease | Pair modal gameplay UI with scoped time/player-control ownership. |
| [`ZombiesHudPresenter.cs`](../mods/TopiaForge.Zombies/ZombiesHudPresenter.cs) | Dirty-checked HUD projection and effective binding labels | Rebuild presentation only when its immutable snapshot changes. |
| [`ZombiesConversationController.cs`](../mods/TopiaForge.Zombies/ZombiesConversationController.cs) | Bounded asynchronous turns, voice capture, timeout, and deterministic fallback | Link every asynchronous operation to the mod lifetime and release focus early. |
| [`ZombiesConfig.cs`](../mods/TopiaForge.Zombies/ZombiesConfig.cs) | Serialized configuration shape and mod-author comments | Keep persisted data easy to scan; do not mix its declarations with validation machinery. |
| [`ZombiesConfig.Defaults.cs`](../mods/TopiaForge.Zombies/ZombiesConfig.Defaults.cs) | Constructor/deserializer defaults and schema migration | Seed defaults before `DataContract` deserialization so omitted members behave like fresh config. |
| [`ZombiesConfig.Validation.cs`](../mods/TopiaForge.Zombies/ZombiesConfig.Validation.cs) | Finite-value recovery, identifier cleanup, key normalization, and bounds | Normalize every external value at one boundary before gameplay reads it. |

## Use this layout as a blueprint

Start a full gamemode in the same order that Zombies does:

1. Declare versioned dependencies and optional capabilities in the manifest.
2. Keep the `TopiaForgeMod` entry point focused on configuration, service discovery, registrations,
   and creating one session coordinator.
3. Give persisted configuration separate shape, defaults/migration, and validation responsibilities.
4. Model the run as explicit phases. Use world-scaled time for simulation and unscaled time only for
   controls, transitions, and UI that must remain responsive while paused.
5. Put entity-local state in domain models and independently owned UI/async flows in collaborators.
   Use responsibility-named partial files only for a coordinator whose methods genuinely share the same
   session state; do not use partials to hide unrelated classes.
6. Acquire SDK resources through owner-scoped services, retain their handles, cancel pending work, and
   make `Dispose` safe to call after partial construction or more than once.
7. Build the feature against `FakeModContext` first, then add focused tests for success, cancellation,
   partial-load failure, repeated reloads, numeric boundaries, and exact host-state restoration.

Every non-generated Zombies C# source stays below 500 lines. The boundary is responsibility, not an
arbitrary numbered split: file names tell a mod author where configuration, combat, wave, session,
health, uplink, presentation, and test behavior live.

## Dependency lessons

Zombies also exercises failure paths that small samples rarely reach:

- Worlds publishes immutable session replacements when the active gameplay scene changes, so
  session consumers rebind instead of holding stale scene identity.
- RobotKit anchors canonical SDK identity to native robot roots. Queries, physics hits, and player
  targeting therefore agree even when a native robot has many child colliders.
- Chronos uses owner-scoped leases. Nested shop, conversation, game-over, and Superhot effects compose,
  restore the original clock exactly, and share player-control suspension safely.
- Every modal surface — shop, JACK IN, game over — holds gameplay through the SDK's `GameplayPause`
  rather than its own acquire-fallback-retry code. It prefers a Chronos world freeze, degrades to a
  player-control lease when Chronos is unavailable, reports a total failure once instead of every frame,
  and reacquires a hold the host takes away mid-session.
- Every asynchronous SDK call — reachable-spawn search, scene return, conversation turn, voice capture —
  runs through `PendingOperation<T>`. Nothing waits on a task, cancellation drains so a late result is
  still released on the main thread, and the return-to-menu path carries a deadline so a scene load that
  never settles cannot strand the run behind a frozen world.
- The frame loop does no spawning, pursuit, or attacks when world delta is zero. Unscaled control time
  still drives menus, conversation timeouts, scene-return polling, and HUD state.
- Native player health is captured before Zombies first mutates it and restored to the exact pre-run
  value on restart, exit, load failure, or unload.
- Framework audio caches synthesized cue clips and reuses fully reset playback hosts, so rapid zapper
  fire does not allocate a new clip and GameObject for every shot. Those cues are notification tones, not
  sampled audio — mod-authored sound ships as a prefab with an `AudioSource`. See
  [Core services](CoreServices.md#audio-interactions-and-items).
- UI surfaces, modals, input actions, spawned agents, audio playback, control handles, cancellation
  sources, and update subscriptions all have early-release paths as well as lifetime fallback cleanup.

These behaviors are implemented in the providers rather than faked inside the gamemode, making the
same guarantees available to other mods.

## Configuration and compatibility

`ZombiesConfig` schema 3 preserves earlier saves, migrates the former `overrideKey` binding to
`jackInKey`, removes retired no-op presentation settings, rejects non-finite tuning, trims identifiers,
normalizes key names, and clamps every gameplay value to a documented safe range. Missing JSON members
retain real defaults, including when `DataContractJsonSerializer` bypasses the constructor.

Schema 3 adds `seed`. It defaults to `0`, meaning seed the run's wave and archetype RNG from entropy, so
no two runs are identical; set any non-zero value to replay a fixed sequence for practice or a bug report.
Saves written before schema 3 deserialize to `0`, which is already the wanted meaning, so the migration
has nothing to reshape — worth copying as a pattern: prefer a new member whose zero value is the correct
default over one that needs migration code.

The manifest declares Worlds, RobotKit, and Chronos as required versioned dependencies. Network,
remote-AI, player-token, microphone, and speech-to-text capabilities are disclosed because players may
explicitly enable those optional paths; installation alone never activates them.

## Test the example

The focused controller suite uses `FakeModContext`, RobotKit fakes, deterministic time, captured UI,
and controllable scene tasks. It covers waves, reachable spawning, combat, shops, live and offline
uplink paths, hard freeze, entity recreation, ally retargeting, health restoration, game over, restart,
pending return cancellation, and numeric saturation.

```sh
dotnet run --project tests/TopiaForge.ModManager.Tests/TopiaForge.ModManager.Tests.csproj -c Release -- --zombies-controller
dotnet run --project tests/TopiaForge.ModManager.Tests/TopiaForge.ModManager.Tests.csproj -c Release -- --zombies-config
dotnet run --project tests/TopiaForge.ModManager.Tests/TopiaForge.ModManager.Tests.csproj -c Release
```

Before release, also run the live candidate flow in the
[first-party mod catalog](https://github.com/furroxide/TopiaForge/blob/main/docs/FirstPartyMods.md):
complete several waves, buy each upgrade, exercise every archetype and ally outcome, verify focus and
reduced motion/high contrast, change scenes, restart repeatedly, cancel a menu return, and confirm that
time, health, controls, UI, audio, and robots all return to baseline.
