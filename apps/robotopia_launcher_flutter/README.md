# Robotopia Launcher

Standalone Flutter desktop launcher for QuantumWorks and Robotopia.

## Native Desktop Icons

Native desktop launcher icons are generated from:

```text
assets/brand/quantumworks-app-icon.png
```

Use the globally activated Dart package, not a `pubspec.yaml` dev dependency:

```powershell
dart pub global activate icons_launcher 3.1.0
dart pub global run icons_launcher:create --path icons_launcher.yaml
```

Run the commands from this app directory. The generator updates the Windows
`.ico`, macOS app icon asset catalog, and Linux Snap icon under `snap/gui`.
