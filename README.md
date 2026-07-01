# QuantumWorks

QuantumWorks is the umbrella toolkit for modding Robotopia: a runtime mod loader, a
standalone desktop launcher, and a developer CLI for the Unity Mono build of Robotopia.

## For players (installing & playing mods)

You need **no developer tools** — no Flutter, Dart, .NET, or Node. Get the launcher from the release package
(the `launcher/` folder inside `RobotopiaModManager.zip`) and run `robotopia_launcher_flutter.exe`. It detects your
Robotopia install, repairs the runtime, and lets you browse, install, enable/disable, and launch mods. The
**Developer** tab is hidden by default — turn it on under **Settings → Developer mode** only if you build mods.

## For mod developers

Validate your machine first with the CLI (`robotopia setup` to auto-fix what it safely can, or `robotopia doctor`
to audit read-only). Only the .NET SDK is required to build mods; Node/Unity are optional (UGC live-sync). See
[docs/Modding.md](docs/Modding.md).

## Standalone launcher

The next-generation desktop launcher is in:

```powershell
apps\robotopia_launcher_flutter
```

Run it locally with:

```powershell
cd apps\robotopia_launcher_flutter
flutter run -d windows
```

The launcher uses Flutter with Bloc state management. It detects the known Robotopia install, validates `Robotopia.exe`, repairs bundled BepInEx and the C# loader, manages profiles, previews dependency/conflict plans before package installs, detects legacy `Robotopia\Mods` entries, launches Robotopia, and creates diagnostic bundles.

Current launcher state management uses `Bloc<LauncherEvent, LauncherState>` rather than Cubit. Non-generated Dart source files are kept at 500 lines or fewer and split by responsibility.

## Developer workflow

Robotopia has a Creator Companion style workflow with project manifests, lock files, package sources, restore, generated C# references, a `robotopia` CLI, and a launcher Developer surface.

Start here:

```text
docs\CreatorCompanionParity.md
```

The workflow is inspired by VCC/VPM concepts and remains Robotopia-native: `.robotopiamod` is still the runtime package format, while `robotopia.project.json`, `robotopia.lock.json`, and generated `robotopia.dev.props` support source-controlled mod development.

## Local install

```powershell
.\tools\install-local.ps1
```

This installs BepInEx 5.4.23.5 and the manager plugin into:

```text
C:\Users\vanst\AppData\Local\Tomato Cake\launcher\Robotopia
```

Launch Robotopia, then open the manager from the main-menu **QuantumWorks** button or press `F10`.

## Package format

Mods are `.robotopiamod` zip files with a required `robotopia.mod.json` manifest and a C# assembly that implements `Robotopia.Mods.IRobotopiaMod`.

Use the sample template:

```powershell
.\tools\pack-mod.ps1 -ProjectDir .\templates\Robotopia.ModTemplate -OutputDir .\dist
```

Packages can be installed from the in-game package tab by full path, or by placing them into:

```text
BepInEx\RobotopiaModManager\package-inbox
```

## Trust model

V1 uses trusted local packages. Do not install `.robotopiamod` files unless you trust their source; C# mods execute code in the game process.

## Verification

```powershell
dotnet build RobotopiaModManager.slnx -c Release
dotnet run --project tests\Robotopia.ModManager.Tests\Robotopia.ModManager.Tests.csproj -c Release
Push-Location packages\launcher_domain; dart test; dart analyze; Pop-Location
Push-Location packages\launcher_data; dart test; dart analyze; Pop-Location
Push-Location apps\robotopia_cli; dart test; dart analyze; Pop-Location
Push-Location packages\launcher_ui; flutter test; flutter analyze; Pop-Location
Push-Location apps\robotopia_launcher_flutter; flutter test; flutter analyze; flutter build windows --debug; Pop-Location
```
