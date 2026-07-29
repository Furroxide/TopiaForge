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
