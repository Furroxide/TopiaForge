# Third Party Notices

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

TopiaForge launcher UI bundles first-party Robotopia web brand assets from `https://robotopia.gg/` and local
TopiaForge artwork for offline launcher theming.

- Web-derived raster files: `topiaforge-city-header.webp`, `baby-stitch.webp`, `robot.webp`, and `sheriff.webp`
- Source: `https://robotopia.gg/`
- Local changes: filenames were normalized for launcher packaging.

The TopiaForge pixel-art wordmark, icon, and generated platform icon variants are first-party project assets,
not Robotopia-derived third-party artwork.

TopiaForge launcher UI bundles the Quicksand font copied from the Robotopia web bundle into `packages/launcher_ui/fonts`.

- Project: Quicksand
- Source file: `https://robotopia.gg/assets/Quicksand-VariableFont_wght-DE2wFU7n.ttf`
- Upstream: https://fonts.google.com/specimen/Quicksand
- License: Open Font License according to Google Fonts at the time this notice was added.
- Local changes: none known; filename was normalized for launcher packaging.
- SHA-256: `8b3a3842cc4b666fde454446e28d1bacde30a0ac861e90cbb0bd77b02ecb9dae`
- License text: `packages/launcher_ui/fonts/Quicksand-OFL.txt` (also copied into the Unity UI bundle source)

TopiaForge bundles the Audiowide font for display typography in the launcher UI and Unity brand bundle.

- Project: Audiowide
- Source files: `https://github.com/google/fonts/raw/main/ofl/audiowide/Audiowide-Regular.ttf`, `https://github.com/google/fonts/raw/main/ofl/audiowide/OFL.txt`
- Upstream: https://fonts.google.com/specimen/Audiowide
- License: SIL Open Font License 1.1
- Bundled at: `packages/launcher_ui/fonts` and `tools/unity-ui-bundle/Assets/Fonts`
- Local changes: none known; filename was normalized for launcher and Unity packaging.
- SHA-256: `c7c0f2b0f6fad8c623e31772ce79f94a4edb9321ffce9fce978ea892d20ae730`

The standalone `topiaforge` executable embeds the Dart SDK runtime and the following runtime packages. Release
packaging copies the exact license texts resolved by `apps/topiaforge_cli/.dart_tool/package_config.json` into
`third_party/dart/LICENSES`, together with `VERSIONS.json`; package validation fails if any text is absent.

| Component | Pinned release resolution | License |
| --- | --- | --- |
| Dart SDK | 3.11.1 | BSD-3-Clause |
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

The Flutter 3.41.4 launcher embeds Flutter, Dart, plugins, and package dependencies. Flutter generates the complete
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
Robotopia build 2227 supplies the referenced `System.Memory`, `System.Buffers`,
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
