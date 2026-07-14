# QuantumWorks

QuantumWorks is the umbrella toolkit for modding Robotopia: a runtime mod loader, a
standalone desktop launcher, and a developer CLI for the Unity Mono build of Robotopia.

## For players (installing & playing mods)

You need **no developer tools** -- no Flutter, Dart, .NET, or Node. Download the release zip for your OS:
`QuantumWorks-windows-x64.zip`, `QuantumWorks-macos-universal.zip`, or `QuantumWorks-linux-x64.zip`. Windows and
Linux packages expose the launcher in `launcher/` and a root `robotopia` CLI executable; macOS packages expose
`QuantumWorks.app` plus a root `robotopia` shim. The launcher detects your Robotopia install, repairs the Windows
or macOS runtime payload, and lets you browse, install, enable/disable, and launch mods. The **Developer** tab is hidden by
default -- turn it on under **Settings -> Developer mode** only if you build mods.

For the initial release, launcher upgrades are manual: download the next signed platform package from the official
GitHub Releases page. Automatic self-update is intentionally excluded until the client can verify owner-signed
metadata and enforce bounded extraction independently of the update index.

The initial compatibility target is Robotopia build **2227**. The built-in registry initially carries verified
first-party release artifacts only; community authors can use the documented self-hosted registry format while
official submission governance is being established.

Native macOS launcher/runtime packages are supported, but the current Unity-authored QwUi brand bundle and
custom-world bundles target `StandaloneWindows64`. Custom worlds are therefore Windows/Proton-only for now;
on macOS, QwUi falls through its documented font fallback chain if the brand bundle cannot load.

## For mod developers

Start with the walkthrough: [docs/YourFirstMod.md](docs/YourFirstMod.md). The release zip contains the
`robotopia` CLI at its root — add it to `PATH` and you're set (see
[docs/Modding.md → Install the CLI](docs/Modding.md#install-the-cli)). Validate your machine first
(`robotopia setup` to auto-fix what it safely can, or `robotopia doctor` to audit read-only). Only the .NET SDK
is required to build mods; Node/Unity are optional (UGC live-sync). See [docs/Modding.md](docs/Modding.md) for
the full reference. Build branded in-game UI (windows, HUDs, modals, toasts) with the QuantumWorks UI kit — see
[docs/UiKit.md](docs/UiKit.md) and the F8 gallery mod. The complete first-party catalog and candidate gameplay
acceptance flows are in [docs/FirstPartyMods.md](docs/FirstPartyMods.md).

## Standalone launcher

The next-generation desktop launcher is in:

```powershell
apps\robotopia_launcher_flutter
```

Run it locally with:

```powershell
cd apps\robotopia_launcher_flutter
flutter run -d windows   # or -d macos / -d linux
```

The launcher uses Flutter with Bloc state management. It detects the known Robotopia install, validates the game payload (`Robotopia.exe` on Windows/Proton, `Robotopia.app` on macOS), repairs bundled BepInEx and the C# loader, manages profiles, previews dependency/conflict plans before package installs, detects legacy `Robotopia\Mods` entries, launches Robotopia, and creates diagnostic bundles.

Current launcher state management uses `Bloc<LauncherEvent, LauncherState>` rather than Cubit. Non-generated Dart source files are kept at 500 lines or fewer and split by responsibility.

## Developer workflow

Robotopia has a Creator Companion style workflow with project manifests, lock files, package sources, restore, generated C# references, a `robotopia` CLI, and a launcher Developer surface.

Contributors building the complete repository on Windows or macOS should run the cross-platform bootstrap first:

```powershell
pwsh ./tools/bootstrap-dev.ps1 -Verify
```

See [docs/ContributorSetup.md](docs/ContributorSetup.md) for prerequisites, the pinned Flutter SDK, managed
reference caching, and platform-specific build notes.

Start here:

```text
docs\CreatorCompanionParity.md
```

The workflow is inspired by VCC/VPM concepts and remains Robotopia-native: `.robotopiamod` is still the runtime package format, while `robotopia.project.json`, `robotopia.lock.json`, and generated `robotopia.dev.props` support source-controlled mod development.

## Local install

```sh
robotopia dev-install                     # add --game-dir <path> to override detection
```

(From a source checkout: `cd apps/robotopia_cli && dart run robotopia dev-install`.
`tools\install-local.ps1` still works as a deprecated wrapper around the same command.)

This installs BepInEx 5.4.23.5 and the manager plugin into the detected game install:

| Platform | Default game location |
| --- | --- |
| Windows | `%LOCALAPPDATA%\Tomato Cake\launcher\Robotopia` |
| macOS | `~/Library/Application Support/Tomato Cake/launcher/Robotopia.app` (BepInEx installs beside the app) |
| Linux | No auto-detect — the game is the Windows build under Proton/Wine; pass `--game-dir` pointing at the game folder inside your prefix |

On Linux, launch the game with `WINEDLLOVERRIDES="winhttp=n,b"` so the mod loader injects. The `ROBOTOPIA_GAME_DIR` environment variable overrides game detection on every platform.

Launch Robotopia, then open the manager from the main-menu **QuantumWorks** button or press `F10`.

## Package format

Mods are `.robotopiamod` zip files with a required `robotopia.mod.json` manifest and a C# assembly that implements `Robotopia.Mods.IRobotopiaMod`.

Scaffold a mod and pack it:

```sh
robotopia new mod yourname.firstmod --name "First Mod" --author "Your Name" --license MIT
cd yourname.firstmod
robotopia pack
```

`robotopia pack --all` packs every first-party mod under `mods/`, and `robotopia unity pack-packages` regenerates the VPM listing in `dist/vpm/`.

Packages can be installed from the in-game package tab by full path, or by placing them into:

```text
BepInEx\RobotopiaModManager\package-inbox
```

## Trust model

Robotopia uses trusted local packages. Do not install `.robotopiamod` files unless you trust their source; C# mods execute code in the game process.

## Community and project policy

See [CONTRIBUTING.md](CONTRIBUTING.md), [SECURITY.md](SECURITY.md), [SUPPORT.md](SUPPORT.md),
[CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md), and the
[compatibility policy](docs/CompatibilityPolicy.md). The release-facing component and contract map is in
[docs/ArchitectureInventory.md](docs/ArchitectureInventory.md). Maintainers preparing a release should use the
[release checklist](docs/ReleaseChecklist.md) and close the current
[launch blocker register](docs/LaunchBlockers.md). Remote-service and sensitive-capability behavior is documented in
[docs/PrivacyAndCapabilities.md](docs/PrivacyAndCapabilities.md).

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

On macOS/Linux the same commands apply with `flutter build macos --debug` / `flutter build linux --debug` in the last step.
