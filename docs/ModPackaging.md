# Mod Packaging

A `.robotopiamod` is a plain zip. This page is the exact anatomy: what `robotopia pack` puts in it, how
integrity is tracked, and how packages land inside the game.

## Filename

`<id>-<version>.robotopiamod` — both tokens sanitized (any character outside `A-Za-z0-9_.-` becomes `_`).

## What `pack` includes

For a buildable mod (a `.csproj` in the project root), `robotopia pack` runs `dotnet build -c Release`
(override with `--configuration`) and stages:

| In the zip | Comes from |
|---|---|
| `robotopia.mod.json` (zip root) | the project manifest |
| `*.dll` + `*.pdb` (zip root) | the build output (first target-framework dir under `bin/<Configuration>/`) — **except** `Robotopia.Mods.Abstractions.*`, which the loader provides |
| `ref/`, `assets/`, `AssetBundles/`, `Resources/` | copied verbatim (recursive) from the project root, when present |
| `apiAssemblies` entries | resolved from the project root first, then the build output; a missing entry fails the pack |
| `bindings/<id>.gamebindings.json` | the repo-root `bindings/` dir, when present (first-party checkouts) — the game-compat manifest travels with the mod |

`pack` requires `name`, `displayName`, `version`, `entryAssembly`, and `entryType` in the manifest, and
fails if `entryAssembly` is missing from the build output.

**Manifest-only mods** (no `.csproj`): the whole project tree ships, minus `bin/`, `obj/`, `dist/`, and
`.robotopia/`.

## dist/ is generated output

- A single-project `robotopia pack` writes to `<project>/dist/` (override with `--output`).
- In a repo checkout, `robotopia pack --all` packs every first-party mod under `mods/` into `<repo>/dist/`,
  keeping exactly one current package per id (superseded versions are deleted). `DevTool`-category mods are
  skipped unless `--include-dev-mods` — the same rule release payloads use.
- Everything under `dist/` is generated — safe to delete at any time.

## sha256 travels outside the package

`pack` does **not** write the manifest's `hashes` field. Package integrity is tracked next to the download
location instead:

- registry entries pin a `packageSha256` per published version ([RegistryFormat.md](RegistryFormat.md));
- project dependency installs pin the hash in the lockfile;
- `robotopia check package <zip>` prints `sha256=<hex> (<size> MB)` for the exact bytes on disk.

Remote installs require the sha256 up front and verify the downloaded bytes; the launcher caps package
downloads at **512 MB**.

## Zip safety

Package entries must be relative, forward-slash paths. Archives containing absolute paths (`/…`, `C:/…`) or
`..` segments are rejected at install time (`Package contains an unsafe path`).

## Where packages land in the game

```text
<game>/BepInEx/RobotopiaModManager/
├── packages/<id>/<version>/    # installed packages, one directory per version
├── package-inbox/              # drop .robotopiamod files here — auto-installed at game launch
├── config/                     # per-mod config JSON
└── logs/manager.log            # game-side manager log
```

- `BepInEx/plugins/` holds **only the loader DLL** — mod files never go there.
- **Inbox flow:** any `.robotopiamod` dropped into `package-inbox/` is installed at the next game launch;
  the highest version per id wins and superseded versions are pruned.
- **Restart required:** Unity Mono cannot unload loaded assemblies, so enable, disable, update, and
  uninstall are staged and applied on the next restart (`robotopia restart` in a dev loop).

Ready to ship the package to players? See [PublishingYourMod.md](PublishingYourMod.md).
