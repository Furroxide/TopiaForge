---
title: Manifest V4
description: Canonical reference for topiaforge.mod.json schema version 4.
---

# Manifest V4

Every code mod has one `topiaforge.mod.json` at the package root. The JSON Schema at
`schemas/topiaforge.mod.schema.json` is canonical and is used by the C# runtime, Dart tooling, CLI,
editor completion, and shared golden/adversarial fixtures.

Schema V4 is strict. Unknown fields are rejected unless their name begins with `x-`. Collections,
strings, paths, and dependency graphs are bounded before any assembly is loaded.

## Minimal publishable manifest

```json
{
  "$schema": "https://raw.githubusercontent.com/furroxide/TopiaForge/main/schemas/topiaforge.mod.schema.json",
  "schemaVersion": 4,
  "name": "example.first-mod",
  "displayName": "First Mod",
  "version": "1.0.0",
  "author": { "name": "You" },
  "description": "A small Robotopia utility mod.",
  "entryAssembly": "ExampleFirstMod.dll",
  "entryType": "Example.FirstMod.FirstMod",
  "supportedGameVersionRange": "0.0.2227",
  "supportedLoaderVersionRange": ">=1.0.0 <2.0.0",
  "supportedSdkVersionRange": ">=1.0.0 <2.0.0",
  "category": "Utility",
  "license": "MIT"
}
```

Robotopia numeric build 2227 is bridged to SemVer as `0.0.2227`. New scaffolds write the current
supported build automatically; do not broaden it until the mod has passed a live smoke test on the
additional build.

## Required fields

| Field | Contract |
| --- | --- |
| `schemaVersion` | Integer `4`. |
| `name` | Stable 2–64 character package id using letters, digits, dot, dash, or underscore. |
| `displayName` | Player-facing name, 1–128 characters. |
| `version` | Full SemVer 2 package version, including prerelease/build metadata when needed. |
| `author` | Object with required `name` and optional `email`/`url`. |
| `entryAssembly` | Portable package-relative DLL path. |
| `entryType` | Fully qualified public type deriving from `TopiaForgeMod` with a public parameterless constructor. |
| `supportedGameVersionRange` | Robotopia compatibility range. |
| `supportedLoaderVersionRange` | Loader compatibility range. |
| `supportedSdkVersionRange` | Safe SDK compatibility range. |

## Dependencies and ordering

`dependencies` and `optionalDependencies` are canonical ID-to-range maps:

```json
{
  "dependencies": {
    "io.github.furroxide.topiaforge.worlds": ">=1.0.0 <2.0.0"
  },
  "optionalDependencies": {
    "example.integration": "^1.2.0"
  },
  "loadAfter": ["example.integration"],
  "loadBefore": ["example.presentation"]
}
```

Required dependencies block resolution when absent or incompatible. Optional dependencies affect
integration and order only when a valid provider is installed. `loadAfter` and `loadBefore` are
soft ordering hints, never implicit dependencies. Cycles, duplicate normalized ids, and unbounded
graphs are rejected.

Supported range forms are exact (`1.2.3`), wildcard (`1.2.x`), caret (`^1.2.3`), tilde (`~1.2.3`),
and comparator sets (`>=1.2.0 <2.0.0`). Prerelease selection follows SemVer precedence and must be
requested explicitly.

## Runtime and content constraints

| Field | Values |
| --- | --- |
| `platforms` | Unique subset of `windows`, `macos`, `linux`. |
| `architectures` | Unique subset of `x64`, `arm64`. |
| `contentTargets` | Stable lowercase content target ids used by bundle/world adapters. |
| `conflicts` | Bounded array of `{ id, versionRange?, reason? }`. |
| `worldGamemodes` | Bounded launcher display metadata for registered modes. |

An absent platform/architecture list means no additional package constraint. It does not claim that
an untested platform is supported; publishing policy still requires evidence for every advertised
artifact.

All three constraint lists are evaluated by the Robotopia-side loader before selection and again before
assembly activation. Empty lists are portable. Host labels are normalized (`amd64` to `x64`, for
example). Proton/Wine runs Robotopia's Windows player and therefore matches `windows`, not `linux`.
The V1 runtime recognizes `code` for managed-only packages plus the active Unity player target:
`standalonewindows64`, `standaloneosx`, or `standalonelinux64`. A constrained exact profile pin fails
closed when it does not match; an unpinned profile selects the highest compatible SemVer.

## Capabilities

`capabilities` is a unique array of known disclosure labels:

`asset-bundles`, `filesystem`, `filesystem-watch`, `harmony-patch`, `hud`, `input`, `navigation`,
`network`, `microphone`, `particles`, `physics`, `physics-settings`, `player-control`,
`player-token`, `prompt-overrides`, `quality-settings`, `remote-ai`, `render-settings`,
`robot-spawning`, `scene-management`, `speech-to-text`, `time`, `ugc-livesync`, `unsafe-native`,
and `world-service`.

Unknown values fail schema validation. Capabilities disclose behavior and drive diagnostics; mods
remain trusted full-process code. See [Privacy and capability disclosure](PrivacyAndCapabilities.md).

## Exported dependency surface

`apiAssemblies` is the only assembly surface another package may compile against. List only stable,
engine-independent contract DLLs. Implementation assemblies remain private even when they happen to
be present in the package.

The loader resolves exports only for declared dependencies. The package validator rejects bundled
framework contract assemblies and validates each exported path as a portable DLL path.

## Package metadata

`description`, `category`, `tags`, `icon`, `screenshots`, `homepage`, `source`, `license`, and
`licenseFiles` describe the package. Paths are package-relative, use forward slashes, and reject
traversal, absolute roots, device names, control characters, alternate separators, and ambiguous
case collisions.

`builtWith` records exact SDK, loader, game, and tool versions. Packing generates it from the active
toolchain; authors should not hand-maintain it. Installation receipts separately store archive and
critical-file digests.

`hashes` may contain file SHA-256 values when produced by trusted packaging tooling. Registry and
lock metadata still pin the whole archive SHA-256.

## Extension metadata

Namespaced extension keys match `x-*`, for example `x-example-build-channel`. Core validators retain
and bound them but do not assign semantics. Any other unknown field is rejected.

## Migrate a schema V3 project

```sh
topiaforge migrate-manifest --project .
topiaforge restore
topiaforge check package .
```

Migration converts legacy dependency forms and disclosure labels, writes canonical V4, and reports
fields that require a human decision. The V1 runtime does not contain a schema V3 compatibility shim.
