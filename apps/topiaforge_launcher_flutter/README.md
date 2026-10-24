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
link, if it has one, to the running launcher and exits with code 0 before it
shows a window:

- **Windows.** Dart claims a named mutex through
  `windows/runner/single_instance.cpp`. The name holds the user's SID and a
  hash of the data root, so two installs with different data roots never take
  each other's links. A later start finds the owner's message-only window,
  checks that the same user owns it, allows it to come to the foreground, and
  sends the link with `WM_COPYDATA`. The owner reads only the scheme in place,
  checks the length (512 characters plus a margin) before it copies anything,
  and passes the text to Dart, never to a shell. A closing launcher gives the
  scope up before its window goes, and refuses a link it can no longer show,
  so the start that sent it claims the scope instead.
- **Linux.** `GApplication` negotiates uniqueness over D-Bus
  (`G_APPLICATION_HANDLES_COMMAND_LINE`). The application id is chosen before
  Dart runs, so the runner resolves the data root by Dart's own rules
  (`linux/runner/data_root_id.cc`): it reads each variable as
  `Platform.environment` does, so a value Dart cannot decode as UTF-8 counts
  as unset; it trims the whitespace `String.trim()` trims, resolves a relative
  root against `getcwd()` as `Directory.current` does, and normalizes as
  package:path does. The default data root keeps the plain id however it is
  spelled; any other root gets an id of its own, from a hash of its normalized
  path. `test/linux_application_id_test.dart` compiles that code and holds it
  to Dart.
- **macOS.** LaunchServices keeps one launcher running and calls
  `application(_:open:)`.

A link start is one whose first argument has the `topiaforge:` scheme, and the
link is its only argument. Windows puts a link into the registered command
verbatim, so the Windows runner reads it whole from the raw command line,
everything after the program name: a quote inside a link cannot add launcher
arguments, and such a link is refused as damaged.

Only the update helper's exact start,
`--topiaforge-update-health-nonce <64 hex> --topiaforge-update-health-file <path>`
and nothing else, is a health start. It never hands off: when another launcher
owns the scope, it runs beside it and still writes its health marker, so the
update can commit. Its options are checked before anything is claimed, and a
start that is refused or fails before its window shows gives the scope up and
quits, with code 1 on Windows (the Linux embedder quits with 0).

While link handling is on, which is the default, a launcher on the default data
root registers itself at start: `HKCU\Software\Classes\topiaforge` on Windows,
and a hidden desktop entry made the `x-scheme-handler/topiaforge` default with
`xdg-mime` on Linux. On macOS the bundle declares the scheme with
`CFBundleURLTypes`. A launcher on its own data root leaves the registration
alone, since the system opens links with the default data root. On Windows the
default root is the one under the user's own roaming folder, read from the
user's shell-folder setting and expanded for the user's token, never from
`APPDATA` or `USERPROFILE`, so a run isolated with a scratch `APPDATA` or
`USERPROFILE` never registers. Turning link
handling off removes the registration while it names this launcher. The choice
is kept with the other multiplayer settings, so "off" lasts across restarts,
and every start while it is off removes a registration that still names this
launcher, such as one a failed removal left. A link never joins by itself; it
opens the consent flow.
