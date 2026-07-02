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

## One-time interactive setup (bake the TMP font assets)

The TMP font assets are baked interactively once and committed; the batch build only
labels + builds (scripted atlas generation is the flaky part of TMP, so we avoid it).

1. Open this project in the pinned editor (first open imports TMP from `com.unity.ugui`).
2. Window → TextMeshPro → Font Asset Creator, then bake and save (Create → Save as…):
   - `Assets/Fonts/Quicksand-VariableFont_wght.ttf`
     → `Assets/FontAssets/QuantumWorks-Quicksand SDF.asset`
     (1024×1024, SDFAA, padding 9, static; charset: ASCII + Latin-1 Supplement + Latin
     Extended-A + `–—‘’“”…•·%°×✓`)
   - Same TTF with Bold instance (or faux-bold source)
     → `Assets/FontAssets/QuantumWorks-Quicksand-Bold SDF.asset` (same settings)
   - `Assets/Fonts/Arista-Pro-Bold.ttf`
     → `Assets/FontAssets/QuantumWorks-Arista SDF.asset` (512×512, SDFAA, padding 9,
     static, ASCII — display headings only)
3. On `QuantumWorks-Quicksand SDF`, set the Bold typeface in the font-weight table
   (weight 700 → `QuantumWorks-Quicksand-Bold SDF`).
4. Leave every font-asset material on the `TextMeshPro/Distance Field` shader — building
   the bundle pulls the shader in as a dependency, which is the in-game safety net.
5. Commit the new `.asset` files (+ `.meta`), then run the build (below).

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
