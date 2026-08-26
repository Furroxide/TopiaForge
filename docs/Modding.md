---
title: TopiaForge V1 SDK for Robotopia
description: Understand the safe mod authoring model, templates, packages, and lifecycle for Robotopia.
---

# TopiaForge V1 SDK for Robotopia

TopiaForge is the supported authoring boundary for mods targeting Robotopia. Safe mods compile only against
reference packages supplied by the TopiaForge release. They do not need a source checkout, engine
editor, Robotopia assemblies, reflection handles, or engine object types.

New here? Follow [Your first mod](YourFirstMod.md), then use this page as the map to deeper guides.

## Install the CLI

Extract the TopiaForge release for your platform and put its root on `PATH`. Verify the release and
the pinned .NET SDK:

```sh
topiaforge doctor --strict
```

Only the `.NET SDK` pinned by the generated `global.json` is needed for ordinary Robotopia code mods. An
engine editor is optional and limited to authoring bundle-backed visual/world content.

## Authoring model

Derive one public, parameterless entry class from `TopiaForgeMod` and override `OnLoad()`. The
loader attaches `Context` first. `OnUnload()` is optional because SDK acquisitions are owned by
`Context.Lifetime`.

This example is generated from the same minimal template that the release acceptance suite builds,
tests, packs, moves outside the release tree, and validates:

<!-- topiaforge-snippet path="templates/mod/minimal/{{TYPE_NAME}}Mod.cs" -->

All lifecycle callbacks and engine-facing SDK calls run on Robotopia's Unity main thread. Events are subscribed
through `Context.Events`; asynchronous SDK methods return to the main thread and combine caller
cancellation with the lifetime stopping token.

## Choose a template

```sh
topiaforge list templates
topiaforge new mod example.my-mod --template gameplay --name "My Mod" --author "You" --license AGPL-3.0-or-later --version 1.0.0
```

| Template | Demonstrates |
| --- | --- |
| `minimal` | Typed validated/migrating config, logging, and a namespaced command. |
| `gameplay` | Named input, player aim, a safe physics query, logging, and a toast. |
| `gamemode` | Worlds registration, session events, and automatic teardown. |
| `service` | A dependency-scoped typed provider plus consumer contract. |
| `ui` | Configurable input and a TopiaForgeUi-backed window. |
| `asset` | Package bundle/prefab loading, spawning, and result handling. |
| `world` | Bundle-backed world/menu registration and save-aware teardown. |

Every scaffold includes:

- an exact V1 `PackageReference` and analyzer;
- manifest schema V5 with current compatibility defaults;
- project-local `global.json` and NuGet lock state;
- source-control rules for generated restore output; and
- an NUnit project using `TopiaForge.Mods.Testing`.

## Core and specialist APIs

`IModContext` exposes discoverable, non-null properties for identity/runtime metadata, logging,
lifetime, events, files, config, installation-local storage, input, time, scheduling, the local player, scenes, entities,
physics, interactions, items, assets, audio, UI, localization, commands, diagnostics, and
extensions. See [Core services](CoreServices.md) for the complete service map and usage rules.

Creator Content, RobotKit, Worlds, Chronos, Prompts, and the multiplayer preview are separate
Unity-free module contracts. Add a module with `topiaforge mod add <module>` so its compile-time
package and runtime manifest dependency stay in sync. See [Specialist modules](Modules.md) and the
[Sandbox guide](Sandbox.md).

## Errors, queries, and cancellation

- Use `Try...` methods for cheap state queries.
- Use `OperationResult<T>` for expected operation failures and branch on stable `ModErrorCode`.
- Use `Task<OperationResult<T>>` for asynchronous work.
- Pass caller cancellation when useful; lifetime shutdown is always combined automatically.
- Treat exceptions as bugs in arguments or contracts, not normal control flow.

`Context.Runtime.UnavailableCapabilities` explains why a provider or Robotopia adapter is unavailable.
Mods should disable only the affected feature and show a useful message.

## Manifest, restore, and packaging

`topiaforge.mod.json` schema V5 is canonical. Omit `multiplayer` for a standalone-only mod; add it through the multiplayer module command when needed. Required dependencies and optional dependencies are
ID-to-range maps. Compatibility ranges, platform/architecture/content constraints, capabilities,
load-order hints, exported API assemblies, and namespaced `x-*` metadata are validated before code
executes. Read [Manifest V5](ManifestV5.md) for every field and
[Multiplayer API preview](Multiplayer.md) before opting in.

```sh
topiaforge restore
dotnet test --configuration Release
topiaforge pack
topiaforge check package dist/example.my-mod-1.0.0.topiaforgemod
```

`topiaforge dev` runs the full restore-to-launch loop. Projects consume the SDK from their ordinary
NuGet cache; they never build against the CLI extraction directory.

The package validator checks archive paths, manifest/schema compatibility, managed PE validity,
assembly identity, entry type/base class, public parameterless construction, SDK compatibility,
canonical directory identity/version, and bundled framework assemblies without executing mod code.

## Trust and capabilities

Mods are trusted code loaded into the Robotopia process. Manifest capabilities disclose sensitive behavior and improve
diagnostics; they are not a security sandbox. Review package source, author, archive hash, and the
aggregate capabilities of required dependencies before installation. See
[Privacy and capability disclosure](PrivacyAndCapabilities.md).

## Compatibility boundary

Safe contracts expose SDK values and opaque handles, not native objects or reflection types. The
loader and module providers absorb Robotopia-specific engine complexity. An explicitly unstable interop package
exists for exceptional adapters and low-level patches, is absent from normal templates, and is not
covered by the V1 compatibility guarantee. See [Advanced interop](UnityInterop.md).

Loaded assemblies cannot be replaced in-process. Enable, disable, update, and uninstall actions are
staged when required and take effect after a Robotopia restart.

## Next steps

- [Manifest V5](ManifestV5.md)
- [Multiplayer API preview](Multiplayer.md)
- [Core services](CoreServices.md)
- [Specialist modules](Modules.md)
- [Sandbox](Sandbox.md)
- [Test a mod](TestingMods.md)
- [Development loop](CliDevLoop.md)
- [Diagnostics](Diagnostics.md)
- [Zombies worked example](Zombies.md)
- [V1 capability coverage](CapabilityMatrix.md)
- [Live Robotopia acceptance](LiveGameAcceptance.md)
- [Publish a mod](PublishingYourMod.md)
