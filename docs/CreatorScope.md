---
title: TopiaForge and the Robotopia Creator
description: What TopiaForge is for now that Robotopia ships its own world editor.
---

# TopiaForge and the Robotopia Creator

Robotopia build 2409 shipped **Robotopia Creator**: an official browser world editor with prop and
building placement, robot personality authoring, a ready-made robot library, a city street kit,
grid-snapped placement, templates, playtesting, and world sharing by link. It is free, first-party,
integrated with publishing, and runs on a tablet.

That changes what TopiaForge should be, so this page states the boundary explicitly. Everything else
in these docs should be readable against it.

## The boundary

**The Creator owns what is in a world.** Entity placement, prop selection, personalities, lore,
scene layout, publishing, and sharing are first-party, server-backed, and account-bound. TopiaForge
does not compete with any of that and should not try.

**TopiaForge owns what a world does, and where the browser cannot reach.** Concretely:

| TopiaForge | Robotopia Creator |
|---|---|
| C# behaviour: patched game runtime, gamemodes, shop/wallet, pause semantics | Scene and entity authoring |
| Runtime robot control: spawning, native pathfinding, objectives, dialogue, brain queries | Robot personalities, lore, voices |
| Time control, performance patches, in-game UI for mods | The world's look and contents |
| Custom-geometry worlds authored in Unity and shipped as AssetBundles | Worlds built from the shipped asset libraries |
| Local files, git, CI, offline validation | Cloud persistence, live collaboration, publish |

The Creator's own [documentation](https://robotopia.gg/docs/) lists what it deliberately does not do:
no scripting language, no visual logic graph, no multiplayer, no custom asset upload, no undo/redo
history. Those gaps are where TopiaForge belongs — not because they are oversights, but because they
are a different job.

## What this cost

Two subsystems were retired in the 2409 cutover rather than retargeted:

- **UGC live sync** reimplemented the game's own `UgcLiveSyncController` apply logic and synthesised
  the web editor's handshake, back when that editor was not a shipped product. It now is. Deleting it
  removed 37 declared game bindings — 26 of them critical — and a third-party sync server from the
  critical path.
- **Creator Tools** hosted the in-game workbench in ordinary play, but its global mutation path never
  worked: the only shipped mutation-safety service fails closed unconditionally. The workbench itself
  survives inside [Sandbox](Sandbox.md), where a controller-driven in-game creator is still something
  a browser editor does not give you.

## The `.roboworld` gap

The Creator exports `.roboworld` files and imports `.roboworld`, `.json`, and `.json.gz`. The shipped
game reads the same formats — build 2409's import host describes "the folder scanned for .roboworld,
.json, and .json.gz exports" — and exposes a local import path that needs no account.

TopiaForge does not yet read or write that format. The world-format contract is already pinned by
`tests/fixtures/ugc/sample-project.json` and its schema test, whose component keys (`transform`,
`agent`, `poi`, `spawn-location`, `aoi`, `prefab-instance`, `model-renderer`) match the 2409 runtime.
That fixture is the starting point, not leftover live-sync code.

Deliberately **not** planned: authoring the five components the Creator docs call hidden (Teleport,
Toggle Trigger, Grabbable, Event Trigger, Kill Trigger). The shipped 2409 runtime has eight
`Ugc*Component` types and none of those, and warns-and-ignores unknown components — so authoring them
would produce worlds that load with the feature silently missing.

Also out of scope, for reasons that will not change: publishing from the CLI (no public API, and there
is no unpublish), redistributing shipped assets (the FAQ is explicit that they stay Robotopia's), and
storing collaborate links (they are bearer credentials that carry delete rights).
