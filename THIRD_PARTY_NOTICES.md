# Third Party Notices

## Trademarks and affiliation

This file ships inside the release archive, so it states the notice for readers who never see the
repository. The canonical wording is [TRADEMARKS.md](TRADEMARKS.md).

TopiaForge is an independent, community-built modding toolkit. It is not developed, published, or
endorsed by Tomato Cake or the Robotopia development team, and is not otherwise affiliated with
them. "Robotopia" and "Tomato Cake" are the property of their respective owners and are used here
only to identify the game this toolkit works with.

## Bundled runtimes and assets

This repository bundles BepInEx 5.4.23.5 binary runtime files under
`third_party/BepInEx/win_x64_5.4.23.5` and `third_party/BepInEx/macos_universal_5.4.23.5` for local Robotopia
loader installation.

- Project: BepInEx
- Version/tag: 5.4.23.5 (`v5.4.23.5`)
- Upstream: https://github.com/BepInEx/BepInEx/tree/v5.4.23.5
- License: MIT
- Local changes: none known; files are treated as bundled runtime assets.
- License text: `third_party/BepInEx/LICENSES/BepInEx-MIT.txt`

The official BepInEx 5.4.23.5 archives include these runtime dependencies. Their exact upstream revisions are
recorded by the BepInEx 5.4.23.5 README, and their license texts are redistributed beside the bundles:

| Component | Version / revision | License | Bundled files | License text |
| --- | --- | --- | --- | --- |
| UnityDoorstop | 4.5.0 / `33dab9a6733862eb81869ff08431d9478b28784b` | LGPL-2.1 | `winhttp.dll`, `libdoorstop.dylib` | `third_party/BepInEx/LICENSES/UnityDoorstop-LGPL-2.1.txt` |
| HarmonyX | 2.7.0 / `253725768e59b0e1ea90105cdbcc4a0a477422c7` | MIT | `0Harmony20.dll`, BepInEx Harmony integration | `third_party/BepInEx/LICENSES/HarmonyX-MIT.txt` |
| Harmony | HarmonyX upstream base | MIT | `0Harmony.dll` | `third_party/BepInEx/LICENSES/Harmony-MIT.txt` |
| MonoMod | 21.12.13.01 / `ede81f48924d58abf05359409fad740fe2b0dfb5` | MIT | `MonoMod.RuntimeDetour.dll`, `MonoMod.Utils.dll` | `third_party/BepInEx/LICENSES/MonoMod-MIT.txt` |
| Mono.Cecil | 0.10.4 / `98ec890d44643ad88d573e97be0e120435eda732` | MIT | `Mono.Cecil*.dll` | `third_party/BepInEx/LICENSES/Mono.Cecil-MIT.txt` |

RoboPatch was used only as behavior prior art for clean-room compatibility planning. No RoboPatch code was copied or ported.

Prism Launcher was used only as product maturity and UX inspiration. No Prism Launcher code was copied or ported.

TopiaForge launcher UI bundles Robotopia web brand assets from `https://robotopia.gg/` and local
TopiaForge artwork for offline launcher theming.

- Web-derived raster files: `packages/launcher_ui/assets/brand/topiaforge-city-header.webp`,
  `packages/launcher_ui/assets/brand/baby-stitch.webp`, and
  `packages/launcher_ui/assets/brand/sheriff.webp`
- Source: `https://robotopia.gg/`
- Owner: **Tomato Cake.** These three files are Robotopia web brand assets and remain the property of
  Tomato Cake. TopiaForge does not claim any ownership of them, they are not covered by this project's
  AGPL-3.0-or-later grant, and they carry no separate redistribution licence. They are bundled for
  offline launcher theming with attribution only.
- Rights basis: attribution pending a written grant. Permission has been requested from Tomato Cake and
  no written grant has been recorded yet. The project owner has accepted this as a non-blocking risk
  for the `0.x` line (recorded 2026-08-24); it must be revisited before `1.0`. If permission is
  declined, or if Tomato Cake asks for their removal, these files are removed or replaced on request.
- Replacement status: no first-party artwork exists for these three yet. All three are referenced by the
  launcher (`TopiaForgeBrandAssets.cityHeader`, `.babyStitch`, `.sheriff` in
  `packages/launcher_ui/lib/src/launcher_theme.dart`). The precedent for replacing them is
  `packages/launcher_ui/lib/src/pixel_robot.dart`, which retired the bundled `robot.webp`.
- Local changes: filenames were normalized for launcher packaging.

## First-party binary and generated assets

These ship in the release archive, are not third-party redistribution, and are recorded here so the
inventory covers every non-source file a user receives rather than only the ones with licence texts.

- `src/TopiaForge.Mods.UnityUi/Assets/topiaforge-ui.bundle` and
  `mods/TopiaForge.Worlds/AssetBundles/topiaforge-representative-world.bundle` are compiled from this
  repository's own Unity sources with editor `6000.0.23f1`. Each carries a sibling
  `*.manifest.json` recording the editor version, the contained asset paths, and a SHA-256, and release
  validation rebuilds them twice and compares bytes. A compiled AssetBundle is opaque to inspection, so
  that reproducible rebuild is what establishes the contents, not a reading of the file. The UI bundle
  bakes in glyph data derived from the OFL fonts noted below; those notices apply to it.
- `templates/TopiaForge.UnityWorldTemplate/Assets/HDRPDefaultResources/*.asset` are Unity YAML
  configuration assets authored by this project. They hold settings values and reference HDRP script
  GUIDs; the HDRP package itself is resolved by the consumer's Unity and is not redistributed here.

The TopiaForge pixel-art wordmark, icon, generated platform icon variants, and the drawn pixel-art robot
in `packages/launcher_ui/lib/src/pixel_robot.dart` are first-party project assets, not Robotopia-derived
third-party artwork. The robot replaced a previously bundled `robot.webp` taken from the Robotopia web
bundle; it is defined as a checked-in pixel grid rather than a raster file so its provenance is
unambiguous.

Those marks are the following files. They are listed by path because a prose description cannot be
checked, and this section claims to cover every non-source file a user receives:

- `packages/launcher_ui/assets/brand/topiaforge-icon.png` and
  `packages/launcher_ui/assets/brand/topiaforge-wordmark.png` — the source marks. Clean pixel art with
  six opaque colours and no anti-aliasing, which is what lets `assets/readme/source/pixel_marks.py`
  convert them to exact SVG paths for the repository homepage.
- `apps/topiaforge_launcher_flutter/assets/brand/topiaforge-app-icon.png` and
  `apps/topiaforge_launcher_flutter/snap/gui/topiaforge.png` — the launcher application icon.
- `apps/topiaforge_launcher_flutter/macos/Runner/Assets.xcassets/AppIcon.appiconset` — the same icon at
  the eight sizes macOS requires. Generated from the mark above, not drawn separately.
- `website/public/favicon.png` and `website/src/assets/topiaforge-wordmark.png` — the documentation
  site's copies of the same marks.

TopiaForge bundles the Quicksand font for body typography in the launcher UI and Unity brand bundle.

- Project: Quicksand
- Source file: `https://github.com/google/fonts/raw/main/ofl/quicksand/Quicksand%5Bwght%5D.ttf`
- Upstream: https://fonts.google.com/specimen/Quicksand
- License: SIL Open Font License 1.1
- Bundled at: `packages/launcher_ui/fonts` and `tools/unity-ui-bundle/Assets/Fonts`
- Local changes: none; filename was normalized for launcher and Unity packaging.
- SHA-256: `39c9b64223561f56aaff6062a6f04063c4fc86809ad6768722c06614d977e1cc`
- License text: `packages/launcher_ui/fonts/Quicksand-OFL.txt` (also copied into the Unity UI bundle source)

  This file was previously copied from the Robotopia web bundle
  (`https://robotopia.gg/assets/Quicksand-VariableFont_wght-DE2wFU7n.ttf`, SHA-256
  `8b3a3842cc4b666fde454446e28d1bacde30a0ac861e90cbb0bd77b02ecb9dae`). That copy was not
  byte-identical to upstream, so both bundled copies were replaced with the Google Fonts release
  above to give the font a verifiable first-hand provenance.

TopiaForge bundles the Audiowide font for display typography in the launcher UI and Unity brand bundle.

- Project: Audiowide
- Source files: `https://github.com/google/fonts/raw/main/ofl/audiowide/Audiowide-Regular.ttf`, `https://github.com/google/fonts/raw/main/ofl/audiowide/OFL.txt`
- Upstream: https://fonts.google.com/specimen/Audiowide
- License: SIL Open Font License 1.1
- Bundled at: `packages/launcher_ui/fonts` and `tools/unity-ui-bundle/Assets/Fonts`
- Local changes: none known; filename was normalized for launcher and Unity packaging.
- SHA-256: `c7c0f2b0f6fad8c623e31772ce79f94a4edb9321ffce9fce978ea892d20ae730`

TopiaForge redistributes Unity's TextMesh Pro essential resources inside the Unity UI bundle source at
`tools/unity-ui-bundle/Assets/TextMesh Pro`, and the release archives carry that directory. The set
comprises shaders, materials, style sheets, line-breaking data, and the Liberation Sans font.

- Project: TextMesh Pro essential resources (Unity Technologies)
- Bundled at: `tools/unity-ui-bundle/Assets/TextMesh Pro`
- License: distributed under the Unity Companion License as part of the Unity Editor package.
- Local changes: the EmojiOne sprite sheet and its sprite asset were removed because their bundled
  attribution granted no redistribution right; the TMP default sprite asset is cleared and emoji
  support disabled accordingly.

- Project: Liberation Sans
- Bundled at: `tools/unity-ui-bundle/Assets/TextMesh Pro/Fonts/LiberationSans.ttf`
- License: SIL Open Font License 1.1
- License text: `tools/unity-ui-bundle/Assets/TextMesh Pro/Fonts/LiberationSans - OFL.txt`
- Local changes: none; redistributed as shipped with TextMesh Pro.
- SHA-256: `e5b0af421ea2bfbc1ac8d251d647268087ae82786234c57f757d1f0b90fa8b49`

The standalone `topiaforge` executable embeds the Dart SDK runtime and the following runtime packages. Release
packaging copies the exact license texts resolved by `apps/topiaforge_cli/.dart_tool/package_config.json` into
`third_party/dart/LICENSES`, together with `VERSIONS.json`; package validation fails if any text is absent.

| Component | Pinned release resolution | License |
| --- | --- | --- |
| Dart SDK | 3.12.2 | BSD-3-Clause |
| archive | 4.0.9 | MIT |
| async | 2.13.1 | BSD-3-Clause |
| boolean_selector | 2.1.2 | BSD-3-Clause |
| collection | 1.19.1 | BSD-3-Clause |
| crypto | 3.0.7 | BSD-3-Clause |
| ffi | 2.2.0 | BSD-3-Clause |
| http | 1.6.0 | BSD-3-Clause |
| http_parser | 4.1.2 | BSD-3-Clause |
| json_schema | 5.2.2 | BSD-3-Clause |
| logging | 1.3.0 | BSD-3-Clause |
| matcher | 0.12.20 | BSD-3-Clause |
| meta | 1.18.3 | BSD-3-Clause |
| path | 1.9.1 | BSD-3-Clause |
| posix | 6.5.0 | MIT |
| quiver | 3.2.2 | Apache-2.0 |
| rfc_6901 | 0.2.1 | MIT |
| source_span | 1.10.2 | BSD-3-Clause |
| stack_trace | 1.12.1 | BSD-3-Clause |
| stream_channel | 2.1.4 | BSD-3-Clause |
| string_scanner | 1.4.1 | BSD-3-Clause |
| term_glyph | 1.2.2 | BSD-3-Clause |
| test_api | 0.7.13 | BSD-3-Clause |
| typed_data | 1.4.0 | BSD-3-Clause |
| unorm_dart | 0.3.2 | MIT (Copyright Yasuhiro Shimizu) |
| uri | 1.0.0 | BSD-3-Clause |
| web | 1.1.1 | BSD-3-Clause |

The Flutter 3.44.6 launcher embeds Flutter, Dart, plugins, and package dependencies. Flutter generates the complete
`flutter_assets/NOTICES.Z` notice bundle during each platform build; release validation requires that bundle inside
the shipped launcher.

The self-contained `TopiaForge.GameCompat.Extractor` embeds .NET Runtime 10.0.9. Packaging copies `LICENSE.TXT` and
`THIRD-PARTY-NOTICES.TXT` from the exact restored `Microsoft.NETCore.App.Runtime.<rid>/10.0.9` runtime pack into
`third_party/dotnet`; validation fails if the runtime version marker or either notice is absent. Public runners pin
.NET SDK 10.0.301 so the runtime pack and its notices cannot drift between release runs.

The extractor also embeds `System.Reflection.MetadataLoadContext` 10.0.9. That NuGet package declares MIT in its
signed `.nuspec` and ships `THIRD-PARTY-NOTICES.TXT`, but it does not contain a separate `LICENSE.TXT`. Packaging
verifies the exact MIT declaration and copies the identical .NET Foundation MIT text from the pinned runtime pack as
the MetadataLoadContext license, together with the package's exact third-party notices; all are mandatory inputs.

The game-side package validator ships `System.Reflection.Metadata` 10.0.9 and its
`System.Collections.Immutable` 10.0.9 dependency beside the TopiaForge loader.
Both signed NuGet packages declare MIT and record dotnet/dotnet commit
`901ca941248413c79832d2fdbd709da0c4386353`. Release packaging verifies the
exact netstandard2.0 DLL and notice hashes, then emits their license, notices,
and machine-readable provenance under `third_party/dotnet/runtime-loader`.
Robotopia build 2409 supplies the referenced `System.Memory`, `System.Buffers`,
and `System.Runtime.CompilerServices.Unsafe` assemblies; those player-profile
identities and hashes are validated but their proprietary game copies are not
redistributed.

The CLI's publication gate includes a generated identifier allowlist from SPDX License List Data 3.28.0.

- Project: SPDX License List Data
- Version/tag: 3.28.0 (`v3.28.0`)
- Upstream: https://github.com/spdx/license-list-data/tree/v3.28.0
- License: CC0-1.0
- Generated file: `apps/topiaforge_cli/lib/src/spdx_ids_3_28.g.dart`
- Source URLs and verified SHA-256 values: `third_party/SPDX_LICENSE_LIST_PROVENANCE.json`
