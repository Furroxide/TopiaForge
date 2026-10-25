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

## Version-gated SDK additions

Starting with the 0.2 line, annotate every new public SDK type or member with
`[RequiresTopiaForge("0.2.0")]`, using its actual first release. The attribute lives in
`TopiaForge.Mods.Abstractions` and is available to every contract assembly. A marked type covers its
constructors and members; a member added later to that type needs its own later version. A member
added to an existing interface, including a property, must be marked individually. No existing 0.1
member needs retroactive annotation. The marker itself ships with the 0.1 tooling.

```csharp
public interface IExampleService
{
    [RequiresTopiaForge("0.2.0")]
    void NewOperation();
}
```

API baselines record the effective requirement, including accessor and containing-type requirements.
Both verification and refresh reject additions missing the marker, with guidance to add it. They
match APIs by type and member signature, so a new overload, or a new accessor on an existing property or
event, needs its own marker, while a changed line for an API that is already there (a deprecation,
a parameter rename or nullability fix, a new base interface) needs none. An API that is already in
the baseline must never gain a marker: mods that admit older loaders use it. An API the baseline
records as marked must keep its marker, because older loaders lack it. Replacing a parameter or
return type introduces a new signature even if another overload is removed. Generic parameter names,
parameter names, defaults, `params` and reference nullability do not change that identity; nested types retain
the generic arity of each declaring type. Accessor requirements belong to their exact property or event,
including the index parameter types. Review
the baseline diff before accepting it; the reviewed baseline is the inventory of pre-existing APIs.
Refresh with `dotnet run --project tests/TopiaForge.ModManager.Tests -c Release --
--update-sdk-api-baselines .`, then rebuild because baselines are embedded resources. Do not edit a
baseline by hand to bypass this check.

[TF1010](Diagnostics.md#tf1010) checks source uses against the manifest's minimum loader release.
Its IDE fix raises simple bounded loader and SDK ranges together; other ranges need an intentional
edit. [TFPKG180](Diagnostics.md#tfpkg180) applies the equivalent rule to manifest features at pack
and package-check time. These checks do not change `AssemblyVersion` from `0.1.0.0` or turn a minor
release into a compatibility promise.

An older loader refuses an incompatible mod during discovery and checks the loader range again
before loading code. The launcher names the mod and says it needs TopiaForge's minimum version or
newer, followed by "update TopiaForge", once, even when both the loader and SDK floors are above the
installed release; unequal floors report the stricter requirement. A join plan identifies the incompatible mod and blocks admission before enabling
it.

## Manifest and serialized state

Manifest V6 is the canonical authoring contract. Its optional `multiplayer` object does not change
standalone defaults. V4 and V5 are rejected with actionable V6 migration guidance. The CLI
migrates V3/V4/V5 through common preservation and refusal rules; it never infers gameplay
implementations. No policy requires retaining these unreleased schemas. Readers dispatch by schema
version before interpreting fields; unknown
fields fail except bounded namespaced `x-*` metadata. New semantics require an explicit numbered
contract and migration. See [Manifest V6](ManifestV6.md) for current fields and deliberate exclusions.

The approved pre-release redesign removes the Worlds startup API, including `GamemodeHost`, while
keeping `TopiaForge.Mods.Worlds` assembly identity at `0.1.0.0`. Gameplay starts through the declared
factory and a manager-owned session scope. Package format, dependency ordering, inbox, logs,
enablement and restart-required behavior remain part of the release contract.

The `TopiaForge.Mods.Multiplayer` public preview receives the same 0.x posture as other safe specialist
contracts. It does not promise live networking. Protocol versions are
independent from package versions, and standalone mods are not assumed multiplayer-correct.

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
evaluation while retaining the human-readable build label. The supported build is build 2545
(`0.0.2545`). When the installed Robotopia build is unknown, a constrained mod fails closed.

Compatibility is declared **per mod**, by whether the mod actually resolves symbols out of the game's
own implementation assembly:

- A mod with a `bindings/<id>.gamebindings.json` manifest pins the exact audited build, because it may
  claim only what its bindings were verified against. Exact pins fail closed rather than silently
  selecting another version.
- A mod that rides the SDK alone declares a bounded range (`>=0.0.2545 <0.0.2600`), so an ordinary game
  update does not brick it. Published builds do not step by a fixed amount (recent updates moved by as
  few as nine builds), so the ceiling is reviewed at every bump rather than derived, and it admits
  nothing beyond that review.

Two limits are worth stating plainly. There is no loader-level game-build gate: `supportedGameVersionRange`
is the only check, and a ranged mod that loads on an unverified build still runs inside a loader that
holds a compile-time reference to that assembly. The range therefore changes the failure *mode*, not the
underlying coupling. Separately, range evaluation has no npm-style prerelease exclusion, so `<0.0.2600`
also admits `0.0.2600-x`; that is pre-existing behaviour, documented rather than special-cased.
TopiaForge reads the launcher's `installed-build.json` marker from the game root first. In Tomato Cake's
Windows/Proton layout it also checks beside the launcher-owned `Robotopia` directory, matching the real
installation shape. When the official launcher has moved the game, the marker stays in its state
directory, `%LOCALAPPDATA%\Tomato Cake\launcher`. TopiaForge checks it there last, and only for the exact
`<game_dir>\Robotopia` folder named by that directory's `launcher-config.json`, so the marker is never
attributed to another copy of the game. That includes a copy left at the old default location: its parent
is the state directory, but after a move it does not read that marker as the one beside its `Robotopia`
folder. An existing malformed higher-priority marker never falls through to a lower-priority one; users
are directed to finish or repair the game installation instead.

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
