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

Prerelease launchers check the signed beta channel by default after a persisted
cooldown. Updates are verified against embedded Ed25519 trust, downloaded and
extracted within signed bounds, and replace the complete package only after
explicit confirmation; a startup health handshake enables automatic rollback.
Unsupported layouts use the verified manual GitHub Releases download. See
[launcher updates](docs/LauncherUpdates.md).

The current compatibility target is Robotopia build **2309**. The built-in registry initially carries verified
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
is required to build mods; Node/Unity are optional. See [docs/Modding.md](docs/Modding.md) for
the full reference. Build branded in-game UI for Robotopia (windows, fullscreen tools, graph
editors, HUDs, modals, and toasts) with the TopiaForge UI kit — see
[docs/UiKit.md](docs/UiKit.md) and the F8 gallery mod. Add safe creator catalogs and reversible
sessions with [Creator Content](docs/CreatorTools.md). The complete first-party catalog and
candidate gameplay acceptance flows are in [docs/FirstPartyMods.md](docs/FirstPartyMods.md).

TopiaForge 1.0 remains standalone-only, while the stable multiplayer API preview lets authors opt a V5 mod into
generated server-canonical contracts, loopback play, and deterministic multi-peer tests before live
transport ships. V5 is also the normal standalone manifest when `multiplayer` is omitted; pre-release V4 was retired.
See
[docs/Multiplayer.md](docs/Multiplayer.md) and [docs/ManifestV5.md](docs/ManifestV5.md).

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

| Platform | Built-in discovery |
| --- | --- |
| Windows | `%LOCALAPPDATA%\Tomato Cake\launcher\Robotopia`, plus Robotopia manifests in declared Steam libraries |
| macOS | `~/Library/Application Support/Tomato Cake/launcher` (the folder containing `Robotopia.app`; BepInEx installs beside the app), plus Robotopia manifests in declared Steam libraries |
| Linux | Robotopia manifests in Steam libraries declared by `libraryfolders.vdf`, including the Windows payload used by Proton |

Steam discovery requires an app manifest whose name and install directory are both exactly `Robotopia`; TopiaForge does not guess a Steam app id or recursively scan Wine/Proton prefixes. For another store or a custom prefix, pass `--game-dir` pointing at the Windows-layout Robotopia folder. On Linux, launch Robotopia with `WINEDLLOVERRIDES="winhttp=n,b"` so the mod loader injects. The `ROBOTOPIA_GAME_DIR` environment variable overrides automatic detection on every platform.

Launch Robotopia, then open the manager from the main-menu **TopiaForge** button or press `F10`.

## Package format

Mods are `.topiaforgemod` zip files with a required `topiaforge.mod.json` manifest and a C# entry class that derives from `TopiaForge.Mods.TopiaForgeMod`.

Scaffold a mod and pack it:

```sh
topiaforge new mod yourname.firstmod --name "First Mod" --author "Your Name" --license AGPL-3.0-or-later
cd yourname.firstmod
topiaforge pack
```

`topiaforge pack --all` packs the non-DevTool first-party mods under `mods/`; add
`--include-dev-mods` to include Creator Tools and UiGallery. Release packaging adds Creator Tools
explicitly while keeping UiGallery out of the player payload. `topiaforge unity pack-packages`
regenerates the VPM listing in `dist/vpm/`.

Packages can be installed from the Robotopia manager's package tab by full path, or by placing them into:

```text
BepInEx\TopiaForge\package-inbox
```

## Trust model

TopiaForge uses trusted local packages. Do not install `.topiaforgemod` files unless you trust their source; C# mods execute code in the Robotopia process.

## License

TopiaForge is free software: you can redistribute it and/or modify it under the terms of the
[GNU Affero General Public License](LICENSE), version 3 or later (`AGPL-3.0-or-later`),
`Copyright (C) 2026 furroxide`.

The SDK packages are covered by the same terms with no linking exception, so a mod distributed
against the TopiaForge SDK must also be licensed `AGPL-3.0-or-later`. Third-party materials keep
their original licenses; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Contributions are
accepted under the same terms with a [DCO 1.1](DCO) sign-off.

## Community and project policy

See [CONTRIBUTING.md](CONTRIBUTING.md), [SECURITY.md](SECURITY.md), [SUPPORT.md](SUPPORT.md),
[CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md), and the
[compatibility policy](docs/CompatibilityPolicy.md). The release-facing component and contract map is in
[docs/ArchitectureInventory.md](docs/ArchitectureInventory.md). Maintainers preparing a release should use the
[release checklist](docs/ReleaseChecklist.md) and close the current
[launch blocker register](docs/LaunchBlockers.md). Remote-service and sensitive-capability behavior is documented in
[docs/PrivacyAndCapabilities.md](docs/PrivacyAndCapabilities.md).

## Trademarks and affiliation

TopiaForge is an independent, community-built modding toolkit. It is not developed, published, or endorsed by
Tomato Cake or the Robotopia development team, and is not otherwise affiliated with them. "Robotopia" and
"Tomato Cake" are the property of their respective owners and are used here only to identify the game this
toolkit works with.

## Verification

```powershell
dotnet build TopiaForge.slnx -c Release
dotnet run --project tests\TopiaForge.ModManager.Tests\TopiaForge.ModManager.Tests.csproj -c Release
dotnet run --project tests\TopiaForge.ModRuntime.Tests\TopiaForge.ModRuntime.Tests.csproj -c Release
dotnet run --project tests\TopiaForge.Mods.Analyzers.Tests\TopiaForge.Mods.Analyzers.Tests.csproj -c Release
dotnet run --project tests\TopiaForge.Mods.Multiplayer.Generators.Tests\TopiaForge.Mods.Multiplayer.Generators.Tests.csproj -c Release
dotnet run --project tests\TopiaForge.Mods.Multiplayer.Tests\TopiaForge.Mods.Multiplayer.Tests.csproj -c Release
Push-Location packages\launcher_domain; dart test; dart analyze; Pop-Location
Push-Location packages\launcher_data; dart test; dart analyze; Pop-Location
Push-Location apps\topiaforge_cli; dart test; dart analyze; Pop-Location
Push-Location packages\launcher_ui; flutter test; flutter analyze; Pop-Location
Push-Location apps\topiaforge_launcher_flutter; flutter test; flutter analyze; flutter build windows --debug; Pop-Location
```

On macOS/Linux the same commands apply with `flutter build macos --debug` / `flutter build linux --debug` in the last step.
