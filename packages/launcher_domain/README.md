# Robotopia launcher domain

Framework-independent models and planning rules shared by the Robotopia
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
  gameVersion: '0.0.2227',
  requireKnownGameVersion: true,
);
if (resolution.hasBlockingIssues) {
  for (final issue in resolution.issues) {
    print(issue.message);
  }
}
```

Robotopia launcher build `N` maps to the canonical game version `0.0.N` when
manifest compatibility ranges are evaluated. Missing compatibility ranges mean
`*`; production callers should require a known game build for constrained mods.

Run `dart analyze` and `dart test` from this directory after contract changes.
Serialized changes must remain additive unless a coordinated schema migration
is supplied for every C# and Dart consumer.

See [CompatibilityPolicy.md](../../docs/CompatibilityPolicy.md) and
[Modding.md](../../docs/Modding.md) for ecosystem-facing policy.
