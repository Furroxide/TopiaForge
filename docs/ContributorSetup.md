# TopiaForge contributor setup

This guide is for contributors building the complete repository: the C# runtime and mods, Dart CLI, Flutter
launcher. Mod users do not need this toolchain, and mod authors can use the lighter
`topiaforge setup` flow described in [Modding.md](Modding.md).

## Supported contributor hosts

TopiaForge is developed and packaged on Windows and macOS, with launcher builds also maintained for Linux.
The repository pins Flutter **3.44.6** (Dart **3.12.2**) as its current stable toolchain. Configure that
FVM-managed SDK on `PATH`, then use ordinary `flutter` and `dart`
commands throughout this repository.

Required tools:

- .NET SDK **10.0.301**. The checked-in `global.json` requires that exact SDK, and release tooling embeds .NET runtime
  **10.0.9** so extractor bytes and notices are reproducible. Unity-facing runtime and mod projects continue to target
  `netstandard2.1` for Robotopia's Mono runtime.
- [FVM](https://fvm.app/) and its managed Flutter 3.44.6 SDK.
- Git and Git LFS.
- PowerShell 7 (`pwsh`) and 7-Zip.
- Node.js **24.16.0** or newer for the documentation portal.
- Xcode and CocoaPods for macOS launcher builds, or Visual Studio Build Tools with Desktop C++ for Windows builds.

macOS prerequisites:

```sh
brew install git-lfs powershell sevenzip cocoapods
brew install fvm
```

Windows prerequisites can be installed from an elevated terminal:

```powershell
winget install GitHub.GitLFS
winget install Microsoft.PowerShell
winget install 7zip.7zip
choco install fvm
New-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem" `
    -Name LongPathsEnabled -PropertyType DWord -Value 1 -Force
New-ItemProperty -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock" `
    -Name AllowDevelopmentWithoutDevLicense -PropertyType DWord -Value 1 -Force
git config --global core.longpaths true
```

Windows long-path support is required by the locked NuGet and Flutter dependency trees. Open a new terminal after
enabling it; `bootstrap-dev.ps1` checks the machine policy before restoring packages and configures the checkout's
Git long-path support. Windows Developer Mode allows FVM and the repository's security tests to create symlinks
without running the contributor workflow as Administrator; bootstrap probes this capability before invoking FVM.
During full verification, bootstrap also keeps the FVM-managed Dart sources at LF. This works around a Dart 3.12.2
dartdoc `@docImport` offset bug when Git for Windows is configured to check out text files as CRLF; repository source
files and the contributor's global Git configuration are not changed.

On Linux, install FVM with its standalone installer after installing Git, PowerShell 7, and 7-Zip through the
host package manager:

```sh
curl -fsSL https://fvm.app/install.sh | bash
```

The standalone FVM installation avoids depending on a separate Dart SDK. If FVM was installed through another
method, ensure its executable directory is on `PATH` before continuing.

## Flutter SDK selection

### Recommended: FVM global default

Install the pinned SDK and make it FVM's machine-wide default:

```powershell
fvm install 3.44.6 --skip-pub-get
fvm global 3.44.6
```

`fvm global` prints the exact SDK directory to add when it is not already on `PATH`. With FVM's default cache
location, prepend the following path so it takes precedence over any separately installed Flutter SDK.

For Bash, add this to `~/.bashrc`; for Zsh, add it to `~/.zshrc`:

```sh
export PATH="$HOME/fvm/default/bin:$PATH"
```

For Fish:

```fish
fish_add_path --prepend "$HOME/fvm/default/bin"
```

For Windows PowerShell, update the current terminal and persist the same directory in the user `Path`:

```powershell
$sdkBin = Join-Path $HOME "fvm\default\bin"
$env:Path = "$sdkBin;$env:Path"
$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
if (($userPath -split ";") -notcontains $sdkBin) {
    [Environment]::SetEnvironmentVariable("Path", "$sdkBin;$userPath", "User")
}
```

Open a new terminal after changing a shell profile or the Windows user `Path`.

[Sidekick](https://github.com/leoafarias/sidekick) is a recommended GUI alternative for browsing, installing,
and selecting Flutter SDK releases on Windows, macOS, and Linux. Select Flutter 3.44.6 in Sidekick, then still
configure the selected/default SDK's `bin` directory on `PATH` and run the verification below; the GUI does not
replace terminal or IDE SDK selection.

Verify both the selected versions and the executable locations. The first matching executable must be the
FVM-managed SDK, not an older system installation:

```sh
command -v flutter
command -v dart
flutter --version
dart --version
```

Windows PowerShell equivalent:

```powershell
Get-Command flutter, dart | Format-Table Name, Source
flutter --version
dart --version
```

The expected versions are Flutter **3.44.6** and Dart **3.12.2**.

### Alternative: project-specific SDK

To leave the machine-wide Flutter default unchanged, create the ignored project-local link from the repository
root:

```powershell
fvm install 3.44.6 --skip-pub-get
fvm use 3.44.6 --force --skip-pub-get
```

Then prepend the local SDK for the current terminal. Run this from the repository root before changing into an
app or package directory:

```sh
export PATH="$(pwd)/.fvm/flutter_sdk/bin:$PATH"
```

Windows PowerShell equivalent:

```powershell
$env:Path = "$(Resolve-Path .fvm/flutter_sdk/bin);$env:Path"
```

The tracked VS Code workspace configuration points the Dart and Flutter extensions at `.fvm/flutter_sdk` and
prepends that SDK to new integrated terminals on Windows, macOS, and Linux. Install the recommended Flutter
extension, run `fvm use` if the link is missing, and reload the VS Code window after changing SDK versions.

For Android Studio or IntelliJ, open **Settings > Languages & Frameworks > Flutter** (or **Preferences** on
macOS) and set the Flutter SDK to the absolute path of `<repository>/.fvm/flutter_sdk`. Some JetBrains versions
resolve and store the symlink target; after changing versions with FVM, reselect the project path and restart
Dart Analysis if the IDE continues to show the old SDK.

## Bootstrap

From the repository root:

```powershell
pwsh ./tools/bootstrap-dev.ps1
```

The bootstrap validates host tools, enables the tracked Git hooks, installs Flutter 3.44.6 through FVM,
restores Dart/Flutter/npm/NuGet dependencies, and prepares compile-only Robotopia managed references. It sets
`core.hooksPath` to `.githooks`: the tracked hooks run Git LFS integration, and `commit-msg` removes AI co-author
trailers before a commit is created.

Managed references come from the Windows archive pinned in `.github/robotopia-game-build.json`. The first run
downloads a SHA-256-verified archive of about **2.17 GB**, extracts only the managed assemblies, deletes the
temporary archive, and retains the smaller reference cache at:

| Platform | Default cache |
| --- | --- |
| macOS | `~/Library/Caches/Robotopia/managed-refs` |
| Windows | `%LOCALAPPDATA%\Robotopia\managed-refs` |
| Linux | `${XDG_CACHE_HOME:-~/.cache}/Robotopia/managed-refs` |

The restore writes ignored `Directory.Build.local.props`, allowing ordinary `dotnet build` and IDE builds to
find the cached assemblies without a persistent `ROBOTOPIA_GAME_DIR` environment variable.

The managed-reference bootstrap is an independently buildable C# tool, so it can run before the game-dependent
solution is restored or built:

```powershell
dotnet run --project tools/TopiaForge.ManagedRefs/TopiaForge.ManagedRefs.csproj -c Release -- --help
```

Release and CI gates pass `--require-latest` to that tool. The option intentionally performs an online
public-compatibility check before using any cache or restore source: it requires the latest public manifest to match
the pinned build, path, and SHA-256 for **both** Windows and macOS, then probes both public archives. A bundled or
offline restore that should not contact the public service must omit `--require-latest`.

Bootstrap options:

```powershell
pwsh ./tools/bootstrap-dev.ps1 -CacheRoot /custom/cache
pwsh ./tools/bootstrap-dev.ps1 -SkipManagedRefs
pwsh ./tools/bootstrap-dev.ps1 -Verify
```

`-SkipManagedRefs` is useful for Dart/Flutter-only work. `-Verify` runs the C# manager, runtime-integration,
managed-reference, and scaffold-validator harnesses; all Dart, Flutter, documentation, and Automerge checks; then
builds the current host's debug launcher. Platform signing, release publication, and live Robotopia acceptance remain
CI or authorized test-host gates rather than bootstrap tasks.

## Daily commands

Use the pinned SDK for Dart and Flutter work:

```powershell
dart test packages/launcher_domain
flutter test apps/topiaforge_launcher_flutter
flutter build macos --debug       # macOS
flutter build windows --debug     # Windows
```

For macOS debugging in Xcode, open
`apps/topiaforge_launcher_flutter/macos/Runner.xcworkspace`. The tracked scheme
points Run and Profile at the checkout payload; a raw DerivedData app does not
embed distributable BepInEx or mod packages. Build the C# Release output and run
`topiaforge pack --all` first when exercising Repair or Browse.

Xcode's scheme pre-action log includes inherited environment variables. Do not
launch Xcode from a terminal or parent application carrying API tokens,
publishing credentials, or signing secrets; quit it and reopen it from Finder
first. If a credential appears in an Xcode build log, rotate it and remove the
affected DerivedData build log. Repository-owned shell phases suppress their
environment listings, and release CLI child processes receive a scrubbed
environment.

The C# entry points remain standard:

```powershell
dotnet build TopiaForge.slnx -c Release
dotnet run --project tests/TopiaForge.ModManager.Tests/TopiaForge.ModManager.Tests.csproj -c Release
```

## Full verification

Run the complete suite before opening a pull request. This is the whole-repository
check that used to live in `README.md`.

```powershell
dotnet build TopiaForge.slnx -c Release
dotnet run --project tests/TopiaForge.ModManager.Tests/TopiaForge.ModManager.Tests.csproj -c Release
dotnet run --project tests/TopiaForge.ModRuntime.Tests/TopiaForge.ModRuntime.Tests.csproj -c Release
dotnet run --project tests/TopiaForge.Mods.Analyzers.Tests/TopiaForge.Mods.Analyzers.Tests.csproj -c Release
dotnet run --project tests/TopiaForge.Mods.Multiplayer.Generators.Tests/TopiaForge.Mods.Multiplayer.Generators.Tests.csproj -c Release
dotnet run --project tests/TopiaForge.Mods.Multiplayer.Tests/TopiaForge.Mods.Multiplayer.Tests.csproj -c Release
Push-Location packages/launcher_domain; dart test; dart analyze; Pop-Location
Push-Location packages/launcher_data; dart test; dart analyze; Pop-Location
Push-Location apps/topiaforge_cli; dart test; dart analyze; Pop-Location
Push-Location packages/launcher_ui; flutter test; flutter analyze; Pop-Location
Push-Location apps/topiaforge_launcher_flutter; flutter test; flutter analyze; flutter build windows --debug; Pop-Location
```

On macOS and Linux the same commands apply with `flutter build macos --debug` or
`flutter build linux --debug` in the last step.

## Optional Unity authoring

Unity is not required for the normal runtime, CLI, launcher, or mod build. World and UI AssetBundles must use
Unity **6000.0.23f1 (1c4764c07fb4)**, matching the Robotopia player. Other installed Unity versions may be used
for unrelated work but are rejected by the bundle build gate. See [CustomWorlds.md](CustomWorlds.md) and
[UiKit.md](UiKit.md) before installing the exact editor through Unity Hub.
