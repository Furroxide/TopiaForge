---
title: Sandbox acceptance mod
description: How the first-party Robotopia Sandbox exercises safe Worlds and RobotKit V1 contracts.
---

# Sandbox acceptance mod

Sandbox is TopiaForge's first-party Robotopia acceptance mod for custom-world, robot, dialogue,
asset, interaction, and UI authoring through the safe V1 SDK. Its consumer project has no direct
Unity or Robotopia assembly, reflection, or patching dependency.

## Dependencies

The manifest declares schema V5 dependencies on the Worlds and RobotKit runtime providers, and the
project references the exact `TopiaForge.Mods.Worlds` and `TopiaForge.Mods.RobotKit` contract
packages. Providers are resolved with `Context.RequireExtension<T>()`.

## Lifecycle

Sandbox derives from `TopiaForgeMod`. During `OnLoad()` it registers its Robotopia world/menu
definitions, named input actions, session observers, and safe in-game UI surfaces. Registrations,
subscriptions, spawned entities, robot agents, conversations, and content handles belong to
`Context.Lifetime`.

Session end, mod unload, and partial-load failure therefore follow the same reverse-order,
idempotent cleanup path. Reload tests assert no actions, registrations, providers, UI surfaces,
assets, entities, or scheduled work remain.

## Feature coverage

- Worlds: Robotopia custom content root, menu entry, current session, pause/exit behavior, and teardown.
- RobotKit: type discovery, reachable spawn, objectives, interaction, dialogue, and optional voice.
- Core services: named input, process-local Robotopia player and camera aim state, physics/entity
  queries, package assets, audio, config, installation-local storage, localization, commands,
  diagnostics, and scheduling.
- Robotopia UI: HUD/window/modal/toast behavior through `Context.Ui`, including scale, high
  contrast, and reduced motion.

Remote dialogue remains optional and has a deterministic fallback. Any unavailable Robotopia
provider binding disables only the affected feature and reports the reason in the mod's attributed
diagnostics.

Sandbox is a live acceptance case, not a substitute for the smaller generated templates. New
modders should begin with [Your first mod](YourFirstMod.md), then add [Specialist modules](Modules.md)
as their feature needs grow.
