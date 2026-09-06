---
title: Manifest V6
description: Canonical TopiaForge package manifest, including optional multiplayer metadata.
---

# Manifest V6

Manifest V6 is the current authoring contract and the canonical schema alias. It is strict: unknown fields are
rejected unless their name begins with `x-`, and collections, strings, paths, and dependency graphs are
bounded before an assembly loads. The `multiplayer` object is optional. Omitting it is the canonical
standalone-only declaration.

Manifest V4 was retired before the first public release. Tooling rejects it with a migration command
instead of carrying a second permanent contract.

The editor-friendly `topiaforge.mod.schema.json` URL names the latest supported schema. The immutable
`topiaforge.mod.v6.schema.json` file is a self-contained V6 contract and never references that moving alias.
The redesign retires V5 before release; the intermediate activation slice still accepts explicit V5
dispatch while migration tooling is completed. Do not author new V5 packages.

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

`dependencies` and `optionalDependencies` are ID-to-version-range maps. `loadAfter` and `loadBefore`
are soft ordering hints, not implicit dependencies. Supported ranges include exact, wildcard, caret,
tilde, and comparator sets. Required dependencies block resolution; optional dependencies participate
only when a compatible provider is installed.

`platforms`, `architectures`, and `contentTargets` constrain where a package may load. `conflicts` is
a bounded list of `{ id, versionRange?, reason? }`. `apiAssemblies` is the only assembly surface a
dependent package may compile against.

`capabilities` is a bounded disclosure list. Known values are `asset-bundles`, `filesystem`,
`filesystem-watch`, `harmony-patch`, `hud`, `input`, `navigation`, `network`, `microphone`, `particles`,
`physics`, `physics-settings`, `player-control`, `player-token`, `prompt-overrides`, `quality-settings`,
`remote-ai`, `render-settings`, `robot-spawning`, `scene-management`, `speech-to-text`, `time`,
`unsafe-native`, and `world-service`. Capabilities do not grant or sandbox authority.
In particular, `network` continues to mean arbitrary outbound networking and is unrelated to
TopiaForge's multiplayer transport.

Package metadata includes `description`, `category`, `tags`, `icon`, `screenshots`, `homepage`,
`source`, `license`, and `licenseFiles`. All package paths are portable, relative, and collision-safe.
Packing owns `builtWith` and `hashes`; authors should not hand-maintain generated hashes.

Namespaced `x-*` fields are retained and bounded without receiving core semantics. Any other unknown
field is rejected.

## Add multiplayer support

Add the preview contract through the CLI rather than editing project and package metadata independently:

```sh
topiaforge mod add multiplayer
topiaforge restore
topiaforge mod sync multiplayer
```

New scaffolds use V6. The command preserves the input manifest version, pins the `TopiaForge.Mods.Multiplayer` contract,
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
the admission report retains structured inactive reasons for launcher diagnostics. Retired V4 packages fail manifest
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

## Deliberate exclusions and retired fields

V6 has no `options`, `optionValues` or `sessionExtensions`. `worldGamemodes` and the previously retired
top-level `gamemodes` are rejecting sentinels, including empty values. Contributions belong under the
wrapper. The package entry point is not a second controller factory; `GamemodeHost` and notification-
driven startup are removed. Use the `gamemode` or `world` template for executable declarations.

Legacy metadata does not supply an implementation, world or spawn policy. Migration must preserve
untouched JSON values and property presence and refuse missing required information rather than invent
it. During the activation slice the full V5 retirement command is still pending; do not interpret the
schema's planned migration guidance as evidence that runtime or game acceptance has passed.

## Compatibility rule

V6 is the canonical authoring schema. V5 dispatch exists temporarily during the sequential redesign
and is removed in its retirement slice before release. New numbered contracts require explicit
readers, validators and migration; no compatibility promise requires retaining unreleased V5.
See [Compatibility policy](CompatibilityPolicy.md) and [Multiplayer API preview](Multiplayer.md).
Native timing, visuals and release acceptance remain pending until verified against the actual candidate.
