# Robotopia Launcher

Standalone Flutter desktop launcher for QuantumWorks and Robotopia.

## macOS Xcode development

Open `macos/Runner.xcworkspace`, not the `.xcodeproj`. The shared Runner scheme
sets `ROBOTOPIA_REPOSITORY_ROOT` for Run and Profile, allowing a DerivedData app
to use the checkout's BepInEx, loader, and `dist/` payload without copying those
development files into the app bundle. Prepare them before using Repair or
Browse:

```sh
dotnet build RobotopiaModManager.slnx -c Release
(cd apps/robotopia_cli && dart run bin/robotopia.dart pack --all --output ../../dist)
```

Xcode scheme pre-actions can print inherited environment variables in the build
log. Quit Xcode and reopen it from Finder before building if the launching shell
or parent application contains API tokens, signing secrets, or other
credentials. The release CLI additionally strips secret-shaped variables from
child build environments.

An Xcode Run build is a development artifact. Public macOS archives must be
assembled through the release packager so `Contents/Resources/QuantumWorks` is
embedded before final Developer ID signing and notarization.

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
