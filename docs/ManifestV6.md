---
title: Manifest V6
description: The TopiaForge package manifest, where worlds, gamemodes and launch targets are declared separately and each names what implements it.
---

# Manifest V6

Manifest V6 is the sole manifest schema TopiaForge supports. It is strict: unknown fields are rejected
unless their name begins with `x-`, and collections, strings, paths, and dependency graphs are bounded
before an assembly loads. The `multiplayer` object is optional; omitting it is the canonical
standalone-only declaration.

Everything V5 could say, V6 says the same way. The one difference is `contributions`.

## Why the version moved

A V5 manifest could describe a gamemode but not bind one. A `worldGamemodes` entry was three fields:

```json
{ "id": "author.mod.survival", "name": "Survival", "description": "Wave survival." }
```

That is a label. It does not say which type runs the mode, which world it starts in, what the player
picks in a menu, or what happens when the scene changes underneath it. All of that lived in C#, in a
different file, reachable only by reading the code — so the manifest and the thing that actually ran
could disagree, and nothing would notice. A gamemode could be declared and unimplemented, or
implemented and undeclared, and both look identical from outside.

V6 splits that into three declarations, each of which names its owner.

## `contributions`

```json
"capabilities": ["world-service"],
"contributions": {
  "worlds": [ ... ],
  "gamemodes": [ ... ],
  "launchTargets": [ ... ]
}
```

Declaring anything here requires the `world-service` capability, because a package with a launch
surface owns world content at runtime and that is worth disclosing.

The wrapper exists because `gamemodes` is already a retired top-level field name. A supported
top-level `gamemodes` key cannot coexist with a sentinel that rejects it, so the declarations sit one
level down.

### Ids

Every declaration id must start with the package's own `name` plus a dot, and be longer than that
prefix. A package can only declare things inside the namespace it owns, and `id` equal to `name` is
not a declaration — it is the package.

Ids may be up to 96 characters, wider than the 64 a package `name` allows, because a declaration id
always carries its package's name as a prefix. `io.github.furroxide.topiaforge.sandbox.creator.menu`
spends 51 characters before it says anything.

### Worlds

```json
{
  "id": "author.mod.island",
  "name": "The Island",
  "content": {
    "kind": "bundle",
    "bundle": "AssetBundles/island.bundle",
    "prefab": "assets/world/world.prefab"
  },
  "transitions": ["additive-arena"],
  "spawn": { "kind": "authored-marker", "markerName": "SpawnPoint" },
  "openToAnyCompatible": true
}
```

`content.kind` is one of four, and each requires exactly the fields it uses:

| kind | means | requires |
| --- | --- | --- |
| `bundle` | a prefab shipped in the package | `bundle`, `prefab` |
| `provider` | a world the package builds at runtime | `implementation` |
| `game-scene` | a scene the game already has | `sceneName` |
| `discovered` | a *family* of worlds enumerated at runtime | `implementation` |

A `discovered` id is a prefix, not a world. Its members are `<id>.<slug>` and exist only once the game
has run and reported them, which is why a launch target's world policy may never name a family or a
member: a stored selection would otherwise point at content that has never existed on this
installation.

`spawn` is `authored-marker` (with a `markerName`) or `provider-default`. It is deliberately not a
transform. The manifest declares no coordinates anywhere, because a spawn point drifting in its last
bits is the bug nobody attributes to a manifest.

`openTo` and `openToAnyCompatible` are the world's consent to be paired with a gamemode by a target
whose world policy is `open`. Consent applies to that one policy and no other, so a world's package
never has to depend on the gamemodes that use it.

### Gamemodes

```json
{
  "id": "author.mod.survival",
  "name": "Survival",
  "implementation": { "type": "Author.Mod.SurvivalGamemode" },
  "worldRequirements": { "transitions": ["additive-arena"], "spawn": "any" },
  "sceneChangePolicy": "end-session"
}
```

`implementation.type` names a class implementing `IGamemodeFactory`. It is a namespace-qualified CLR
type name and nothing else: the pattern rejects assembly-qualified names and nested-type syntax, so a
binding can never reach a type outside the assembly it points at.

`implementation.assembly` is optional and means this manifest's `entryAssembly` when omitted. When
present it must also be a key of `hashes`, so a declaration can only bind to bytes the installer
verified.

`worldRequirements` is omitted entirely to mean no requirement. An empty object is rejected, because
absent already means that and the two have to stay distinguishable.

### Launch targets

```json
{
  "id": "author.mod.menu",
  "title": "Survival",
  "sortKey": 20,
  "gamemode": "author.mod.survival",
  "world": {
    "policy": "fixed",
    "default": "author.mod.island",
    "allowPlayerOverride": false
  },
  "transition": "auto"
}
```

A launch target is what the player picks. Menus, Home, Setup and the CLI all select the same one, so
its identity is user-facing and outlives any particular menu.

`world.policy` is one of three:

- **`fixed`** — only `default`. State this when the mode was never meant to offer a choice.
- **`list`** — `default` plus `allow`, one-directional. The listed worlds need not agree.
- **`open`** — `default` plus any world in the profile that meets the gamemode's requirements *and*
  consents.

`transition` is `auto` when absent. `auto` takes the highest-precedence member of the world's
transitions intersected with the gamemode's requirements, and **scene replacement outranks the
additive arena**. The precedence is fixed rather than discovered, because a world that supports both
already ships: without a stated order, `auto` would be a launch whose behaviour depends on declaration
order. `player-choice` offers the whole intersection to the player instead.

## Cross-package references

Any id a package does not own must be prefix-owned by a package it *requires*. `optionalDependencies`
never qualifies: a reference that resolves only when an optional package happens to be installed is a
launch that fails without warning for everyone else.

Ownership goes to the longest matching package name. Package ids may contain dots, so a package named
`author.mod.extra` legitimately sits inside `author.mod`'s namespace; falling through to the shorter
name would let one package answer for ids another package covers.

## Migrating from V5

```bash
topiaforge migrate-manifest --project <path>
```

Everything mechanical is carried: the version, the schema URL, every other field byte for byte, and
`world-service` added to capabilities.

**A manifest that declared gamemodes will be refused, by design.** The tool tells you which entries it
cannot carry and exactly what each one needs. It refuses because the missing facts are not in the old
document at all:

- **`implementation.type`** — V5 recorded no implementation. `entryType` is the mod class, not the
  gamemode factory. Binding it would validate and then fail at first launch.
- **The launch target** — its id, title and world existed only in C#. Deriving a target from a menu
  entry is guesswork about what the player was meant to see.
- **`world.default`** — the world a V5 gamemode started in was runtime configuration a player could
  edit. TopiaForge will not promote a config default into a manifest.
- **`worldRequirements.transitions`** — V5 had no antecedent in any form.

`--stub` writes the mechanical half plus an `x-migration-todo` block naming what is left. The result
**still fails validation on purpose**, so a half-migrated project cannot be packed or published by
accident.

## Both readers, one contract

The manifest is read twice: by the launcher in Dart, and by the mod manager in C#. Only the launcher
ever sees the JSON Schema — nothing in the manager reads `topiaforge.mod.schema.json`. Every rule the
schema states is therefore written out in the manager too, and the rules no schema can state —
ownership, cross-package references, policy coherence — exist only in the two validators.

What holds them together is `tests/fixtures/gamemode-v6`. Both readers enumerate the same generated
index, both must execute every case on a channel they are listed for, error codes are compared as a
set rather than as a verdict, and a divergence between the two needs a stated reason to land. A
fixture no runner executes fails the build.

## Schema files

- `schemas/topiaforge.mod.schema.json` — the alias editors resolve. Currently V6.
- `schemas/topiaforge.mod.v6.schema.json` — the immutable V6 contract. Never references the alias.
- `schemas/topiaforge.mod.v5.schema.json` — retired, and rejects every document. See
  [Manifest V5](ManifestV5.md).

A future version adds a reader and a schema beside these. It never widens an existing one.
