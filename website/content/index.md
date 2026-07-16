---
title: TopiaForge V1 mod SDK
description: Build Robotopia mods without taking a dependency on Unity or Robotopia internals.
template: splash
hero:
  tagline: A clean, typed Robotopia SDK for utility mods, gameplay abilities, worlds, robots, story content, and UI.
  actions:
    - text: Build your first mod
      link: /getting-started/first-mod/
      icon: right-arrow
    - text: Explore core services
      link: /guides/core-services/
      icon: open-book
      variant: minimal
---

TopiaForge V1 is the supported modding SDK for Robotopia. It gives each mod an owner-scoped context
with safe services for input, player state,
physics, entities, assets, audio, UI, configuration, storage, localization, commands, and diagnostics.
Resources are tied to the mod lifetime, so subscriptions, registrations, handles, and spawned content
are released automatically after unload or a failed load.

Start with the `minimal` template if this is your first C# project. Add specialist modules only when
you need robots, worlds and modes, time control, prompt overrides, or UGC live sync.

## What you do not need

- A source checkout of TopiaForge.
- An engine editor for ordinary code mods.
- Robotopia assembly references, reflection, or Unity object handles.
- Manual owner identifiers or global cleanup calls.

The optional advanced interop package is isolated from the safe SDK and has a separate compatibility
policy. Most mods should never install it.

The [V1 capability matrix](../../docs/CapabilityMatrix.md) traces every launch promise to a safe
API, compiled sample, task guide, deterministic fake, and live Robotopia acceptance case.
