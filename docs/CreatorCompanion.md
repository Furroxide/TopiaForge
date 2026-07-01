# QuantumWorks Creator Companion (VCC-parity guide)

The QuantumWorks launcher's **Developer** tab is a Creator-Companion-style cockpit for building content for
Robotopia — the equivalent of VRChat's Creator Companion (VCC). It manages multiple projects, detects Unity,
installs/restores VPM packages, scaffolds new projects/packages from templates, and drives UGC live-sync into the
running game. Everything is also available from the `robotopia` CLI.

Enable it in **Settings → Developer mode** (off by default; consumers never see it).

## Projects (multi-project management)

The **Projects** pane lists every tracked project (persisted to `developer_projects.json` in the launcher data
root — VCC's project list). Each card shows the name, a kind badge (Mod / Unity world / Unity package), the Unity
version, and the path.

- **New mod…** — scaffolds a C# mod project (`robotopia.project.json` + a starter mod), optionally with a Unity
  companion.
- **New Unity world…** — copies the [Unity world template](#templates) and installs the UGC companion (see
  [create-from-template](#create-from-template)).
- **Add existing…** — registers any project folder; the kind is sniffed from its files (`robotopia.project.json`
  → mod; `Packages/vpm-manifest.json` → Unity world; `package.json` → Unity package).
- **Manage** — loads the project into the per-project panes below. Mod projects get the lifecycle panes (Resolve /
  Pack / Install / Doctor); Unity projects get the [Packages](UnityVpm.md) pane.
- **Open in Unity** — launches the Unity editor matching `ProjectSettings/ProjectVersion.txt` (or the newest
  installed editor, with a warning on mismatch). Detect-only: install Unity via Unity Hub yourself.
- **Remove** — untracks (never deletes files).

CLI: `robotopia projects list|add [path]|remove <path>|open [path]`.

## Unity detection

The launcher discovers installed Unity editors via Unity Hub (Windows: `%PROGRAMFILES%\Unity\Hub\Editor\*` plus
the Hub's secondary install path, and `UNITY_EDITOR_PATH`), newest first. This is **detect + open only** — the
launcher never downloads or installs Unity.

## Templates

- **Unity world** (`templates/Robotopia.UnityWorldTemplate/`) — a starter Unity project for authoring UGC levels:
  a sample scene, `Packages/manifest.json` + `Packages/vpm-manifest.json` (UGC companion + the embedded
  resolver), a VCC-style `Packages/.gitignore`, and a pinned Unity 6 version. The `template-world` analog.
- **Unity package** (`templates/Robotopia.UnityPackageTemplate/`) — a starter VPM package (Runtime/Editor asmdefs
  + `Samples~`). The `template-package` analog. Scaffold one with `robotopia unity new-package <id>`.

### Create-from-template

Creating a Unity world project copies the template, installs the `com.robotopia.ugc-companion` package into
`Packages/`, points the embedded resolver at the local listing (`vpm-resolver-repos.json`), and registers it —
the same instantiate-then-resolve flow VCC uses.

CLI: `robotopia new unity-world <name> [--dir path]`.

## Packages (VPM)

The **Packages** pane (shown when managing a Unity project) lists installed/resolved packages, available packages
to add, **Resolve All**, and subscribed repositories. See [UnityVpm.md](UnityVpm.md) for the package/manifest/
listing formats, the resolver, and the `robotopia unity …` CLI.

## UGC Live Sync cockpit

The **UGC Live Sync** pane (see [UgcLiveSync.md](UgcLiveSync.md)) auto-detects connection values, shows live
diagnostics from the game, and offers a one-button **Go Live** that runs the whole pipeline with no manual
scripts.

## VRChat-feature → QuantumWorks parity

| VCC / VPM | QuantumWorks |
|---|---|
| Project list, add/remove/open | Projects pane + `robotopia projects …` |
| New project from template | New mod… / New Unity world… + `robotopia new …` |
| `template-world` / `template-package` | `Robotopia.UnityWorldTemplate` / `Robotopia.UnityPackageTemplate` |
| Manage Project (packages) | Packages pane + `robotopia unity …` |
| Repository management | Packages pane repos + `robotopia unity repos/add-repo` |
| `vpm resolve project` | Resolve All + `robotopia unity resolve` |
| Resolver auto-restore on open | `com.robotopia.vpm-resolver` (embedded) + launcher resolver |
| `vpm-package-maker` | `robotopia unity new-package` |
| Unity version detect/open | `listUnityEditors` + Open in Unity (detect-only) |
| ClientSim (in-editor preview) | **UGC Live Sync** (preview in the real game) |
| Settings folder / project list | `%APPDATA%\RobotopiaLauncher\` (`developer_projects.json`, `vpm_sources.json`) |
