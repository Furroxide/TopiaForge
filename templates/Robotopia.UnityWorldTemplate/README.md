# Robotopia UGC World — Unity project template

A starter Unity project for authoring **UGC level content** for Robotopia and live-syncing it into the running
game with no restart. This is the QuantumWorks equivalent of VRChat's `template-world`.

Create one from the launcher (**Developer → Projects → New ▾ → Unity world project**) or the CLI
(`robotopia new unity-world <name>`). Both copy this template, install the
`com.robotopia.ugc-companion` package, register the project in the launcher's Projects list, and (where Unity is
detected) let you open it directly.

## What's inside

- `Assets/Scenes/Example.unity` — an empty starter scene. Build your level here.
- `Packages/vpm-manifest.json` — the VPM dependencies (the UGC companion + the resolver). The companion is
  installed for you; a freshly git-cloned copy self-restores on open via `com.robotopia.vpm-resolver`.
- `ProjectSettings/ProjectVersion.txt` — the recommended Unity 6 version. "Open in Unity" picks the matching
  installed editor (or the newest installed one, with a warning).

## Author + go live (the workflow)

1. Open the project in Unity (Unity 6 recommended).
2. In `Example.unity`, create an empty GameObject named **UGC Root**. Build your level as children of it.
3. Tag GameObjects with UGC markers (Add Component → *UgcEntityMarker*, *UgcSpawnLocationMarker*,
   *UgcModelRenderer*, *UgcPoiMarker*, *UgcAgentMarker*, …) from the `com.robotopia.ugc-companion` package.
4. Open **Robotopia → UGC Live Sync**, set the export root to **UGC Root** and a watch folder, then enable
   **Live Sync**. The scene exports to the watch folder on every save/change.
5. In the QuantumWorks launcher's **UGC Live Sync** cockpit, point the watch folder at the same folder and hit
   **Go Live** — the running game hot-reloads your content. No manual scripts.

See `docs/UgcLiveSync.md` in the QuantumWorks repo for the full contract (handedness, the export JSON shape, and
the Automerge channel for web-editor parity).

## Custom worlds (fully bespoke geometry — Blender welcome)

The UGC loop above places the game's **built-in** assets. To ship a **completely custom world**
(your own Blender/Unity geometry) as a playable Robotopia world:

1. Scaffold + pair (once):
   `robotopia new mod my.world --template world` and
   `robotopia new unity-world MyWorld --mod ..\my.world`
   (or pair this project with `robotopia world link --project . --mod <modDir>` — writes
   `robotopia.world.json`).
2. Author the world as **one prefab** at `Assets/World/World.prefab` — see `Assets/World/README.md`
   for the Blender→Unity crib sheet and the prefab contract (a `SpawnPoint` child, colliders, no
   custom scripts, optional HDRP Volume).
3. Build the bundle into the mod: **Robotopia → Build World Bundle**, or `robotopia world build`
   (headless; needs a Unity 6000.0.x editor, patch ≤ 31 — the game player is 6000.0.31f1).
4. Play: `robotopia world play` builds → packs → installs → launches; the world appears in the
   in-game GAMEMODES menu.

The `com.robotopia.world-companion` package provides the build/validate menu items. Full walkthrough:
`docs/CustomWorlds.md` in the QuantumWorks repo.
