---
title: Manifest V6
description: Canonical TopiaForge package manifest, including optional multiplayer metadata.
---

# Manifest V6

Manifest V6 is the current authoring contract and the canonical schema alias. It is strict: unknown fields are
rejected unless their name begins with `x-`, and collections, strings, paths, and dependency graphs are
bounded before an assembly loads. The `multiplayer` object is optional. Omitting it is the canonical
standalone-only declaration.

Manifest V4 and V5 were retired before the first public release. Tooling rejects them with a migration command
instead of carrying a second permanent contract.

The editor-friendly `topiaforge.mod.schema.json` URL names the latest supported schema. The immutable
`topiaforge.mod.v6.schema.json` file is a self-contained V6 contract and never references that moving alias.
The V4 and V5 versioned schemas are rejecting stubs. Use the migration command below
for existing V3/V4/V5 source projects; no retired reader can activate a package.

## Minimal standalone manifest

```json
{
  "$schema": "https://raw.githubusercontent.com/furroxide/TopiaForge/main/schemas/topiaforge.mod.schema.json",
  "schemaVersion": 6,
  "name": "example.first-mod",
  "displayName": "First Mod",
  "version": "1.0.0",
  "author": { "name": "You" },
  "entryAssembly": "ExampleFirstMod.dll",
  "entryType": "Example.FirstMod.FirstMod",
  "supportedGameVersionRange": "0.0.2409",
  "supportedLoaderVersionRange": ">=0.1.0-rc.1 <0.2.0",
  "supportedSdkVersionRange": ">=0.1.0-rc.1 <0.2.0",
  "license": "AGPL-3.0-or-later"
}
```

Required fields are `schemaVersion`, `name`, `displayName`, `version`, `author`, `entryAssembly`,
`entryType`, and the three `supported*VersionRange` fields. `name` is a stable 2–64 character package
id. Versions use SemVer 2; Robotopia build 2409 is represented as `0.0.2409`.

## Package contract

### Common fields

The root object has at most 64 properties. Required fields are marked **required**.
All string limits count Unicode code points. JSON types and property presence are
checked before defaults: an explicit null does not stand for an absent optional
field. Nested objects are closed except the arbitrary JSON values of root `x-*`
extensions. Refer to the versioned schema for the exact ASCII regular expressions.

| Field | Type, limits and meaning |
| --- | --- |
| `$schema` | Optional editor URL string, at most 512 characters. It does not select the reader. |
| `schemaVersion` | **Required** integer `6`; selectors are checked before any other field. |
| `name` | **Required** package ID, 2–64 ASCII letters, digits, `_`, `.`, `-`; first character alphanumeric. Retired ecosystem namespaces are rejected. |
| `displayName` | **Required** nonblank display name, 1–128 characters. |
| `version` | **Required** full SemVer 2 string, including optional prerelease/build identifiers. |
| `author` | **Required** object with nonblank `name` (1–128), optional `email` (up to 254) and `url` (up to 2,048). A bare string is rejected. |
| `description` | Optional string, up to 4,096 characters. |
| `entryAssembly` | **Required** portable relative `.dll` package path. |
| `entryType` | **Required** nonblank CLR entry type string, up to 512 characters. Runtime verifies the declared entry point. |
| `dependencies`, `optionalDependencies` | Optional ID-to-version-range objects, at most 128 entries each. An ID cannot occur in both collections or collide by case. |
| `conflicts` | Optional list of up to 128 objects: required `id`, optional `versionRange` and `reason` (up to 512 characters). Omitted range means any version. |
| `loadAfter`, `loadBefore` | Optional unique lists of up to 128 package IDs. Soft order hints do not create dependencies; self-ordering is rejected. |
| `supportedGameVersionRange`, `supportedLoaderVersionRange`, `supportedSdkVersionRange` | **Required** range strings, 1–256 characters each. Missing values are never publishable defaults. |
| `category` | Optional string, up to 64 characters. |
| `tags` | Optional unique list of up to 64 strings, each 1–64 characters. |
| `icon`, `screenshots` | Optional portable relative image path and unique list of up to 32 such paths. |
| `homepage`, `source` | Optional URL strings, up to 2,048 characters each. |
| `license` | Optional nonempty SPDX expression string, up to 256 characters. Public registry publication requires a concrete license. |
| `licenseFiles` | Optional unique list of 1–32 portable relative notice/license paths; an empty list is invalid. |
| `hashes` | Pack-generated map of at most 8,192 portable relative paths to 64-hex-character SHA-256 digests. |
| `capabilities` | Optional unique list of up to 64 recognized disclosure values; see below. |
| `platforms` | Optional unique subset of `windows`, `macos`, `linux` (up to 3). |
| `architectures` | Optional unique subset of `x64`, `arm64` (up to 2). |
| `contentTargets` | Optional unique list of up to 64 target tokens, each 1–64 lowercase ASCII letters, digits, `_`, `.`, `-`, starting alphanumeric. |
| `builtWith` | Optional nonempty object with SemVer values for any of `sdkVersion`, `loaderVersion`, `gameVersion`, `toolVersion`; written by tooling. |
| `apiAssemblies` | Optional unique list of up to 64 portable relative `.dll` paths exported to declared dependants. |
| `multiplayer` | Optional admission metadata described below; absence means standalone-only. |
| `contributions` | Optional nonempty world/gamemode/launch-target declarations described below. Requires `world-service`. |
| `x-*` | At most 32 extension properties; names match `x-[A-Za-z0-9_.-]{1,64}`; values retain their JSON types and receive no core semantics. Overall input size/depth/property bounds still apply. |

All package paths are 1–1,024 characters, relative and portable: no rooted or drive
paths, traversal, empty segments, control characters, Windows device names, streams,
or trailing spaces/dots. Path collections reject portable collisions. Packing owns
`builtWith` and `hashes`; authors should not hand-maintain generated hashes.

Required dependency ranges block resolution when unsatisfied. Optional dependencies
participate when a compatible provider is installed. Supported ranges include exact,
wildcard and comparator sets. The V3 migrator translates legacy caret/tilde ranges
into explicit comparators; they are not accepted by the V6 readers. Versions retain full SemVer precedence;
Robotopia build 2409 maps to `0.0.2409`. Host constraints restrict package loading;
an empty constraint list is portable, not evidence of testing on every host.

Capabilities disclose access and do not grant or sandbox authority. Recognized values
are `asset-bundles`, `filesystem`, `filesystem-watch`, `harmony-patch`, `hud`, `input`,
`navigation`, `network`, `microphone`, `particles`, `physics`, `physics-settings`,
`player-control`, `player-token`, `prompt-overrides`, `quality-settings`, `remote-ai`,
`render-settings`, `robot-spawning`, `scene-management`, `speech-to-text`, `time`,
`ugc-livesync`, `unsafe-native` and `world-service`. A recognized disclosure does not
promise that a provider is available. `network` means arbitrary outbound networking;
it is separate from the TopiaForge multiplayer transport.

## Add multiplayer support

Add the preview contract through the CLI rather than editing project and package metadata independently:

```sh
topiaforge mod add multiplayer
topiaforge restore
topiaforge mod sync multiplayer
```

New scaffolds and module edits use V6. The command pins the `TopiaForge.Mods.Multiplayer` contract,
`TopiaForge.Mods.Multiplayer.Generators`, and runtime provider to the same exact release, scaffolds protocol
metadata, and creates the checked-in `topiaforge.multiplayer.lock.json`. Removing the module reverses those changes
only after an explicit command and preserves unrelated contribution declarations.

## Session manifest

```json
{
  "schemaVersion": 6,
  "multiplayer": {
    "mode": "session",
    "presence": "required",
    "protocol": {
      "version": "1.0.0",
      "peerVersionRange": ">=1.0.0 <2.0.0"
    },
    "synchronizedFiles": [
      "Content/gameplay-rules.json"
    ]
  }
}
```

Adding multiplayer changes neither the meaning of ordinary fields nor the package dependency model. The `network`
capability still discloses arbitrary outbound network access; TopiaForge multiplayer transport is supplied by the
declared specialist provider.

## Modes

| Mode | Contract |
| --- | --- |
| `client-local` | Runs on an interactive client, is optional per player, performs no shared-world mutation, and does not participate in the session handshake. `presence`, `protocol`, and `synchronizedFiles` are not allowed. |
| `server-only` | Runs only on the logical server, including listen and dedicated hosts. Clients do not install it. `presence`, `protocol`, and `synchronizedFiles` are not allowed. |
| `session` + `required` | A mutually compatible copy is required on the server and every client. |
| `session` + `optional` | Activates only between peers that both have mutually compatible copies. |

For `session`, `presence` and `protocol.version` are required. `peerVersionRange` is optional; when omitted, peers
must advertise exactly the same protocol version. Protocol versions are independent from package versions.
Compatibility is mutual: each peer's protocol version must satisfy the other peer's effective range.

## Synchronized content

`synchronizedFiles` is a bounded list of portable, package-relative paths whose bytes affect shared simulation.
For session mods, packing automatically appends `topiaforge.multiplayer.lock.json` and its generated hash to the
packed manifest, so contract/schema changes participate in admission without author-maintained identifiers or hashes.
The source manifest is left unchanged, and non-session modes do not synchronize the lock. The 256-entry packed limit
reserves one entry for this lock, so a session source manifest may list at most 255 other synchronized files.
`topiaforge pack` hashes every listed file and writes its SHA-256 to the packed manifest. Source manifests do not
contain hand-maintained hashes. Packing rejects a missing file; installation and runtime scanning reject a missing
or changed synchronized file before code loads.

Package versions and whole-archive hashes remain diagnostics and local-integrity evidence. They are not default wire
compatibility. A curated server may later require the opt-in `exact-profile` policy.

## Admission contract

Admission completes before a world is loaded or a network-session callback runs. The default policy compares the
Robotopia build, the TopiaForge multiplayer protocol, required mod presence, mutual per-mod protocol ranges, and
exact synchronized-file hashes. V6 mods without multiplayer metadata block multiplayer with a per-mod explanation;
they remain fully supported in standalone play. Optional session mods that do not negotiate remain non-fatal, while
the admission report retains structured inactive reasons for launcher diagnostics. Retired V4 and V5 packages fail manifest
validation before admission.

TopiaForge never silently disables a mod and never downloads and executes a server package. A launcher may offer a
derived profile with incompatible mods disabled or a trusted-registry install plan, but either action requires
explicit approval.

## Worlds, gamemodes and launch targets

`contributions` requires the `world-service` capability. It is a nonempty object with optional
`worlds` (1–64), `gamemodes` (1–16) and `launchTargets` (1–64) arrays. Each present array is nonempty.
Declaration IDs are unique across all three collections, ASCII, 4–96 characters, and begin with
`<package name>.`. Discovered family IDs are at most 94 characters to leave room for an instance suffix.
Package IDs retain the 64-character limit. Ownership uses the longest matching package prefix;
a longer disabled or missing owner never falls back to a shorter package.

Names/titles are 1–128 Unicode characters and descriptions at most 1,024. Lengths count Unicode
characters rather than UTF-16 code units. Scalars retain their JSON types: null, numeric strings,
fractional integer fields and empty prohibited fields are rejected. An omitted optional value is
not interchangeable with an explicitly supplied null. Unknown nested fields are rejected.

### Implementation binding

`implementation` contains required `type` and optional `assembly` (defaults to `entryAssembly`).
Types use an ASCII namespace-qualified CLR name, with no nested-type or assembly-qualified syntax.
An explicit assembly is a portable package DLL path and must appear in packed `hashes`. Runtime
activation verifies the owning package's installed assembly receipt and actual defining assembly.
The type must be public, concrete and parameterless, implementing the required factory/provider
contract. Constructors and startup callbacks run only after the child context is created.

### World declaration

Required fields are `id`, `name`, `content`, `transitions` and `spawn`; optional fields are
`description`, `openTo` and `openToAnyCompatible`.

| `content.kind` | Required content fields | Runtime behavior |
| --- | --- | --- |
| `bundle` | `bundle`, `prefab` | Owned package bundle and prefab, prepared by the built-in adapter |
| `provider` | `implementation` | `IWorldContentProvider.LoadAsync(context, cancellationToken)` |
| `game-scene` | `sceneName` | Built-in native scene adapter |
| `discovered` | `implementation` | `IWorldDiscoverySource` enumerates bounded family instances and loads the selected instance |

Fields from other branches are forbidden, even when empty. `transitions` is a nonempty unique list
containing `scene-replacement`, `additive-arena`, or both. `spawn.kind` is `authored-marker` (requires
`markerName`, 1–128 characters) or `provider-default` (forbids `markerName`). Missing or ambiguous
markers fail startup; no camera position or zero-position fallback is used.

`openTo` lists up to 32 unique gamemode IDs. `openToAnyCompatible: true` forbids even an empty `openTo`.
These fields express world-side consent for an open target policy, including that policy's default.
Local references must name gamemodes; cross-package consent requires a satisfied required dependency.

Discovered declarations are families, not static worlds. Neither a family nor a discovered instance
may be named by a target's static `default` or `allow`. Observations can supply concrete instances
only under a permitted open override. A resolved plan records both the instance ID and its family ID.

### Gamemode declaration

Required fields are `id`, `name` and `implementation`; optional fields are `description`,
`worldRequirements` and `sceneChangePolicy`. The implementation is an `IGamemodeFactory` whose
`StartAsync(session, cancellationToken)` returns an operation result containing one controller.

Omitting `worldRequirements` means no extra requirement. A present object must be nonempty and may
contain `transitions` (nonempty unique supported transitions) and `spawn` (`authored-marker` or `any`).
`sceneChangePolicy` is `end-session` or `keep-controller`; omission means `keep-controller`.
Scene notifications never create controllers or change the immutable session identity.

### Launch target declaration

Required fields are `id`, `title`, `gamemode` and `world`; optional fields are `description`,
`sortKey` (integer 0–999) and `transition`. The gamemode reference must resolve to a gamemode.
The `world` object requires `policy` and `default`, with optional `allowPlayerOverride` and `allow`.

| Policy | Allowed worlds | World-side consent |
| --- | --- | --- |
| `fixed` | Only the static default; player override cannot be enabled | Not required |
| `list` | Default plus the required nonempty `allow` list (1–64 unique static IDs) | Not required |
| `open` | Default, plus compatible profile worlds when player override is enabled | Required for every choice, including default |

`allow` is forbidden outside `list`. Absent `allowPlayerOverride` means false. All choices must meet
mode requirements. Manifest-declared cross-package references require satisfied dependencies; an
open world chosen by the player does not invent a dependency from the target's package to that world.

`transition` is `auto` (also the omitted default), `scene-replacement`, `additive-arena` or
`player-choice`. Choices intersect the world's transitions with the gamemode's requirements.
`auto` prefers scene replacement when available, then additive arena. An explicit transition must
belong to the intersection. Only `player-choice` accepts an explicit player transition override.

Resolution uses the exact effective installed/enabled package selection and install facts. It
accumulates independently determinable blocks, deduplicated and sorted by ordinal code, subject and
version. Plans copy immutable package identities and a digest; runtime resolves again against loaded
manifests and verified available bindings before scene work. Registry descriptions cannot add content.

### Runtime lifecycle

The committed phases are `Idle → Preparing → LoadingWorld → StartingMode → Running → Stopping`.
A launch succeeds only at Running, after scene, content, player and resolved spawn readiness.
`IGamemodeSession` supplies immutable IDs, prepared world, cancellation, lifetime, scoped mod context
and bound stop/restart/main-menu operations. Resource-producing facades are rebound to that child
scope while retaining package identity, capabilities and dependency visibility. `IWorldSessionService`
observes committed snapshots and does not expose another package's context.

Competing launches are Busy during startup, shutdown and native drain. Cancellation does not release
native ownership early. Cleanup cancels work, attempts all disposers, aggregates failures and publishes
one terminal outcome. A stale handle cannot stop a newer session. See [Custom worlds](CustomWorlds.md)
for compiled template examples and the session-bound local import API.

### Launcher commands and observed availability

Home, Setup, the CLI and the in-game manager select declared launch targets.
World and transition overrides appear only when the target permits them. The
launcher resolves the exact installed/enabled/pinned package set immediately before
process creation; the runtime compares immutable package identities and resolves
again against loaded manifests before scene work.

One-shot launch wire version 4 carries an explicit `main-menu` or `launch-target`
command, request ID, profile revision, package identities and digest. A target
command also carries the resolved world/transition and original override presence.
Explicit main-menu overrides remembered autoload; Safe Mode uses main-menu and an
empty package set. Only direct startup without a launcher command may use the
manager's remembered choice.

Versioned runtime observations replace `catalog.json`. Their profile/revision,
producer package and package digest must match installed content. Observations can
supply discovered instances and availability reasons, but cannot add targets or
restore disabled packages. A missing observation is unknown availability.

Request-correlated progress and separate launch/terminal outcomes distinguish
process creation, confirmed Running and session teardown. Missing acknowledgement
remains unconfirmed. Durable legacy selections retain their original values when
no unique valid target mapping exists, with explicit target or main-menu repair.
See [Development loop](CliDevLoop.md#choose-and-confirm-the-launch) for CLI examples
and acknowledgement behavior.

## Deliberate exclusions and retired fields

V6 has no `options`, `optionValues` or `sessionExtensions`. `worldGamemodes` and the previously retired
top-level `gamemodes` are rejecting sentinels, including empty values. Contributions belong under the
wrapper. The package entry point is not a second controller factory; `GamemodeHost` and notification-
driven startup are removed. Use the `gamemode` or `world` template for executable declarations.

## Migration from V3, V4 or V5

```sh
topiaforge migrate-manifest --project .
topiaforge check package .
```

A positional project path is also accepted. Use exactly one path; unknown, duplicate
or incomplete options fail before writing. A valid V6 manifest is an unchanged no-op.
V3 mechanically normalizes legacy dependency maps/lists, capability disclosures and conflict
ranges; V4/V5 then follow the same strict checks. Malformed arrays, wrong scalar types,
duplicate dependencies or unknown legacy entry fields cause a no-write failure.
Legacy gamemode IDs must satisfy the source contract's 2–64-character ASCII limit,
including in stub mode. V6 declaration IDs retain their separate 96-character limit.
Raw JSON with duplicate property names (including escaped-equivalent names) or numbers
that cannot survive JSON decoding and encoding without a changed value is also refused.
Equivalent number spellings may change; migration never silently rounds a value.

Successful migration preserves untouched JSON values and property presence. Formatting
may change. The writer checks the original bytes and commits one same-directory atomic
replacement; cooperating migrations hold an exclusive lease. It refuses changed sources
and unsafe paths. This is not an operating-system compare-and-swap against arbitrary
editors, so close other manifest editors while migrating.

Legacy `worldGamemodes` contains no implementation or launch policy. Normal migration
refuses to invent a factory, world, target, spawn setting or missing compatibility
range. Diagnostics identify the source file, JSON pointer/original entry index, ID when
available, ownership violations and required author fields. Optional `worldRequirements`
remain optional. IDs are retained for explicit repair, never silently renamed.

When the legacy entries are well formed, an explicitly requested stub can preserve
incomplete authoring work:

```sh
topiaforge migrate-manifest --project . --stub
```

When required author decisions remain, this writes an **intentionally invalid** V6
document, retains every original mode and adds `x-migration-todo`. It returns exit
code 1; the incomplete result is not a publishable package. A gamemode stub also
adds the required `world-service` capability if absent, preserving existing capability
values and order. Diagnostics disclose that mechanical addition.

Complete the required fields listed in the notes using the
[gamemode/world templates](CustomWorlds.md), then validate. Add worlds, launch targets
and spawn policies only when the package provides them; `worldRequirements` remains
optional. If no author decisions remain, `--stub` follows ordinary successful migration
with exit code 0, or leaves valid V6 input unchanged. Stub mode still refuses malformed
legacy data and never overwrites existing migration notes. Metadata-only
`--gamemode` and `mod add|remove gamemode` commands are retired; use `new mod <id>
--template gamemode` and edit `contributions.gamemodes` and `contributions.launchTargets`.

## Compatibility rule

V6 is the supported authoring schema. V4 and V5 dispatch reject before interpreting fields.
New numbered contracts require explicit
readers, validators and migration; no compatibility promise requires retaining unreleased V5.
See [Compatibility policy](CompatibilityPolicy.md) and [Multiplayer API preview](Multiplayer.md).
Native timing, visuals and release acceptance remain pending until verified against the actual candidate.
