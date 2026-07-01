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
pattern the `.robotopiamod` catalog uses. Regenerate it with `tools/pack-unity-packages.ps1` (run automatically by
`tools/package-distribution.ps1`): it zips every shipped `com.robotopia.*` package (companion + resolver),
computes the SHA-256, and rewrites `index.json`. There is no hand-maintained registry.

## The resolver

Two resolvers, both reading the same listings:

1. **Launcher-driven** (Dart `UnityVpmResolver` + `LocalDeveloperRepository`) — reads `vpm-manifest.json`,
   resolves declared + transitive `vpmDependencies` against the subscribed listings (highest satisfying version,
   dependency-first order), downloads + SHA-verifies + extracts the zips into `Packages/<id>`, and writes `locked`
   back. Drives the **Packages** pane and `robotopia unity` CLI.
2. **Embedded** (`com.robotopia.vpm-resolver`, an Editor-only package committed in projects) — on project open it
   diffs `locked` vs the installed `Packages/`, and restores anything missing (e.g. after a fresh `git clone`)
   from the listings in `Packages/vpm-resolver-repos.json`. The git-clone self-heal; the VRChat
   `com.vrchat.core.vpm-resolver` analog.

Version ranges follow semver: `^1.2.3` → `>=1.2.3 <2.0.0` (and `^0.1.0` → `>=0.1.0 <0.2.0`, caret locks the minor
when the major is 0); `~1.2.3` → `>=1.2.3 <1.3.0`; plus `1.2.*`, exact, comparators, and `*`.

## Source control

Commit `Assets/`, `ProjectSettings/`, `Packages/manifest.json`, `Packages/vpm-manifest.json`, and the embedded
`Packages/com.robotopia.vpm-resolver/`. Don't commit the other `Packages/com.robotopia.*` packages — they restore
from the manifest. The world template ships a `Packages/.gitignore` that does exactly this.

## CLI

```
robotopia unity new-package <id> [--name Name] [--dir path]   # scaffold a VPM package (package-maker)
robotopia unity resolve [path] [--no-restore]                 # resolve + restore a project's packages
robotopia unity add <id[@range]> [path]                       # add a dependency, then resolve
robotopia unity remove <id> [path]                            # remove a dependency
robotopia unity list                                          # packages available across listings
robotopia unity repos | add-repo <url> | remove-repo <id>     # manage subscribed listings
robotopia unity new-repo                                      # guidance to (re)build dist/vpm/index.json
```
