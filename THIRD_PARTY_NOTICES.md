# Third Party Notices

This repository bundles BepInEx 5.4.23.5 binary runtime files under `third_party/BepInEx/win_x64_5.4.23.5` for local Robotopia loader installation.

- Project: BepInEx
- Upstream: https://github.com/BepInEx/BepInEx
- License: LGPL-2.1-or-later upstream at the time this notice was added. Verify the exact upstream release license before redistribution.
- Local changes: none known; files are treated as bundled runtime assets.

RoboPatch was used only as behavior prior art for clean-room compatibility planning. No RoboPatch code was copied or ported.

Prism Launcher was used only as product maturity and UX inspiration. No Prism Launcher code was copied or ported.

Robotopia launcher UI bundles first-party Robotopia web brand assets copied from `https://robotopia.gg/` into `packages/launcher_ui/assets/brand` for offline launcher theming.

- Files: `robotopia-logo.webp`, `robotopia-city-header.webp`, `baby-stitch.webp`, `robot.webp`, `sheriff.webp`
- Source: `https://robotopia.gg/`
- Local changes: none known; filenames were normalized for launcher packaging.

Robotopia launcher UI bundles the Quicksand font copied from the Robotopia web bundle into `packages/launcher_ui/fonts`.

- Project: Quicksand
- Source file: `https://robotopia.gg/assets/Quicksand-VariableFont_wght-DE2wFU7n.ttf`
- Upstream: https://fonts.google.com/specimen/Quicksand
- License: Open Font License according to Google Fonts at the time this notice was added.
- Local changes: none known; filename was normalized for launcher packaging.

Robotopia bundles the Audiowide font for display typography in the launcher UI and Unity brand bundle.

- Project: Audiowide
- Source files: `https://github.com/google/fonts/raw/main/ofl/audiowide/Audiowide-Regular.ttf`, `https://github.com/google/fonts/raw/main/ofl/audiowide/OFL.txt`
- Upstream: https://fonts.google.com/specimen/Audiowide
- License: SIL Open Font License 1.1
- Bundled at: `packages/launcher_ui/fonts` and `tools/unity-ui-bundle/Assets/Fonts`
- Local changes: none known; filename was normalized for launcher and Unity packaging.
