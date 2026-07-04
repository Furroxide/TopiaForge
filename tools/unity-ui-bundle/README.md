# QuantumWorks UI brand bundle builder

This tiny Unity project builds `quantumworks-ui.bundle` — the brand AssetBundle
(TextMeshPro font assets + brand sprites) that `Robotopia.Mods.UnityUi` embeds into its
DLL and loads at runtime. It also doubles as an editor harness for visual QA of the kit.

## Editor version rule (hard requirement)

The Robotopia player is **Unity 6000.0.31f1**. AssetBundles serialized by a newer editor
stream (6000.5+, 7000.x) are not safe to load in that player. Use an editor in the range
**6000.0.23f1 – 6000.0.31f1** (this project is pinned to 6000.0.31f1). Headless install
via the Unity Hub CLI:

```sh
# Windows
& "$env:ProgramFiles\Unity Hub\Unity Hub.exe" -- --headless install --version 6000.0.31f1 --changeset a206c360e2a8
# macOS / Linux
unityhub -- --headless install --version 6000.0.31f1 --changeset a206c360e2a8
```

`robotopia unity build-ui-bundle` auto-detects eligible editors and hard-fails on
anything else.

## Prerequisites on macOS/Linux

- The bundle targets `StandaloneWindows64` (the shipped player is Windows), so the
  editor needs the **Windows Build Support (Mono)** module installed via Unity Hub.
- The editor must have an activated Unity license (batchmode fails without one).
- Headless Linux boxes need a display for the build (no `-nographics` — TMP font baking
  needs `Shader.Find`); use `xvfb-run` if there is no desktop session.
- `git lfs` must be installed to check out and commit the bundle.

## Font baking

The build script bakes the static SDF TMP font assets itself (idempotent — an existing
committed `Assets/FontAssets/*.asset` is reused as-is; delete it to re-bake):

- `QuantumWorks-Quicksand SDF` — 1024×1024, SDFAA, padding 9, static; ASCII + Latin-1
  Supplement + Latin Extended-A + typographic punctuation.
- `QuantumWorks-Audiowide SDF` — 512×512, same settings (display headings).
- Bold renders via TMP faux-bold (the variable TTF imports only its default instance),
  which the kit selects automatically when no dedicated bold asset ships.

Every font-asset material stays on the `TextMeshPro/Distance Field` shader — building
the bundle pulls the shader in as a dependency, which is the in-game safety net.
Commit the baked `.asset` files (+ `.meta`) together with the rebuilt bundle.

## Building the bundle

From `apps/robotopia_cli` (or anywhere inside the repo):

```sh
dart run bin/robotopia.dart unity build-ui-bundle             # auto-detects an eligible editor
dart run bin/robotopia.dart unity build-ui-bundle --unity "C:\Program Files\Unity\Hub\Editor\6000.0.31f1\Editor\Unity.exe"
dart run bin/robotopia.dart unity build-ui-bundle --rebuild   # also rebuilds Robotopia.Mods.UnityUi
```

Or in-editor: **QuantumWorks → Build UI Bundle**. (`tools/build-ui-bundle.ps1` remains
as a deprecated wrapper that forwards to the CLI.)

The build stamps provenance into `Assets/UiBundleManifest.json`, verifies the required
assets are labeled `quantumworks-ui`, builds LZ4 for StandaloneWindows64, and copies the
result to `src/Robotopia.Mods.UnityUi/Assets/quantumworks-ui.bundle` (+ a SHA256
provenance json). Rebuild the .NET solution afterwards so the embedded resource updates.

`*.bundle` is Git LFS-tracked (see `.gitattributes`).
