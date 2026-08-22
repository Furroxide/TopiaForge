---
title: Compatibility policy
description: 0.x source, binary, manifest, runtime, and package compatibility posture for mods targeting Robotopia.
---

# Compatibility policy

TopiaForge runtime, SDK, CLI, launcher, schemas, and first-party packages use Semantic Versioning 2 and
are currently on the **0.x** line, starting at `0.1.0-rc.1`.

**A 0.x line makes no cross-minor compatibility promise.** Under SemVer the breaking axis below `1.0.0`
is MINOR, so anything in this document that holds "within a release" holds within a 0.x **patch**, and a
minor bump may break source, binary, or manifest compatibility. Stable contract identity is reserved for
a future `1.0.0`.

## Safe SDK packages

Core, UI facade, testing, and specialist module contract assemblies keep `AssemblyVersion` `0.1.0.0`
frozen for the whole 0.x line. That is a deliberate deviation from strict SemVer: Mono and BepInEx have
no binding-redirect infrastructure, so changing a contract assembly's identity is a hard load failure for
every already-compiled third-party mod. Tracking the minor would be "correct" and operationally useless.
NuGet package and file versions carry the real release SemVer. Compatibility baselines include public
types, members, nullability, default values, and XML documentation coverage.

Within a 0.x patch:

- patch releases fix behavior without breaking public source or binary compatibility;
- provider implementations may adapt to a Robotopia update behind unchanged safe contracts; and
- an unavailable adapter reports a reason instead of exposing a native fallback.

Across a 0.x **minor** bump, APIs may be removed or changed. Removals are listed in the package
CHANGELOG; there is no deprecate-until-next-major guarantee before `1.0.0`.

The explicitly unstable interop package is outside even these guarantees.

## Manifest and serialized state

Schema V5 is the sole TopiaForge package manifest. Its `multiplayer` object is optional; absence means
standalone-only. Manifest V4 was retired before the first public release and is rejected with an actionable V5
migration path. Readers dispatch by schema version and never reinterpret an older schema. Unknown fields fail
validation except bounded namespaced `x-*` metadata. Changing an existing field's meaning requires a new schema and
migration command. Future loaders may accept newer schemas alongside V5, but must keep a dedicated V5 reader and
its original semantics for the entire 0.x line and into 1.0.

The `TopiaForge.Mods.Multiplayer` public preview receives the same 0.x posture as other safe specialist
contracts. It does not promise live networking. Protocol versions are
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

Robotopia uses numeric build identifiers. TopiaForge maps build `N` to SemVer `0.0.N` for range
evaluation while retaining the human-readable build label. The supported build is build 2409
(`0.0.2409`). When the installed Robotopia build is unknown, a constrained mod fails closed.

Compatibility is declared **per mod**, by whether the mod actually resolves GameCode symbols:

- A mod with a `bindings/<id>.gamebindings.json` manifest pins the exact audited build, because it may
  claim only what its bindings were verified against. Exact pins fail closed rather than silently
  selecting another version.
- A mod that rides the SDK alone declares a bounded range (`>=0.0.2409 <0.0.2600`), so an ordinary game
  update does not brick it. Published builds step by roughly +100 (2309 -> 2409), so that bound admits
  the current build and the next one, and nothing beyond a review.

Two limits are worth stating plainly. There is no loader-level game-build gate: `supportedGameVersionRange`
is the only check, and a ranged mod that loads on an unverified build still runs inside a loader that
compile-time references `GameCode.dll`. The range therefore changes the failure *mode*, not the underlying
coupling. Separately, range evaluation has no npm-style prerelease exclusion, so `<0.0.2600` also admits
`0.0.2600-x`; that is pre-existing behaviour, documented rather than special-cased.
TopiaForge reads the launcher's `installed-build.json` marker from the game root first. In Tomato Cake's
Windows/Proton layout it also checks beside the launcher-owned `Robotopia` directory, matching the real
installation shape. An existing malformed higher-priority marker never falls through to a lower-priority
one; users are directed to finish or repair the game installation instead.

Platform and architecture claims are made per release artifact and require their native CI jobs.
Custom-world live acceptance is Windows/Proton-only for 0.x. Bundle content must declare an
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
