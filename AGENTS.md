# Robotopia Agent Guide

## Project Shape

- C# runtime loader projects live under `src/Robotopia.*`.
- The standalone launcher lives under `apps/robotopia_launcher_flutter`.
- Launcher packages live under `packages/launcher_domain`, `packages/launcher_data`, and `packages/launcher_ui`.
- Keep domain logic UI-independent. Flutter screens dispatch `LauncherEvent`s to `LauncherBloc`; blocs talk to `LauncherRepository`.
- Use Bloc classes for Flutter application state. Do not introduce Cubit-based launcher state.
- Keep non-generated Dart files at 500 lines or fewer. Split by feature/responsibility before a file grows past that cap.
- Prefer CLEAN, SOLID, and OOP boundaries: domain models/planners stay framework-independent, data services own filesystem/process/archive work, and widgets remain presentation-focused.
- Keep the BepInEx runtime loader as the game-side component. The launcher owns detection, install/repair, profiles, package planning, diagnostics, and launch orchestration.

## Verification

Run these before handoff when touching the relevant areas:

```powershell
dotnet build RobotopiaModManager.slnx -c Release
dotnet run --project tests\Robotopia.ModManager.Tests\Robotopia.ModManager.Tests.csproj -c Release
dart test packages\launcher_domain
dart test packages\launcher_data
dart analyze packages\launcher_domain
dart analyze packages\launcher_data
flutter test packages\launcher_ui
flutter analyze packages\launcher_ui
flutter analyze apps\robotopia_launcher_flutter
flutter test apps\robotopia_launcher_flutter
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
- Required screens are Library/Launch, Mods, Browse, Profiles, Diagnostics, and Settings.
- Include loading, empty, error, warning, destructive confirmation, focus, and no-overflow states.
- Use Flutter Material icons for common commands.

## C# Runtime Boundaries

- Preserve `.robotopiamod`, `robotopia.mod.json`, dependency ordering, package inbox, manager logs, enable/disable state, and restart-required behavior.
- Keep Unity/BepInEx-specific work in `src/Robotopia.ModManager`.
- Keep `src/Robotopia.ModManager.Core` free of Unity references.
- SDK conveniences in `Robotopia.Mods.Abstractions` must remain additive and clean-room.
