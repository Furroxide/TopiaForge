---
title: Mod package format
description: Understand .topiaforgemod layout, validation, receipts, and installation.
---

# Mod package format

A `.topiaforgemod` is a bounded zip archive with one schema-V4 `topiaforge.mod.json` at its root.
Its canonical filename is `<normalized-id>-<semver>.topiaforgemod`; the manifest identity and version
must match the containing install directory.

## Pack a project

```sh
topiaforge pack
topiaforge check package dist/example.first-mod-1.0.0.topiaforgemod
```

For a code mod, `pack` builds Release configuration and stages:

| Package entry | Source |
| --- | --- |
| `topiaforge.mod.json` | Validated manifest plus generated `builtWith` metadata. |
| Entry DLL and symbols | Selected target output. |
| `apiAssemblies` | Explicit public dependency contracts only. |
| `assets/`, `AssetBundles/`, `Resources/` | Declared project content when present. |
| license/provenance files | Manifest-declared package metadata. |

Reference-only TopiaForge SDK assemblies are supplied by the loader and must not be bundled. The
analyzer and package validator reject copied framework assemblies.

## Validation never executes mod code

Before installation or load, TopiaForge checks:

- bounded, portable archive paths with no traversal, roots, device names, links, or case collisions;
- schema V4, known capabilities, required compatibility ranges, and bounded dependency graphs;
- canonical package id, SemVer, directory layout, and supported platform/content constraints;
- managed PE validity and declared assembly identity;
- a public parameterless `entryType` deriving from `TopiaForgeMod`;
- SDK compatibility and absence of bundled framework contracts; and
- every exported `apiAssemblies` path.

A bad optional provider is reported and skipped without blocking consumers that do not require it.
Valid installed versions coexist so profiles can switch without reinstalling. An exact profile pin
fails closed when its package is absent and never deletes another installed version. An unpinned
profile selects the highest compatible SemVer deterministically and records that recovery decision.

## Integrity receipt

Installation writes `topiaforge.install.json` beside the unpacked payload. The receipt records:

- normalized mod identity and version;
- source archive filename, SHA-256, and sanitized provenance (`local`, `inbox`, `cache`,
  registry ID, or remote host) without credentials, query strings, or filesystem paths;
- install timestamp, validator version, and trust result; and
- a bounded inventory containing path, byte length, SHA-256, and critical-file classification.

The manifest, entry assembly, and every exported API assembly are critical. Before load, TopiaForge
compares the receipt with installed bytes. A missing, added, changed, or linked file blocks execution
and offers reinstall/repair. A receipt proves integrity relative to the installed archive; it is not
a signature or security sandbox.

## Archive integrity in registries

Registry records and project locks pin the SHA-256 of the exact archive. Published versions are
immutable: changed bytes require a new SemVer. Remote downloads are size-bounded, use HTTPS, verify
the declared hash before extraction, and install atomically.

## Install layout

```text
<Robotopia>/BepInEx/TopiaForge/
├── packages/<id>/<version>/
│   ├── topiaforge.mod.json
│   ├── topiaforge.install.json
│   └── <package payload>
├── package-inbox/
├── config/
├── state/
└── logs/
```

The package inbox safely preflights every bounded, regular archive without executing it. Candidates
are grouped by normalized ID, then selected deterministically by highest compatible SemVer and
normalized path. A package that fails archive, manifest, runtime-constraint, managed-metadata, or
atomic-install validation is reported with a stable `TFINBOX` diagnostic and retained for inspection
or retry. Successful winners and their valid superseded candidates are consumed only after their
preflight SHA-256 is reverified; a file changed during processing is retained. Inbox dependency
groups are retried deterministically so a provider in the same batch can unblock its consumer.

Installing a new version preserves other valid installed versions; replacing the same ID and version
remains atomic, and explicit uninstall removes the package ID. Loader-owned framework assemblies are
placed in the host plugin directory; mod payloads remain under versioned package roots.

Enable, disable, update, and uninstall operations may require a Robotopia restart because loaded managed
assemblies cannot be replaced safely in-process.

Ready to distribute immutable bytes? Continue with [Publish a mod](PublishingYourMod.md).
