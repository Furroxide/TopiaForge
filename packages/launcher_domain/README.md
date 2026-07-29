# TopiaForge launcher domain

Framework-independent models and planning rules shared by the TopiaForge
launcher, CLI, and data adapters. This package owns serialized launcher
contracts, SemVer and version-range handling, dependency resolution, profile
launch configuration, registry models, and developer-project planning. It does
not access the filesystem, start processes, perform network requests, or import
Flutter.

## Use

The package is private to this repository. Use its public barrel rather than
importing files under `lib/src`:

```dart
import 'package:launcher_domain/launcher_domain.dart';

final resolution = const DependencyPlanner().resolveInstalled(
  installedMods,
  gameVersion: '0.0.2309',
  requireKnownGameVersion: true,
);
if (resolution.hasBlockingIssues) {
  for (final issue in resolution.issues) {
    print(issue.message);
  }
}
```

TopiaForge launcher build `N` maps to the canonical game version `0.0.N` when
manifest compatibility ranges are evaluated. Missing compatibility ranges mean
`*`; production callers should require a known game build for constrained mods.

Run `dart analyze` and `dart test` from this directory after contract changes.
Breaking serialized changes must increment their discriminator across every
C# and Dart consumer.

See the [compatibility policy](https://docs.topiaforge.dev/reference/compatibility/)
and [SDK overview](https://docs.topiaforge.dev/reference/sdk-overview/) for
ecosystem-facing policy.
