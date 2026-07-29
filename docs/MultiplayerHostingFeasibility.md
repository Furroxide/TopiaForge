---
title: Multiplayer hosting feasibility gate
description: Evidence required before TopiaForge advertises live transport or dedicated Robotopia hosting.
---

# Multiplayer hosting feasibility gate

The stable multiplayer contract, loopback provider, and deterministic test rig do not prove that Robotopia can be
run, rebuilt, licensed, or redistributed as a dedicated server. TopiaForge must keep live transport and dedicated
hosting experimental until this gate is closed with evidence for the exact supported Robotopia build.

## Current evidence

- TopiaForge can inject its BepInEx runtime into the supported interactive Robotopia client build. There is no
  supported dedicated Robotopia executable or separately validated server bootstrap in this repository.
- The SDK can model a headless logical server and prevents it from fabricating local-player or presentation access.
  Those test facades are contract evidence, not proof that Robotopia's native/game assemblies initialize headlessly.
- Unity 6 documents a Dedicated Server desktop sub-target that strips unnecessary rendering and asset work. That
  capability belongs to a Unity project build pipeline; it does not show that a compiled third-party game can be
  converted into, or legally shipped as, a server build. See the
  [Unity Dedicated Server introduction](https://docs.unity3d.com/6000.0/Documentation/Manual/dedicated-server-introduction.html).
- TopiaForge has no recorded permission to redistribute Robotopia executables, managed assemblies, data files, or
  derived server builds. Release artifacts therefore must not contain them.

## Required spike evidence

Before implementing a supported live provider, record a dated spike for the pinned game build that proves:

1. **Bootstrap:** a separately launched server process can load the TopiaForge runtime predictably, report failures,
   and shut down cleanly without an interactive player, graphics device, audio device, or UI.
2. **Simulation:** required Robotopia world, scene, physics, and gameplay systems advance correctly under a bounded
   server tick; presentation-only dependencies either stay unloaded or fail through typed unavailable facades.
3. **Transport injection:** authenticated participant identity, bounded/rate-limited commands, admission-before-world,
   reconnect snapshots, and orderly cancellation can be integrated without exposing native transport objects to mods.
4. **Distribution and licensing:** the game owner or applicable terms explicitly permit the chosen deployment model.
   If redistribution is not permitted, document an installation model that uses only player-owned game bytes and does
   not upload or copy them into TopiaForge infrastructure.
5. **Platform operations:** the supported host platforms, update compatibility, secrets, logs, crash recovery,
   resource limits, abuse handling, and version rollout/rollback have executable acceptance tests.

Each item needs reproducible commands, exact binary hashes, logs, and an owner. A normal client started with generic
headless flags is insufficient if it still depends on presentation state or lacks redistribution rights.

## Release decision

Until every item passes, TopiaForge may ship only the stable transport-neutral contract, real-game standalone
loopback, and synthetic multi-peer rig. It must not advertise live multiplayer, dedicated Robotopia hosting, hosted
server downloads, or automatic server-package execution. A listen-server transport and a dedicated-server transport
receive separate go/no-go decisions because the former does not resolve the latter's build and licensing constraints.
