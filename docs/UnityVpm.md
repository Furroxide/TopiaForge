# QuantumWorks Unity packages (VPM)

QuantumWorks ships a **VPM-compatible** (VRChat Package Manager) layer for the Unity authoring side: Unity
packages (`com.robotopia.*`), a per-project manifest, a repository listing, and a resolver that installs packages
into a project's `Packages/`. The formats mirror VRChat's so creators coming from VCC feel at home.

## Package format (`package.json`)

A VPM package is a UPM package plus a few VPM fields:

```json
{
  "name": "com.robotopia.ugc-companion",
  "version": "0.1.0",
  "displayName": "Robotopia UGC Companion",
  "unity": "2022.3",
  "vpmDependencies": { "com.robotopia.vpm-resolver": ">=0.1.0" },
  "legacyFolders": {},
  "samples": []
}
```

- `vpmDependencies` — VPM (not UPM) dependencies, as version ranges (`>=`, `^`, `~`, `1.2.*`, exact, `*`).
- `legacyFolders` — pre-VPM folders to remove on install (migration). Empty for new packages.
- `samples` — importable sample folders under `Samples~`.

## Project manifest (`Packages/vpm-manifest.json`)

```json
{
  "dependencies": { "com.robotopia.ugc-companion": "^0.1.0" },
  "locked": {
    "com.robotopia.ugc-companion": {
      "version": "0.1.0",
      "dependencies": { "com.robotopia.vpm-resolver": ">=0.1.0" }
    }
  }
}
```

- `dependencies` — what the project declares (version ranges).
- `locked` — what the resolver pinned (exact versions + their resolved deps) for reproducible restores.

## Repository listing (`index.json`)

A listing serves packages keyed by id → version → the package.json plus `url` + `zipSHA256`:

```json
{
  "name": "QuantumWorks Local",
  "id": "com.robotopia.repos.local",
  "packages": {
    "com.robotopia.ugc-companion": {
      "versions": {
        "0.1.0": { "name": "com.robotopia.ugc-companion", "version": "0.1.0",
                   "url": "…/com.robotopia.ugc-companion-0.1.0.zip", "zipSHA256": "…" }
      }
    }
  }
}
```

The built-in local listing is `dist/vpm/index.json`, **derived from the packed zips** — the same drift-proof
pattern the `.robotopiamod` catalog uses. Regenerate it with `robotopia unity pack-packages` (run automatically by
`robotopia release build-package`): it zips every shipped `com.robotopia.*` package (companion + resolver),
computes the SHA-256, and rewrites `index.json`. There is no hand-maintained registry.

Community authors package one or more scaffolded VPM packages explicitly; the no-flag command above remains the
first-party repository build:

```text
robotopia unity pack-packages --package . --output dist/vpm --repo-id com.you.repo --repo-name "Your Repository" --author "Your Name"
```

Repeat `--package <dir>` for every package that belongs in the same listing. Explicit packaging validates package
ids, semantic versions, dependency ranges, regular-file roots, archive paths, case collisions, and size limits;
it rejects duplicate ids and symlinks, emits reproducible zips, and records each zip SHA-256 in `index.json`.
Upload the entire output directory to one stable HTTPS location so the relative package URLs remain valid, then
subscribe to that hosted `index.json` and resolve into a clean Unity project before publishing the URL.

## The resolver

Two resolvers, both reading the same listings:

1. **Launcher-driven** (Dart `UnityVpmResolver` + `LocalDeveloperRepository`) — reads `vpm-manifest.json`,
   resolves declared + transitive `vpmDependencies` against the subscribed listings (highest satisfying version,
   dependency-first order), downloads + SHA-verifies + extracts the zips into `Packages/<id>`, and writes `locked`
   back. Drives the **Packages** pane and `robotopia unity` CLI.
2. **Embedded recovery bridge** (`com.robotopia.vpm-resolver`, an Editor-only package committed in projects) —
   on project open it performs bounded, read-only checks of `vpm-manifest.json` and installed `package.json`
   files. If an exact locked package is missing, invalid, or mismatched, it offers to copy the explicit
   `robotopia unity resolve <project>` command. It never reads package listings, chooses a fallback version,
   downloads or extracts an archive, launches a process, or changes `Packages/`; those security-sensitive
   operations remain in the launcher/CLI boundary described above.

After a fresh clone, run `robotopia unity resolve .` from the project directory (or use **Developer → Packages →
Resolve All** in the launcher), then review the resulting `Packages/vpm-manifest.json` diff. Resolution selects
the highest versions satisfying all declared ranges and records exact versions in `locked`; it does not silently
substitute a version inside Unity editor startup. Malformed, oversized, non-regular, or symlinked VPM manifests
fail closed and remain untouched.

Version ranges follow semver: `^1.2.3` → `>=1.2.3 <2.0.0` (and `^0.1.0` → `>=0.1.0 <0.2.0`, caret locks the minor
when the major is 0); `~1.2.3` → `>=1.2.3 <1.3.0`; plus `1.2.*`, exact, comparators, and `*`.

## Source control

Commit `Assets/`, `ProjectSettings/`, `Packages/manifest.json`, `Packages/vpm-manifest.json`, and the embedded
`Packages/com.robotopia.vpm-resolver/`. Don't commit the other `Packages/com.robotopia.*` packages — restore them
explicitly through the launcher/CLI after cloning. The world template ships a `Packages/.gitignore` that does
exactly this.

## CLI

```
robotopia unity new-package <id> [--name Name] [--dir path]   # scaffold a VPM package (package-maker)
robotopia unity pack-packages --package <dir> --output <dir>  # package a community listing (also pass repo metadata)
robotopia unity resolve [path] [--no-restore]                 # resolve + restore a project's packages
robotopia unity add <id[@range]> [path]                       # add a dependency, then resolve
robotopia unity remove <id> [path]                            # remove a dependency
robotopia unity list                                          # packages available across listings
robotopia unity repos | add-repo <url> | remove-repo <id>     # manage subscribed listings
robotopia unity new-repo                                      # guidance to (re)build dist/vpm/index.json
```
