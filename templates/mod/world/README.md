# {{DISPLAY_NAME}} — a custom Robotopia world

This Manifest V6 package declares a bundle world in `AssetBundles/{{BUNDLE_NAME}}.bundle` and a launch target that pairs it with Free Play. It requires Worlds; Free Play does not require Sandbox. Package loading does not instantiate content. The manager binds the declared bundle provider and owns content loading, readiness and cleanup within the session.

## Authoring loop

1. Set your author and license information in `topiaforge.mod.json`.
2. Pair a Unity authoring project: `topiaforge new unity-world {{TYPE_NAME}}World --mod .`, or use `topiaforge world link --project <unityProj> --mod .` for an existing project.
3. Assemble `Assets/World/World.prefab`. Place exactly one descendant named `SpawnPoint` at the intended player position and rotation. Give walkable geometry colliders and use native Unity/HDRP components without custom scripts.
4. Build and attest the bundle with `topiaforge world build` (or `TopiaForge → Build World Bundle` in Unity).
5. Restore and test with `topiaforge restore` and `dotnet test tests/{{ASSEMBLY_NAME}}.Tests`, then validate, package and install with `topiaforge check package .`, `topiaforge pack` and `topiaforge install`.
6. Launch this package's target with `topiaforge launch --target {{MOD_ID}}.menu`, or run `topiaforge world play --target {{MOD_ID}}.menu` from its paired Unity project to build, package, install and launch it. The default CLI wait confirms Running only after a matching runtime acknowledgement; exit `3` means startup is unconfirmed. Verify geometry, authored spawn and return-to-menu behavior in the installed game.

## Contract and tests

`contributions.worlds` owns the bundle/prefab paths, supported transition and authored-marker policy. `contributions.launchTargets` selects that world and `io.github.furroxide.topiaforge.worlds.freeplay`. The authoring template builds `StandaloneWindows64`; `contentTargets` declares that requirement explicitly. Rebuild the bundle and update its target metadata together when choosing a different native target. The world consents to other compatible gamemodes through `openToAnyCompatible`; the supplied target keeps its world fixed.

The generated NUnit test checks that loading the package allocates no gameplay resources and unloads cleanly. It does not load a native bundle or verify an authored marker. A missing bundle or missing/ambiguous `SpawnPoint` is a startup failure; a bare scaffold is not yet playable. Manifest V6 deliberately excludes `options`, `optionValues` and `sessionExtensions`.

See `docs/ManifestV6.md`, `docs/CustomWorlds.md` and `docs/YourFirstMod.md` in the TopiaForge repository.
