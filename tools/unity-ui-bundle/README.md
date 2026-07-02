# QuantumWorks UI brand bundle builder

This tiny Unity project builds `quantumworks-ui.bundle` — the brand AssetBundle
(TextMeshPro font assets + brand sprites) that `Robotopia.Mods.UnityUi` embeds into its
DLL and loads at runtime. It also doubles as an editor harness for visual QA of the kit.

## Editor version rule (hard requirement)

The Robotopia player is **Unity 6000.0.31f1**. AssetBundles serialized by a newer editor
stream (6000.5+, 7000.x) are not safe to load in that player. Use an editor in the range
**6000.0.23f1 – 6000.0.31f1** (this project is pinned to 6000.0.31f1). Headless install:

```powershell
& "$env:ProgramFiles\Unity Hub\Unity Hub.exe" -- --headless install --version 6000.0.31f1 --changeset a206c360e2a8
```

`tools/build-ui-bundle.ps1` auto-detects eligible editors and hard-fails on anything else.

## Font baking

The build script bakes the static SDF TMP font assets itself (idempotent — an existing
committed `Assets/FontAssets/*.asset` is reused as-is; delete it to re-bake):

- `QuantumWorks-Quicksand SDF` — 1024×1024, SDFAA, padding 9, static; ASCII + Latin-1
  Supplement + Latin Extended-A + typographic punctuation.
- `QuantumWorks-Arista SDF` — 512×512, same settings (display headings).
- Bold renders via TMP faux-bold (the variable TTF imports only its default instance),
  which the kit selects automatically when no dedicated bold asset ships.

Every font-asset material stays on the `TextMeshPro/Distance Field` shader — building
the bundle pulls the shader in as a dependency, which is the in-game safety net.
Commit the baked `.asset` files (+ `.meta`) together with the rebuilt bundle.

## Building the bundle

```powershell
.\tools\build-ui-bundle.ps1            # auto-detects an eligible editor
.\tools\build-ui-bundle.ps1 -UnityExe "C:\Program Files\Unity\Hub\Editor\6000.0.31f1\Editor\Unity.exe"
```

Or in-editor: **QuantumWorks → Build UI Bundle**.

The build stamps provenance into `Assets/UiBundleManifest.json`, verifies the required
assets are labeled `quantumworks-ui`, builds LZ4 for StandaloneWindows64, and copies the
result to `src/Robotopia.Mods.UnityUi/Assets/quantumworks-ui.bundle` (+ a SHA256
provenance json). Rebuild the .NET solution afterwards so the embedded resource updates.

`*.bundle` is Git LFS-tracked (see `.gitattributes`).
