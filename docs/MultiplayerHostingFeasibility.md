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
   *Partial evidence on record — item still unproven.* A transport vendor decision now exists that treats
   game-identity neutrality as the primary selection criterion, and rules out every relay that would require
   TopiaForge to assert product ownership, a Unity project relationship, or a Steam AppID relationship it does not
   hold. That narrows the licensing surface; it does **not** establish permission from the game owner, which is what
   this item actually requires.
5. **Platform operations:** the supported host platforms, update compatibility, secrets, logs, crash recovery,
   resource limits, abuse handling, and version rollout/rollback have executable acceptance tests.
   *Partial evidence on record — item still unproven.* Abuse handling and relay spend control now have a written
   design: a per-ticket byte ceiling, a per-IP credential mint rate limit, strongly consistent counters, and a global
   kill switch whose effective latency is the credential lifetime. That design also records the open problem it
   cannot solve — TopiaForge has no identity model, so every control is scoped to a rotatable ticket or address.
   Bandwidth is likewise bounded on one side only: a generator check (`TFMP014`) now warns when a contract's declared
   client-to-server command and object rates exceed the recorded per-connection budget, while the server-to-client
   direction stays unbounded by the frozen contract. **None of this has executable acceptance tests, and this item
   requires executable acceptance tests.**

## Where partial evidence lives

Two maintainer-internal decision records supply the partial evidence noted against items 4 and 5. They are not part of
release packages, because they carry cost figures:

- `docs/internal/MultiplayerTransportOptions.md` — transport vendor analysis on game-identity neutrality, the
  bandwidth envelope and what the frozen contract can and cannot enforce, host-clustered relay demand and the host
  selection requirements that follow from it, and the abuse and spend-control design.
- `docs/internal/LauncherReachabilityProbe.md` — an opt-in, developer-mode-only launcher probe that would measure how
  many real players are directly reachable. It is disabled by default and blocked on the same privacy-notice approval
  as every other TopiaForge data collection.

The player-safety half of the same analysis is player-facing and lives in
[privacy and capability disclosure](PrivacyAndCapabilities.md): direct peer-to-peer connections expose participant IP
addresses, and the cost model prefers exactly the connections that do so.

Partial evidence does not advance the gate. **Every one of the five items above remains unproven, and the gate is
open.** These records exist so that the decisions behind a future spike are already made and reviewable, not to
suggest that a spike is imminent.

Each item needs reproducible commands, exact binary hashes, logs, and an owner. A normal client started with generic
headless flags is insufficient if it still depends on presentation state or lacks redistribution rights.

## Release decision

Until every item passes, TopiaForge may ship only the stable transport-neutral contract, real-game standalone
loopback, and synthetic multi-peer rig. It must not advertise live multiplayer, dedicated Robotopia hosting, hosted
server downloads, or automatic server-package execution. A listen-server transport and a dedicated-server transport
receive separate go/no-go decisions because the former does not resolve the latter's build and licensing constraints.
