# Custom Worlds — ship your own Blender/Unity world as a playable Robotopia world

A mod can register a **fully custom world** — geometry modeled in Blender (or anywhere), assembled in
Unity, shipped as a prefab in an AssetBundle. Launching it loads the game's clean play stage (a real
player spawns natively), places your world at the player spawn, and tears it down when the session
ends. Custom worlds appear in the in-game **GAMEMODES** menu and work with the Sandbox gamemode's
spawn menu out of the box.

```
Blender (.fbx/.gltf)  →  Unity world project  →  world prefab  →  AssetBundle  →  .robotopiamod
                          (robotopia new           Assets/World/     AssetBundles/     installed via the
                           unity-world)            World.prefab      <name>.bundle     launcher / CLI
                                                                         │
                                        game: UgcPlay scene + native player spawn + your world
```

## Prerequisites

- `robotopia doctor` — .NET SDK 8+ (build/pack) and, for bundle builds, a **Unity 6000.0.x editor
  with patch ≤ 31** (the game player is 6000.0.31f1; bundles from newer editor streams may not load).
  Doctor warns when your installed editors don't qualify and prints the Hub install hint.

## Scaffold and pair (once)

```powershell
robotopia new mod my.world --template world          # the C# mod that ships + registers the world
robotopia new unity-world MyWorld --mod ..\my.world  # the Unity authoring project, paired
```

Pairing writes `robotopia.world.json` at the Unity project root (`worldId`, `bundleName`,
`worldPrefab`, `modPath`). Pair an existing project instead with
`robotopia world link --project <unityProj> --mod <modDir>`. The scaffolded mod is a single
registration call:

```csharp
context.RegisterWorldFromBundle(worlds, new BundleWorldOptions
{
    Id = "my.world.world",
    Name = "My World",
    BundleRelativePath = "AssetBundles/my-world.bundle",
    // Content = new CustomWorldOptions { SpawnPointName = "SpawnPoint", KillPlaneDepth = 100f, ... }
});
```

and `UnregisterWorld` + `UnloadOwner` on unload. Manifest: `vpmDependencies` on `robotopia.worlds
>= 0.5.0` and `robotopia.assets`, `supportedSdkVersionRange >= 0.1.1`.

## Author the world (Blender → Unity)

Full crib sheet: the template's `Assets/World/README.md`. The contract, in short — one prefab
(default `Assets/World/World.prefab`) where:

- a descendant named **`SpawnPoint`** marks where the player stands (≥ 1 m above walkable ground);
- **no custom MonoBehaviours** — the game cannot resolve modder scripts inside content bundles; only
  native Unity/HDRP components survive (colliders, lights, Volumes, reflection probes, audio, LODs);
- **colliders on all walkable geometry** (MeshCollider for Blender imports);
- optionally a **global HDRP Volume** child (suggested name `Environment`) with your own
  sky/exposure — its presence suppresses the framework's default gradient sky + sun;
- no cameras/event systems (the game's play scene owns those).

`Robotopia → Validate World Prefab` checks all of this in-editor; the build runs the same validation.

## Build the bundle

- In-editor: **Robotopia → Build World Bundle** (from `com.robotopia.world-companion`, preinstalled
  in the template).
- Headless: `robotopia world build [--project <path|name>] [--mod <dir>] [--bundle <name>]
  [--unity <Unity.exe>] [--dry-run]` — locates an eligible editor (explicit → `UNITY_EDITOR_PATH` →
  Unity Hub scan), runs `-batchmode -executeMethod
  Robotopia.WorldCompanion.Editor.WorldBundleBuilder.Build`, and verifies the bundle landed at
  `<mod>/AssetBundles/<name>.bundle` (with a provenance `.manifest.json`: sha256, editor version,
  asset list). Failures print the tail of `Logs/robotopia-world-build.log`.

## Play

```powershell
robotopia world play        # build → pack → install → launch, one command
```

or compose the steps yourself (`world build`, `pack`, `install`, `launch` / `dev-install`). In-game,
the world shows up under **GAMEMODES** (paired with the Sandbox gamemode by default — Q spawn menu,
props, robots all work inside your world).

## Runtime semantics (what the Worlds framework does)

- The bundle prefab is loaded lazily on the world's first launch (via robotopia.assets, cached).
- Content is created **before** the scene switch, so a broken bundle fails the launch with a clear
  message while you are still on the menu.
- The world is moved so its `SpawnPoint` coincides with the native player spawn (no player teleport).
- A fall more than `KillPlaneDepth` (default 100 m) below the spawn respawns the player at the spawn.
- Session end (pause-menu exit, another launch superseding, mod unload) destroys the world content;
  `UnregisterWorld` during a live session ends it cleanly.
- Placement failure falls back to the generated sandbox arena rather than stranding the player.

## Command reference

| Command | What it does |
|---|---|
| `robotopia new unity-world <name> [--dir Path]` | Scaffold the Unity authoring project (add `--mod <modDir>` to pair it in the same step). |
| `robotopia world link --project <unityProj> --mod <modDir> [--bundle name] [--prefab assetPath]` | Pair an existing Unity project with the mod that ships its bundle (writes `robotopia.world.json`). |
| `robotopia world build [--project <unityProj\|name>] [--mod <modDir>] [--bundle name] [--unity Unity.exe] [--dry-run]` | Headless bundle build into `<mod>/AssetBundles/`; `--dry-run` prints the resolved project/mod/bundle/editor without launching Unity. |
| `robotopia world play [--project <unityProj\|name>] [--mod <modDir>] [--configuration cfg]` | Build → pack → install → launch, one command. |

Ready to ship the world to other players? See [PublishingYourMod.md](PublishingYourMod.md).

## Troubleshooting

- **"No eligible Unity editor"** — install 6000.0.31f1 (Hub → Installs → Archive, or headless:
  `"Unity Hub.exe" -- --headless install --version 6000.0.31f1 --changeset a206c360e2a8`).
- **Validation: custom component** — a script from your project/package is on the prefab; replace it
  with native components or move behaviour into the mod's C# (attach at runtime).
- **World loads but looks washed out** — no global Volume and `ApplyDefaultEnvironment = false`; use
  the default environment or ship your own Volume.
- **Player falls through the floor** — missing colliders on the imported meshes.
- **`manager.log` says the bundle has N prefabs** — pin `PrefabAssetName` in `BundleWorldOptions` or
  keep exactly one prefab in the bundle.
