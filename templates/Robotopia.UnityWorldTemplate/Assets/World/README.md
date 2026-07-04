# Building your world (Blender → Unity → Robotopia)

Assemble your world as **one prefab** at `Assets/World/World.prefab`. `Robotopia → Build World Bundle`
(or `robotopia world build`) validates it and ships it into the paired mod.

## The prefab contract

- A descendant named **`SpawnPoint`** (an empty GameObject) marks where the player stands. Place it
  ≥ 1 m above walkable ground.
- **No custom scripts.** Only native Unity/HDRP components survive the trip into the game (colliders,
  lights + HDAdditionalLightData, HDRP Volumes, reflection probes, audio sources, LOD groups...).
  The validator fails the build on any custom MonoBehaviour.
- **Colliders on all walkable geometry** — the player falls through anything without one. For Blender
  imports use a MeshCollider (or cheaper primitive colliders for simple shapes).
- Optional: a child (suggested name `Environment`) carrying a **global HDRP Volume** with your own
  sky/exposure/fog. When present, the game skips its default sky. Without it you get the framework's
  gradient sky + sun automatically — no lighting setup needed.
- Do **not** add cameras or event systems; the game's play scene owns those. A fall below 100 m under
  the spawn respawns the player automatically (tunable from the mod's registration code).

## Importing from Blender

1. In Blender, apply transforms (`Ctrl+A → All Transforms`) and export **FBX** (or glTF):
   - FBX: scale 1.0, `Apply Scalings: FBX Units Scale`, `Forward: -Z`, `Up: Y` (the defaults work for
     most setups; check a 1 m reference cube lands at 1 m in Unity).
   - Keep materials simple — Unity re-creates them; textures pack/export alongside.
2. Drop the export into `Assets/World/Models/`. In the Unity import settings enable
   `Generate Lightmap UVs` if you plan to bake later; leave `Read/Write` off.
3. **Convert materials to HDRP**: `Edit → Rendering → HDRP Wizard → Convert All Built-in Materials to HDRP`
   (or create fresh `HDRP/Lit` materials and assign your textures).
4. Add colliders: select the imported meshes → Add Component → MeshCollider (or box/capsule primitives
   for simple furniture — cheaper at runtime).
5. Drag everything under one root GameObject, add the `SpawnPoint` child, and save the root as
   `Assets/World/World.prefab` (overwrite the sample).
6. `Robotopia → Validate World Prefab`, fix anything it flags, then build.
