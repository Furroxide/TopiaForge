# Robotopia World Companion

Editor tooling for authoring **custom Robotopia worlds**:

- **Robotopia → Validate World Prefab** — checks the selected prefab (default
  `Assets/World/World.prefab`) against the runtime contract: a `SpawnPoint` descendant, no missing
  scripts, no custom MonoBehaviours (native Unity/HDRP components only), colliders on geometry, and
  sane bounds.
- **Robotopia → Build World Bundle** — validates, then builds the world prefab into an AssetBundle
  (StandaloneWindows64) and copies it to the paired mod's `AssetBundles/<bundleName>.bundle` with a
  provenance manifest. The builder pins a project-owned HDRP asset and makes every quality level
  inherit it, so clean editor imports and headless builds use the same render pipeline.

The pairing lives in `robotopia.world.json` at the Unity project root — write it with
`robotopia world link --project <thisProject> --mod <modDir>`. The CLI's `robotopia world build`
invokes the same builder headlessly (`-executeMethod Robotopia.WorldCompanion.Editor.WorldBundleBuilder.Build`).

Build with Unity **6000.0.23f1 (1c4764c07fb4)**. The game player uses the same editor version,
and bundles from other editor versions are not supported. The current `StandaloneWindows64` output runs on
the Windows player (native or through Proton/Wine), not the native macOS player.
