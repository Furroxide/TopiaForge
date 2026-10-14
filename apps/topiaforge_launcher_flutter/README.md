# TopiaForge Launcher

Standalone Flutter desktop launcher for TopiaForge and Robotopia.

## macOS Xcode development

Open `macos/Runner.xcworkspace`, not the `.xcodeproj`. The shared Runner scheme
sets `TOPIAFORGE_REPOSITORY_ROOT` for Run and Profile, allowing a DerivedData app
to use the checkout's BepInEx, loader, and `dist/` payload without copying those
development files into the app bundle. Prepare them before using Repair or
Browse:

```sh
dotnet build TopiaForge.slnx -c Release
(cd apps/topiaforge_cli && dart run bin/topiaforge.dart pack --all --output ../../dist)
```

Xcode scheme pre-actions can print inherited environment variables in the build
log. Quit Xcode and reopen it from Finder before building if the launching shell
or parent application contains API tokens, signing secrets, or other
credentials. The release CLI additionally strips secret-shaped variables from
child build environments.

An Xcode Run build is a development artifact. Public macOS archives must be
assembled through the release packager so `Contents/Resources/TopiaForge` is
embedded before final Developer ID signing and notarization.

## Native Desktop Icons

Native desktop launcher icons are generated from:

```text
../../packages/launcher_ui/assets/brand/topiaforge-icon.png
assets/brand/topiaforge-app-icon.png
```

The shared 144x144 pixel mark is the canonical source. The local brand tool
places it on the dark rounded launcher tile with nearest-neighbor scaling and
emits the 1024x1024 desktop master, Linux Snap icon, and website assets.
`icons_launcher` then produces the Windows and macOS variants:

```powershell
dart run tool/generate_brand_assets.dart
dart run icons_launcher:create --path icons_launcher.yaml
```

Run both commands from this app directory. Together they update the Windows
`.ico`, macOS app icon asset catalog, Linux Snap icon under `snap/gui`, and the
developer website's wordmark and favicon. Linux generation stays in the local
tool because the package name differs from the launcher's custom Snap app name.

## Single instance and `topiaforge://` links

One launcher runs per user and data root. A later start hands its session
link, if it has one, to the running launcher and exits before it shows a
window:

- **Windows.** Dart claims a named mutex through
  `windows/runner/single_instance.cpp`. The name holds the user's SID and a
  hash of the data root, so two installs with different data roots never take
  each other's links. A later start finds the owner's message-only window,
  checks that the same user owns it, allows it to come to the foreground, and
  sends the link with `WM_COPYDATA`. The owner reads only the scheme in place,
  checks the length (512 characters plus a margin) before it copies anything,
  and passes the text to Dart, never to a shell.
- **Linux.** `GApplication` negotiates uniqueness over D-Bus
  (`G_APPLICATION_HANDLES_COMMAND_LINE`). A launcher started with its own
  `TOPIAFORGE_DATA_ROOT` gets an application id of its own.
- **macOS.** LaunchServices keeps one launcher running and calls
  `application(_:open:)`.

A start with the update helper's `--topiaforge-update-health-*` options never
hands off. When another launcher owns the scope, it runs beside it and still
writes its health marker, so the update can commit.

While link handling is on, which is the default, the launcher registers itself
at start: `HKCU\Software\Classes\topiaforge` on Windows, a hidden desktop entry
made the `x-scheme-handler/topiaforge` default with `xdg-mime` on Linux, and
`CFBundleURLTypes` in the bundle on macOS. Turning it off removes the
registration while it names this launcher. A link never joins by itself; it
opens the consent flow.
