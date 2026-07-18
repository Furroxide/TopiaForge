# TopiaForge launcher data

Filesystem, archive, process, HTTP, persistence, installation, diagnostics, and
developer-tool adapters for the TopiaForge desktop launcher. The implementation
fulfils repository interfaces from `launcher_domain`; UI and Bloc code should
not reproduce these operations.

## Use

The package is private to this repository:

```dart
import 'package:launcher_data/launcher_data.dart';

Future<void> inspectInstall() async {
  final repository = LocalLauncherRepository();
  try {
    final snapshot = await repository.loadSnapshot();
    print(snapshot.gameInstall?.gameVersionLabel ?? 'No game detected');
  } finally {
    await repository.dispose();
  }
}
```

`LocalLauncherRepository` owns player-facing installation and launch state.
`LocalDeveloperRepository` owns scaffolding, restore, build, Unity/VPM, and
package-authoring workflows. Both enforce bounded reads, safe archive paths,
atomic persistence, and no-follow checks at trust boundaries.

## Game discovery

Game discovery is adapter-based and injected into `LocalLauncherRepository`.
The built-in service considers, in order:

1. the player's saved selection;
2. `ROBOTOPIA_GAME_DIR`;
3. the documented Tomato Cake location on Windows or macOS; and
4. Steam library manifests on Windows, macOS, and Linux/Proton.

Steam discovery does not assume an app id or scan arbitrary prefixes. It reads
Steam's declared `libraryfolders.vdf` files and accepts only an app manifest
whose exact `name` and `installdir` are both `Robotopia`. Every path is then
validated by the normal Robotopia layout validator, canonicalized,
de-duplicated, and returned with source provenance. A manually selected folder
is the fallback for custom stores or layouts and is saved for future launches.

Tests can inject a `GameInstallDiscoveryService` or individual
`GameInstallDiscoveryAdapter` implementations. Passing the legacy
`knownGamePath` constructor argument intentionally creates a fixed-only service
that bypasses saved settings, so an explicit CLI `--game-dir` always wins and
repository tests never inspect the developer machine. Multi-install enumeration
is exposed through the optional domain `GameInstallDiscoveryRepository`
capability; consumers of a plain `LauncherRepository` can retain the existing
single-install `detectKnownInstall()` contract.

Run `dart analyze` and `dart test` from this directory. Tests use temporary
roots and injectable process starters; do not point them at a real game install.

See [contributor setup](https://github.com/furroxide/TopiaForge/blob/main/docs/ContributorSetup.md)
and the [SDK overview](https://docs.topiaforge.dev/reference/sdk-overview/) for
complete workflows.
