---
title: Manifest V5 (retired)
description: The retired V5 package manifest, why it was replaced, and how to move a project to V6.
---

# Manifest V5 (retired)

**Manifest V5 was retired before the first public release. Use [Manifest V6](ManifestV6.md).**

```bash
topiaforge migrate-manifest --project <path>
```

`schemas/topiaforge.mod.v5.schema.json` now rejects every document, and both readers refuse a V5
`schemaVersion` with the command above rather than a bare "unsupported version". This page stays
because manifests written against V5 still exist, and a reader who lands here needs to know where the
contract went.

## What V5 could not say

A V5 gamemode declaration was three fields:

```json
"worldGamemodes": [
  { "id": "author.mod.survival", "name": "Survival", "description": "Wave survival." }
]
```

No implementation owner, no world, no launch identity. Everything that actually decided what ran lived
in C# — so a manifest could declare a gamemode nothing implemented, or a mod could implement one the
manifest never mentioned, and the two cases were indistinguishable from outside. That is the defect
V6's `contributions` exists to close: a declaration names the type that runs it, the worlds it can run
in, and the target the player picks.

The migration carries everything mechanical and **refuses** when the parts only an author knows are
missing, naming each one. [Manifest V6](ManifestV6.md) lists exactly what it refuses and why.

## The rest of V5

Everything below described V5 and is kept for reference. V6 carries all of it unchanged except
`worldGamemodes`, which has no successor.

## Minimal standalone manifest

```json
{
  "$schema": "https://raw.githubusercontent.com/furroxide/TopiaForge/main/schemas/topiaforge.mod.schema.json",
  "schemaVersion": 5,
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

The command keeps the manifest on V5, pins the `TopiaForge.Mods.Multiplayer` contract,
`TopiaForge.Mods.Multiplayer.Generators`, and runtime provider to the same exact release, scaffolds protocol
metadata, and creates the checked-in `topiaforge.multiplayer.lock.json`. Removing the module reverses those changes
only after an explicit command and leaves the standalone manifest on V5.

## Session manifest

```json
{
  "schemaVersion": 5,
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
exact synchronized-file hashes. V5 mods without multiplayer metadata block multiplayer with a per-mod explanation;
they remain fully supported in standalone play. Optional session mods that do not negotiate remain non-fatal, while
the admission report retains structured inactive reasons for launcher diagnostics. Retired V4 packages fail manifest
validation before admission.

TopiaForge never silently disables a mod and never downloads and executes a server package. A launcher may offer a
derived profile with incompatible mods disabled or a trusted-registry install plan, but either action requires
explicit approval.

## Compatibility rule

V5 is the only supported schema. A future manifest schema must receive a new reader, validator, and explicit
migration; it may not change what V5 means. See [Compatibility policy](CompatibilityPolicy.md) and
[Multiplayer API preview](Multiplayer.md).
