# TopiaForge Custom World — Unity project template

A starter Unity project for authoring **custom-geometry worlds** for Robotopia and building them into
an AssetBundle loaded through a manifest-declared launch target. Code changes require a game restart.

Create one from the launcher (**Developer → Projects → New ▾ → Unity world project**) or the CLI
(`topiaforge new unity-world <name>`). Both copy this template, register the project in the
launcher's Projects list, and (where Unity is detected) let you open it directly.

## What's inside

- `Assets/Scenes/Example.unity` — a preview scene. Exported geometry belongs in `Assets/World/World.prefab` or the prefab configured in `topiaforge.world.json`.
- `Packages/vpm-manifest.json` — the VPM dependencies (the world companion + the resolver). The companion is
  installed for you. On a fresh clone, `io.github.furroxide.topiaforge.vpm-resolver` performs a bounded, read-only check and
  offers the explicit launcher/CLI recovery command; editor startup never downloads or extracts packages.
- `ProjectSettings/ProjectVersion.txt` — the pinned authoring editor version. "Open in Unity" launches only
  the matching installed editor.

## Fresh-clone package recovery

From the cloned project directory, restore packages before authoring:

```sh
topiaforge unity resolve .
```

You can also use **Developer → Packages → Resolve All** in the launcher. The CLI/launcher re-resolves the
declared ranges, verifies integrity for remote archives, validates package identity, stages replacements, and
rolls back on failure. Review the resulting `Packages/vpm-manifest.json` diff before committing it. The embedded
Unity recovery bridge only reports drift and can copy this command; it never mutates the project. Invalid or
oversized VPM manifests fail closed and are preserved for diagnosis.

## Author a custom world (the workflow)

1. Open the project in the required Unity 6000.0.23f1 editor.
2. Assemble your level in `Assets/World/World.prefab`, with exactly one descendant named `SpawnPoint`. Use `Example.unity` to preview it; the bundle exports the configured prefab.
3. Pair the project with a mod and build its AssetBundle:
   `topiaforge world link --project . --mod ..\my.world` then `topiaforge world build`.
4. Install the mod, then run `topiaforge launch --target <launch-target-id>`. Copy the exact ID from
   the paired mod's `contributions.launchTargets`; the world template generates `<mod-id>.menu`.

Scene layout, robot personalities, and lore for the game's **built-in** assets are authored in the
official Robotopia Creator at <https://robotopia.gg/editor/> — see its
[documentation](https://robotopia.gg/docs/). This template is for the custom-geometry path the
browser editor does not cover.

## Custom worlds (fully bespoke geometry — Blender welcome)

To ship a **completely custom world**
(your own Blender/Unity geometry) as a playable Robotopia world:

1. Scaffold + pair (once):
   `topiaforge new mod my.world --template world` and
   `topiaforge new unity-world MyWorld --mod ..\my.world`
   (or pair this project with `topiaforge world link --project . --mod <modDir>` — writes
   `topiaforge.world.json`).
2. Author the world as **one prefab** at `Assets/World/World.prefab` — see `Assets/World/README.md`
   for the Blender→Unity crib sheet and the prefab contract (a `SpawnPoint` child, colliders, no
   custom scripts, optional HDRP Volume).
3. Build the bundle into the mod: **TopiaForge → Build World Bundle**, or `topiaforge world build`
   (headless; needs Unity 6000.0.23f1, the pinned authoring editor).
4. Play: `topiaforge world play --target <launch-target-id>` builds → packs → installs → launches
   the explicit target declared by the paired mod. For a generated world mod, use its `<mod-id>.menu`
   target from `contributions.launchTargets`. The in-game GAMEMODES picker lists launch targets.

By default, the CLI waits up to 30 seconds for runtime acknowledgement. Exit `0` confirms that the
target reached Running; exit `3` means startup is unconfirmed. `--no-wait` reports only process
creation. Missing bundle content or missing/ambiguous `SpawnPoint` markers prevent startup.

The current bundle target is `StandaloneWindows64`, so custom worlds run on the Windows player (native or
through Proton/Wine) but are not yet supported by the native macOS player.

The `io.github.furroxide.topiaforge.world-companion` package provides the build/validate menu items. Full walkthrough:
`docs/CustomWorlds.md` in the TopiaForge repo.
