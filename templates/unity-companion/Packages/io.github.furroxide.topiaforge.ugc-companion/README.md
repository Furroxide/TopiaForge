# TopiaForge UGC Companion (Unity Editor)

Author UGC level content in the Unity Editor and **live-sync it into the running game with no restart**.
This package is the authoring side of Robotopia's UGC live-sync pipeline; the game side is the
`TopiaForge.UgcLiveSync` mod. See [`docs/UgcLiveSync.md`](../../../../docs/UgcLiveSync.md) for the full
contract.

## How it works

1. Build your level as a normal Unity hierarchy under one root GameObject.
2. Tag objects with **TopiaForge UGC** markers (Add Component → *TopiaForge UGC*):
   - **Entity** (required on every exported object) — gives it a stable id.
   - **Model Renderer** / **Prefab Instance** — the asset id to render (e.g. `@robotopia/tree-model`).
   - **Spawn Location** — the player spawn point.
   - **Point Of Interest** / **Area Of Interest** / **Agent** — optional metadata.
3. Open **TopiaForge → UGC Live Sync**, set the **Export root** and the game's **watch folder**
   (the folder the `TopiaForge.UgcLiveSync` mod is configured to watch), then turn **Live Sync ON**.
4. In the running game, start a local watch session (UGC Live panel) on the same folder.
   Now every scene save / hierarchy edit re-exports and the game hot-reloads it.

## Notes

- The exporter writes the exact `UgcExportProject` JSON the game imports and applies the inverse
  coordinate handedness (position X negated; rotation conjugated by the `Scale(-1,1,1)` basis), so the
  scene appears in-game with the same orientation you authored.
- Keep **Entity ids stable** (don't delete/re-add markers needlessly) so edits patch incrementally
  instead of forcing a full rebuild. Reparenting an entity forces a full rebuild.
- Asset ids resolve in-game via the built-in catalog or a mod runtime override
  (`IUgcLiveSyncService.RegisterAssetOverride`). Unknown ids render as placeholder cubes.
- This is **content** live-reload, not C# hot reload (Mono cannot unload assemblies).
