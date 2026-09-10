# TopiaForge Creator Companion (VCC-parity guide)

The TopiaForge launcher's **Developer** tab is a Creator-Companion-style cockpit for building content for
Robotopia — the equivalent of VRChat's Creator Companion (VCC). It manages multiple projects, detects Unity,
installs/restores VPM packages, and scaffolds new projects/packages from templates. Everything is also
available from the `topiaforge` CLI.

Enable it in **Settings → Developer mode** (off by default; consumers never see it).

## Projects (multi-project management)

The **Projects** pane lists every tracked project (persisted to `developer_projects.json` in the launcher data
root — VCC's project list). Each card shows the name, a kind badge (Mod / Unity world / Unity package), the Unity
version, and the path.

- **New mod…** — scaffolds a C# mod project (`topiaforge.project.json` + a starter mod), optionally with a Unity
  companion.
- **New Unity world…** — copies the [Unity world template](#templates) and restores its VPM packages (see
  [create-from-template](#create-from-template)).
- **Add existing…** — registers any project folder; the kind is sniffed from its files (`topiaforge.project.json`
  → mod; `Packages/vpm-manifest.json` → Unity world; `package.json` → Unity package).
- **Manage** — loads the project into the per-project panes below. Mod projects get the lifecycle panes (Resolve /
  Pack / Install / Doctor); Unity projects get the [Packages](UnityVpm.md) pane.
- **Open in Unity** — launches only the TopiaForge Unity authoring editor pinned in
  `ProjectSettings/ProjectVersion.txt` (`6000.0.23f1`). Detect-only: install Unity via Unity Hub yourself.
- **Remove** — untracks (never deletes files).

CLI: `topiaforge projects list|add [path]|remove <path>|open [path]`.

## Unity detection

The launcher discovers installed Unity editors via Unity Hub (Windows: `%PROGRAMFILES%\Unity\Hub\Editor\*` plus
the Hub's secondary install path, and `UNITY_EDITOR_PATH`), newest first. This is **detect + open only** — the
launcher never downloads or installs Unity.

## Templates

- **Mod templates** (`templates/mod/<id>/`) — seven directory templates for C# mods (`minimal`, `gameplay`,
  `gamemode`, `service`, `ui`, `asset`, `world`), each with a `template.json` (metadata + manifest defaults) and
  `{{TOKEN}}`-substituted sources. Scaffold with `topiaforge new mod <id> --template <id>`; list them with
  `topiaforge list templates`. See [Modding.md](Modding.md#choose-a-template).
- **Unity world** (`templates/TopiaForge.UnityWorldTemplate/`) — a starter Unity project for authoring
  custom-geometry worlds: a sample scene, `Packages/manifest.json` + `Packages/vpm-manifest.json` (world
  companion + the embedded resolver), a VCC-style `Packages/.gitignore`, and a pinned Unity 6 version. The
  `template-world` analog.
- **Unity package** (`templates/TopiaForge.UnityPackageTemplate/`) — a starter VPM package (Runtime/Editor asmdefs
  + `Samples~`). The `template-package` analog. Scaffold one with `topiaforge unity new-package <id>`.

### Create-from-template

Creating a Unity world project copies the template, restores its VPM packages into `Packages/` through the
launcher/CLI security boundary, and registers the project — the same
instantiate-then-resolve flow VCC uses. Repository subscriptions remain launcher data; scaffolding does not
write a machine-local repository path into the project. The embedded recovery bridge only detects package drift
and offers the explicit resolve command; it never performs network or archive work during Unity startup.

CLI: `topiaforge new unity-world <name> [--dir path]`. Pair it with a mod using `topiaforge world link`
and build its AssetBundle with `topiaforge world build` — see [Custom worlds](CustomWorlds.md).

## Packages (VPM)

The **Packages** pane (shown when managing a Unity project) lists installed/resolved packages, available packages
to add, **Resolve All**, and subscribed repositories. See [UnityVpm.md](UnityVpm.md) for the package/manifest/
listing formats, the resolver, and the `topiaforge unity …` CLI.

## VRChat-feature → TopiaForge parity

| VCC / VPM | TopiaForge |
|---|---|
| Project list, add/remove/open | Projects pane + `topiaforge projects …` |
| New project from template | New mod… / New Unity world… + `topiaforge new …` |
| `template-world` / `template-package` | `TopiaForge.UnityWorldTemplate` / `TopiaForge.UnityPackageTemplate` |
| Manage Project (packages) | Packages pane + `topiaforge unity …` |
| Repository management | Packages pane repos + `topiaforge unity repos/add-repo` |
| `vpm resolve project` | Resolve All + `topiaforge unity resolve` |
| Safe recovery after clone | Read-only `io.github.furroxide.topiaforge.vpm-resolver` warning + explicit launcher/CLI Resolve All |
| `vpm-package-maker` | `topiaforge unity new-package` |
| Unity version detect/open | `listUnityEditors` + Open in Unity (detect-only) |
| ClientSim (in-editor preview) | Playtest in the official [Robotopia Creator](https://robotopia.gg/editor/); custom-geometry worlds build with `topiaforge world build` |
| Settings folder / project list | `%APPDATA%\TopiaForgeLauncher\` (`developer_projects.json`, `vpm_sources.json`) |
