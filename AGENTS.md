# TopiaForge Agent Guide

## Project Shape

- C# runtime loader projects live under `src/TopiaForge.*`.
- The standalone launcher lives under `apps/topiaforge_launcher_flutter`.
- Launcher packages live under `packages/launcher_domain`, `packages/launcher_data`, and `packages/launcher_ui`.
- Keep domain logic UI-independent. Flutter screens dispatch `LauncherEvent`s to `LauncherBloc`; blocs talk to `LauncherRepository`.
- Use Bloc classes for Flutter application state. Do not introduce Cubit-based launcher state.
- Keep non-generated Dart files at 500 lines or fewer. Split by feature/responsibility before a file grows past that cap.
- Prefer CLEAN, SOLID, and OOP boundaries: domain models/planners stay framework-independent, data services own filesystem/process/archive work, and widgets remain presentation-focused.
- Keep the BepInEx runtime loader as the game-side component. The launcher owns detection, install/repair, profiles, package planning, diagnostics, and launch orchestration.

## Verification

Run these before handoff when touching the relevant areas:

```powershell
dotnet build TopiaForge.slnx -c Release
dotnet run --project tests\TopiaForge.ModManager.Tests\TopiaForge.ModManager.Tests.csproj -c Release
dotnet run --project tests\TopiaForge.ModRuntime.Tests\TopiaForge.ModRuntime.Tests.csproj -c Release
dotnet run --project tests\TopiaForge.Mods.Analyzers.Tests\TopiaForge.Mods.Analyzers.Tests.csproj -c Release
dotnet run --project tests\TopiaForge.Mods.Multiplayer.Generators.Tests\TopiaForge.Mods.Multiplayer.Generators.Tests.csproj -c Release
dotnet run --project tests\TopiaForge.Mods.Multiplayer.Tests\TopiaForge.Mods.Multiplayer.Tests.csproj -c Release
dart test packages\launcher_domain
dart test packages\launcher_data
dart test apps\topiaforge_cli
dart analyze packages\launcher_domain
dart analyze packages\launcher_data
dart analyze apps\topiaforge_cli
flutter test packages\launcher_ui
flutter analyze packages\launcher_ui
flutter analyze apps\topiaforge_launcher_flutter
flutter test apps\topiaforge_launcher_flutter
flutter build windows --debug
```

Line-count audit:

```powershell
$rows = @(); foreach ($file in rg --files -g "*.dart") { $count = (Get-Content -LiteralPath $file).Count; $rows += [PSCustomObject]@{Lines=$count; Path=$file} }; $rows | Sort-Object Lines -Descending
```

## Licensing Rules

- Do not copy RoboPatch code. Reimplement compatibility from observed behavior and documentation only.
- Do not copy Prism Launcher code. Use it only as product/UX inspiration.
- Preserve notices for bundled third-party runtime assets such as BepInEx.
- If any third-party code is copied later, add provenance, license text, and modified-file notes to `THIRD_PARTY_NOTICES.md`.

## UI Quality Bar

- Build a real desktop utility, not a landing page.
- Prefer a quiet, dense layout: left navigation, profile selector, prominent launch button, status bar, mod list, and detail pane.
- Required screens are Home (launch pad + discovery), Setup (runtime/world/load-order config), Mods, Browse, Profiles, Diagnostics, and Settings.
- Include loading, empty, error, warning, destructive confirmation, focus, and no-overflow states.
- Use Flutter Material icons for common commands.

## In-game SDK UI Quality Bar

- All in-game UI (manager overlay, mod HUDs, mod windows) goes through the TopiaForgeUi kit in
  `src/TopiaForge.Mods.UnityUi` — never hand-rolled uGUI in consumers. See `docs/UiKit.md`.
- Paper scheme for full-screen tools/windows/dialogs; HUD scheme for gameplay overlays.
  Tokens/tones only — no hex literals in consumer code.
- Per-frame updates use the kit's dirty-checked setters; zero steady-state allocation
  (cached strings, `SetText(prefix, int)`, pooled spawned elements, `.Dynamic()` around
  per-frame-churning subtrees).
- Destructive actions confirm through `Modal.Destructive`; action results surface as
  toasts; long content always lives in a scroll view or virtualized list.
- Canvas sorting comes from the kit's band allocator — never set sortingOrder directly.
- Respect the accessibility contract: high contrast, UI scale, and reduced-motion/motion
  intensity flow through `TopiaForgeTheme` (feed mod config into it, like Zombies does).
- Split UI files by responsibility (~400 lines max); the `mods/TopiaForge.UiGallery` dev
  mod (F8) is the living catalog and manual QA surface.

## C# Runtime Boundaries

- Preserve `.topiaforgemod`, `topiaforge.mod.json`, dependency ordering, package inbox, manager logs, enable/disable state, and restart-required behavior.
- Keep Unity/BepInEx-specific work in `src/TopiaForge.ModManager`.
- Keep `src/TopiaForge.ModManager.Core` free of Unity references.
- SDK conveniences in `TopiaForge.Mods.Abstractions` must remain additive and clean-room.
