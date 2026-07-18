# TopiaForge

TopiaForge is the umbrella toolkit for modding Robotopia: a runtime mod loader, a
standalone desktop launcher, and a developer CLI for the Unity Mono build of Robotopia.

## For players (installing & playing mods)

You need **no developer tools** -- no Flutter, Dart, .NET, or Node. Download the release zip for your OS:
`TopiaForge-windows-x64.zip`, `TopiaForge-macos-universal.zip`, or `TopiaForge-linux-x64.zip`. Windows and
Linux packages expose the launcher in `launcher/` and a root `topiaforge` CLI executable; macOS packages expose
`TopiaForge.app` plus a root `topiaforge` shim. The launcher detects your Robotopia installation, repairs the Windows
or macOS runtime payload, and lets you browse, install, enable/disable, and launch mods. The **Developer** tab is hidden by
default -- turn it on under **Settings -> Developer mode** only if you build mods.

For the initial release, launcher upgrades are manual: download the next signed platform package from the official
GitHub Releases page. Automatic self-update is intentionally excluded until the client can verify owner-signed
metadata and enforce bounded extraction independently of the update index.

The initial compatibility target is Robotopia build **2227**. The built-in registry initially carries verified
first-party release artifacts only; community authors can use the documented self-hosted registry format while
official submission governance is being established.

Native macOS launcher/runtime packages are supported, but the current Unity-authored TopiaForgeUi brand bundle and
custom-world bundles target `StandaloneWindows64`. Custom worlds are therefore Windows/Proton-only for now;
on macOS, TopiaForgeUi falls through its documented font fallback chain if the brand bundle cannot load.

## For mod developers

Start with the walkthrough: [docs/YourFirstMod.md](docs/YourFirstMod.md). The release zip contains the
`topiaforge` CLI at its root — add it to `PATH` and you're set (see
[docs/Modding.md → Install the CLI](docs/Modding.md#install-the-cli)). Validate your machine first
(`topiaforge setup` to auto-fix what it safely can, or `topiaforge doctor` to audit read-only). Only the .NET SDK
is required to build mods; Node/Unity are optional (UGC live-sync). See [docs/Modding.md](docs/Modding.md) for
the full reference. Build branded in-game UI for Robotopia (windows, HUDs, modals, toasts) with the TopiaForge UI kit — see
[docs/UiKit.md](docs/UiKit.md) and the F8 gallery mod. The complete first-party catalog and candidate gameplay
acceptance flows are in [docs/FirstPartyMods.md](docs/FirstPartyMods.md).

## Standalone launcher

The next-generation desktop launcher is in:

```powershell
apps\topiaforge_launcher_flutter
```

Run it locally with:

```powershell
cd apps\topiaforge_launcher_flutter
flutter run -d windows   # or -d macos / -d linux
```

The launcher uses Flutter with Bloc state management. It detects the known Robotopia installation, validates the Robotopia payload (`Robotopia.exe` on Windows/Proton, `Robotopia.app` on macOS), repairs bundled BepInEx and the C# loader, manages profiles, previews dependency/conflict plans before package installs, launches Robotopia, and creates diagnostic bundles.

Current launcher state management uses `Bloc<LauncherEvent, LauncherState>` rather than Cubit. Non-generated Dart source files are kept at 500 lines or fewer and split by responsibility.

## Developer workflow

TopiaForge has a Creator Companion style workflow with project manifests, lock files, package sources, restore, generated C# references, a `topiaforge` CLI, and a launcher Developer surface.

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

The workflow is inspired by VCC/VPM concepts and remains Robotopia-native: `.topiaforgemod` is still the runtime package format, while `topiaforge.project.json`, `topiaforge.lock.json`, and generated `topiaforge.dev.props` support source-controlled mod development.

## Local install

```sh
topiaforge dev-install                     # add --game-dir <path> to override Robotopia detection
```

(From a source checkout: `cd apps/topiaforge_cli && dart run topiaforge dev-install`.)

This installs BepInEx 5.4.23.5 and the manager plugin into the detected Robotopia installation:

| Platform | Default Robotopia location |
| --- | --- |
| Windows | `%LOCALAPPDATA%\Tomato Cake\launcher\Robotopia` |
| macOS | `~/Library/Application Support/Tomato Cake/launcher/Robotopia.app` (BepInEx installs beside the app) |
| Linux | No auto-detect — Robotopia uses the Windows build under Proton/Wine; pass `--game-dir` pointing at the Robotopia folder inside your prefix |

On Linux, launch Robotopia with `WINEDLLOVERRIDES="winhttp=n,b"` so the mod loader injects. The `ROBOTOPIA_GAME_DIR` environment variable overrides Robotopia detection on every platform.

Launch Robotopia, then open the manager from the main-menu **TopiaForge** button or press `F10`.

## Package format

Mods are `.topiaforgemod` zip files with a required `topiaforge.mod.json` manifest and a C# entry class that derives from `TopiaForge.Mods.TopiaForgeMod`.

Scaffold a mod and pack it:

```sh
topiaforge new mod yourname.firstmod --name "First Mod" --author "Your Name" --license MIT
cd yourname.firstmod
topiaforge pack
```

`topiaforge pack --all` packs every first-party mod under `mods/`, and `topiaforge unity pack-packages` regenerates the VPM listing in `dist/vpm/`.

Packages can be installed from the Robotopia manager's package tab by full path, or by placing them into:

```text
BepInEx\TopiaForge\package-inbox
```

## Trust model

TopiaForge uses trusted local packages. Do not install `.topiaforgemod` files unless you trust their source; C# mods execute code in the Robotopia process.

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
dotnet build TopiaForge.slnx -c Release
dotnet run --project tests\TopiaForge.ModManager.Tests\TopiaForge.ModManager.Tests.csproj -c Release
Push-Location packages\launcher_domain; dart test; dart analyze; Pop-Location
Push-Location packages\launcher_data; dart test; dart analyze; Pop-Location
Push-Location apps\topiaforge_cli; dart test; dart analyze; Pop-Location
Push-Location packages\launcher_ui; flutter test; flutter analyze; Pop-Location
Push-Location apps\topiaforge_launcher_flutter; flutter test; flutter analyze; flutter build windows --debug; Pop-Location
```

On macOS/Linux the same commands apply with `flutter build macos --debug` / `flutter build linux --debug` in the last step.
