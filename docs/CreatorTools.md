---
title: Creator Tools
description: Use Robotopia's shared F5 workbench, safe creator catalog, reversible sessions, and local visual event projects.
---

# Creator Tools

Creator Tools is an optional developer package for ordinary standalone Robotopia play. It shares the same
fullscreen workbench as the managed Sandbox gamemode and is installed **disabled**. Enable it only in a dedicated
launcher profile; dependency selection adds Creator Content, RobotKit, and Worlds without changing other profiles.

Press **F5** in eligible gameplay to open or hide the workbench. Sandbox has routing priority. The global host does
not open in menus, during scene transitions, multiplayer, or any Worlds gamemode. Hiding the window releases player
controls but deliberately retains the creator session, graph run, owned spawns, temporary edits, and persistence
isolation. A warning HUD stays visible until **End Session & Restore** is confirmed.

## Workbench

The left pane combines RobotKit robot types, managed robots, and Creator Content entries. The center pane switches
between scene work and a bounded node graph. The inspector on the right edits the selected object or node, and the
bottom bar shows mutation, run, and save status. It supports search, keyboard graph connections, pan/zoom, UI scale,
high contrast, and reduced motion through TopiaForgeUi.

Creator sessions can spawn, select, transform, duplicate from a safe recipe, temporarily hide, despawn, undo, and
clean up tool-owned content. Native scene targets are borrowed through exclusive leases: their reversible properties
are snapshotted once and restored in reverse order. Native objects are never destroyed, and a native target can be
duplicated only when an approved adapter maps it to a namespaced catalog entry. Quest and progression actors are
filtered from native selection.

RobotKit contributes robot types, objectives, emotes, conversations, and temporary autonomous personality editing.
Personality and location edits use snapshot-backed leases; no edit can be committed to the save game.

## Mutation safety

Browsing the catalog and editing local projects do not mutate the world. Global spawning or editing requires both a
one-time acknowledgement for the current session and a build-validated persistence-isolation lease. If that bridge
is unavailable or stops reporting isolation, every global mutation fails closed while browsing and project authoring
remain available. Sandbox sessions do not require this global gate because their managed world is disposable.

Scene replacement, host unload, provider failure, or explicit **End Session & Restore** stops the graph, restores
borrowed properties, removes tool-owned objects in LIFO order, and releases player controls and isolation. Graph
**Stop** rolls back graph-owned changes without removing unrelated manual session spawns.

## Event projects

Projects are local-only. Creator Content stores `event-projects/index.v1.json` and one versioned document per project,
with at most 256 projects and 2 MiB per document. Documents contain metadata, Sandbox or exact-scene scope, a world-
or player-relative origin, personas, stable entity aliases, namespaced content references and expected source
versions, logical native-binding recipes, and a visual graph. Runtime entity ids and native handles are never saved.

One graph may run per creator session. Limits are 256 entities, 512 nodes, 1,024 edges, 64 personas, 64 native
bindings, 64 node executions per frame, and 100 emissions per Repeat node. Graphs support project/manual/interaction/
radius/entity/objective/conversation triggers; scaled-world-time delays, state conditions, and bounded repeats; and
spawn, remove, transform, robot configuration, personality, objective, emote, conversation, toast, and audio actions.
Ports are typed (`success`, `failure`, `true`, `false`, `each`, and `done`) and fan out deterministically. Arbitrary
cycles, scripts, expressions, and custom callback nodes are rejected.

Missing sources remain visible as unresolved content so a project can be inspected and repaired, but Run stays
blocked. Native binding recipes require explicit resolution confirmation every session.

## Content registration

Add the module and restore the synchronized compile-time/runtime dependency pair:

```sh
topiaforge mod add creatorcontent
topiaforge restore
```

Mods can register safe content through `ICreatorContentService`. The provider authenticates the calling mod and
qualifies every local id with that source; factories execute through the registering mod's own asset/entity services.
Resolve a required provider with `Context.RequireExtension<ICreatorContentService>()`; use
`Context.TryGetExtension<T>(out provider)` only when the manifest relationship is intentionally optional.
Mods with an explicitly verified reversible native surface can additionally register it through
`ICreatorSceneAdapterRegistry`. Creator Content authenticates the adapter namespace, validates and wraps every target,
bounds queries, rejects spoofed duplicate recipes, and owns exclusive edit cleanup across both the session and source
lifetime. Cross-package asset loading, arbitrary `Resources` scans, and custom graph code are not supported. Vehicle
entries must be validated self-contained prefabs or explicit native adapters; an unavailable native vehicle source is
shown as an honest empty/degraded catalog category.

See [Sandbox](Sandbox.md), [RobotKit](RobotKit.md), and [UI kit](UiKit.md).
