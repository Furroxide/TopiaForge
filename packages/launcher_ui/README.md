# Robotopia launcher UI

Shared Flutter theme, motion policy, brand assets, and reusable desktop widgets
for the Robotopia launcher. Application state stays in `LauncherBloc`; this
package contains presentation primitives only and performs no filesystem,
process, archive, or network work.

## Use

```dart
import 'package:flutter/material.dart';
import 'package:launcher_ui/launcher_ui.dart';

MaterialApp(
  theme: buildQuantumWorksTheme(),
  home: const BorderedPane(
    child: EmptyStatePanel(
      icon: Icons.extension_off,
      title: 'No mods installed',
      message: 'Install a .robotopiamod package to begin.',
    ),
  ),
);
```

Use Material icons for common commands, preserve keyboard and screen-reader
semantics, and obtain motion duration through the reduced-motion helpers.
Screens remain responsible for dispatching events rather than mutating state or
performing IO.

Run `flutter analyze` and `flutter test` from this directory after changes.
The package is private to this repository and is not published to pub.dev.
