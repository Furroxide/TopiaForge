# {{DISPLAY_NAME}} — a custom Robotopia world

This mod ships a Unity world prefab in `AssetBundles/{{BUNDLE_NAME}}.bundle` and registers it as a
playable world (it appears under GAMEMODES, paired with the Sandbox gamemode by default).

## Authoring loop

1. Pair a Unity authoring project (once):
   `topiaforge new unity-world {{TYPE_NAME}}World --mod .`
   (or pair an existing project: `topiaforge world link --project <unityProj> --mod .`)
2. Author the world in Unity — model in Blender, import, assemble the prefab at
   `Assets/World/World.prefab`. Keep a descendant named `SpawnPoint` where the player should stand,
   give walkable geometry colliders, and use no custom scripts (native Unity/HDRP components only).
3. Build the bundle into this mod: `topiaforge world build`
   (or in Unity: `TopiaForge → Build World Bundle`).
4. Play: `topiaforge world play` — builds, packs, installs, and launches the game.

## Quick start (without Unity)

`topiaforge check package .` → `topiaforge pack` → `topiaforge install` → `topiaforge launch`.
The world only becomes playable once a bundle exists in `AssetBundles/` (see the authoring loop above).

See `docs/CustomWorlds.md` in the TopiaForge repository for the full walkthrough, and
`docs/YourFirstMod.md` if you are new to modding.
