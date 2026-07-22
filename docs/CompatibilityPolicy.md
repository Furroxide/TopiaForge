---
title: Compatibility policy
description: V1 source, binary, manifest, runtime, and package compatibility guarantees for mods targeting Robotopia.
---

# Compatibility policy

TopiaForge runtime, SDK, CLI, launcher, schemas, and first-party packages use Semantic Versioning 2.
The safe V1 contract identity begins at `1.0.0`; the first public packages carry the
`1.0.0-rc.1` release-candidate suffix.

## Safe SDK packages

Core, UI facade, testing, and specialist module contract assemblies keep `AssemblyVersion`
`1.0.0.0` throughout the V1 line. NuGet package and file versions carry the release SemVer.
Compatibility baselines include public types, members, nullability, default values, and XML
documentation coverage.

Within V1:

- patch releases fix behavior without breaking public source or binary compatibility;
- minor releases add APIs and optional behavior without invalidating existing safe mods;
- obsolete APIs remain callable until the next major unless retaining one creates a critical risk;
- provider implementations may adapt to a Robotopia update behind unchanged safe contracts; and
- an unavailable adapter reports a reason instead of exposing a native fallback.

The explicitly unstable interop package is outside these guarantees.

## Manifest and serialized state

Schema V5 is the sole TopiaForge 1.0 package manifest. Its `multiplayer` object is optional; absence means
standalone-only. Manifest V4 was retired before the first public release and is rejected with an actionable V5
migration path. Readers dispatch by schema version and never reinterpret an older schema. Unknown fields fail
validation except bounded namespaced `x-*` metadata. Changing an existing field's meaning requires a new schema and
migration command. Future loaders may accept newer schemas alongside V5, but must keep a dedicated V5 reader and
its original semantics for the entire V1 compatibility line.

The `TopiaForge.Mods.Multiplayer` 1.0 public preview receives the same V1 source and binary compatibility guarantee as
other safe specialist contracts. It does not promise live networking in TopiaForge 1.0. Protocol versions are
independent from package versions, and standalone V5 mods are not assumed multiplayer-correct.

Manager, profile, receipt, journal, and last-run state are versioned, bounded, and written atomically.
Readers either migrate a known older state or fail with a recovery path. Package-supplied backup
files never replace authoritative manager state.

## Dependencies and resolution

Required dependency ranges participate in version solving and block load when unsatisfied. Optional
dependencies integrate only when present and valid. Conflicts block a plan. Load-order hints do not
create dependencies.

Resolution is deterministic across filesystem enumeration order: ids are normalized, versions use
full SemVer precedence, paths break any remaining tie, and the authoritative selection/load order is
recorded. Exact pins fail closed rather than silently selecting another version.

Extension providers are visible only through declared dependencies and exported API assemblies.
Singleton contracts reject duplicate providers; multi-provider contracts have deterministic order.

## Lifecycle compatibility

All SDK callbacks and engine-facing calls run on Robotopia's Unity main thread. The runtime owns a mod
lifetime before invoking load and releases resources in reverse order after unload or partial-load
failure. Cleanup is idempotent. A provider must isolate subscriber exceptions so one mod cannot
prevent another subscriber from receiving an event.

Scene lifecycle delivery is normalized per scene instance: `Loaded` precedes `Activated`, an unload is
published only for a previously known or valid native scene, startup replay/native echo pairs are deduplicated,
and process-local instance ids correlate equal additive scene names without becoming persistent identifiers.
Startup replay includes lifecycle-only `Loaded` events for already-loaded background additive scenes before the
active scene's normalized `Loaded`/`Activated` pair. Legacy scene-loaded subscriptions retain their existing
active-scene-only startup behavior and one-callback-per-load behavior afterward.

## Robotopia and platform compatibility

Robotopia uses numeric build identifiers. TopiaForge maps build 2227 to SemVer `0.0.2227` for range
evaluation while retaining the human-readable build label. A mod may claim only ranges exercised by
its acceptance tests. When the installed Robotopia build is unknown, a constrained mod fails closed.

Platform and architecture claims are made per release artifact and require their native CI jobs.
Custom-world live acceptance is Windows/Proton-only for V1. Bundle content must declare an
appropriate content target; a build for one target is not assumed portable to another.
Under Proton/Wine, TopiaForge treats Robotopia as the Windows player target. Empty constraint lists are portable;
otherwise the loader normalizes the host platform/architecture and requires at least one declared
content target to match `code` or the active Unity player target. Valid installed versions coexist;
exact profile pins fail closed without deleting alternatives, while ordinary selection chooses the
highest compatible SemVer and records the decision in `last-run.json`.

## Packages, updates, and trust

Published package bytes are immutable for a version, fetched over HTTPS, pinned by SHA-256, validated
without execution, and installed atomically with an integrity receipt. Modified installed bytes are
repaired or blocked, never executed.

Capabilities are disclosures, not grants. Mods execute with the Robotopia process's authority. Registry
listing is not a security endorsement; users still choose which authors and sources to trust.
