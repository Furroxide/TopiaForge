---
title: Custom Robotopia worlds and gamemodes
description: Register bundle-backed Robotopia worlds and modes through the safe Worlds module.
---

# Custom Robotopia worlds and gamemodes

The Worlds module is TopiaForge's safe authoring boundary for custom Robotopia worlds and gamemodes.
It owns mod-defined worlds, gamemodes, menu entries, scene transitions, pause actions, shops, and
one current `WorldSession`. Consumer mods never coordinate scenes or global teardown directly.

## Start from a compiled scaffold

```sh
topiaforge new mod example.world --template world --name "Example World" --author "You" --license MIT --version 1.0.0
topiaforge restore --project example.world
```

The world template uses `TopiaForge.Mods.Worlds`, the core asset service, `BundleWorldContent`, and
lifetime-owned `IWorldRegistration` handles. The gamemode template adds session events and a
per-frame Robotopia gameplay loop. Both have NUnit lifecycle tests and are built, packed, relocated, and validated
from the extracted release in CI.

## Authoring flow

1. Build a Robotopia-compatible prefab bundle for a declared `contentTargets` value.
2. Place it under the mod's `AssetBundles/` content root.
3. Register a `WorldDefinition` and `ICustomWorldContent` through `IWorldGamemodeService`.
4. Register a `GamemodeMenuEntry` that pairs the world with a gamemode.
5. Test create, session start/end, unload, and reload with `TopiaForge.Mods.Testing`.

`BundleWorldContent.CreateAsync()` loads and spawns through opaque asset/entity handles. Returned
content and registrations are released automatically after session teardown, unload, or failed load.

## Pause and save behavior

World pause actions are registered through the Worlds provider and remain owner-bound.
`Context.LocalStorage` is suitable only for installation-local settings and progress that does not
need to follow a save or synchronize between peers. Shared/save-scoped story state requires a
future authoritative world-state service. End the current session with an explicit
`WorldSessionEndReason`; do not infer teardown from arbitrary scene polling.

Live acceptance for custom Robotopia worlds is Windows/Proton-only for V1. Other Robotopia code mods
remain portable when their manifest constraints and content are portable.

See [Specialist modules](Modules.md#worlds), [Manifest V5](ManifestV5.md#package-contract),
and [Test a mod](TestingMods.md).
