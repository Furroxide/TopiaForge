---
title: UGC live sync
description: Preview bounded local or collaborative UGC snapshots in Robotopia through the safe V1 module.
---

# UGC live sync

The optional UGC module streams level snapshots into a running Robotopia preview without exposing
Robotopia or Unity implementation objects. It supports watched local exports and an external
Automerge document. The Robotopia runtime is preview/play only; it never writes edits back to the
authoring source.

## Add UGC

```sh
topiaforge mod add ugc
topiaforge restore
```

Resolve `IUgcLiveSyncService` with `Context.RequireExtension<IUgcLiveSyncService>()`. Sessions and
asset overrides return lifetime-owned leases. Listen for session, snapshot, patch, error, and stop
events, then unregister any provider events with `Context.Lifetime.Defer(...)` when they are not
already owner-bound.

## Local authoring loop

The release can scaffold the optional editor companion for Robotopia bundle/world content. Export
snapshots to a bounded watch folder, then start the UGC development command documented by
`topiaforge ugc --help`. The ordinary code-mod `topiaforge dev` loop remains separate and does not
require an editor.

## Safety and failure behavior

Snapshots, URLs, paths, and remote documents are untrusted input. The provider bounds files,
collections, strings, decompression, network responses, and update frequency. A rejected patch
leaves the last known-good preview intact and raises a structured `UgcSyncError`.

Do not drive shared Robotopia world/save state from a live authoring document. Use
`Context.LocalStorage` only for installation-local mod state, and publish immutable bundle content
for Robotopia players. Future shared or save-scoped state belongs in its own authoritative service.

## Asset overrides

Load a prefab through `Context.Assets`, keep the returned opaque handle, and register a typed
`UgcAssetOverride`. The override applies on the next import/rebuild and never exposes Robotopia or
Unity native objects to consumers. Both handles are released with the mod lifetime.

See [Specialist modules](Modules.md#ugc), [Custom worlds](CustomWorlds.md), and the generated C# API
reference.
