# Robotopia launcher data

Filesystem, archive, process, HTTP, persistence, installation, diagnostics, and
developer-tool adapters for the Robotopia desktop launcher. The implementation
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

Run `dart analyze` and `dart test` from this directory. Tests use temporary
roots and injectable process starters; do not point them at a real game install.

See [ContributorSetup.md](../../docs/ContributorSetup.md) and
[Modding.md](../../docs/Modding.md) for complete workflows.
