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
topiaforge new mod example.world --template world --name "Example World" --author "You" --license AGPL-3.0-or-later --version 1.0.0
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

**Never block on `CreateAsync()`.** It is driven by the game's own asynchronous asset loader, so the
task completes on the main thread. Calling `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()` from
the main thread stops the frame loop that would have completed it, and the game hangs with no
recovery. Drive it with `PendingOperation<IWorldContent>` and poll that from your per-frame update;
it also hands back content that arrives after a cancel or timeout so you can release it. The analyzer
reports a blocking wait as [TF1008](Diagnostics.md#tf1008). The same rule applies to every
`IAssetService` load.

## Hosting a gamemode

`GamemodeHost<TController>` owns the wiring between a Worlds gamemode and the object that runs one round
of it, so an entry point keeps only the parts that are about the gamemode:

```csharp
var hosted = GamemodeHost<MyRound>.Create(
    Context,
    Context.RequireExtension<IWorldGamemodeService>(),
    GamemodeId,
    session => new MyRound(Context, session),
    new GamemodeDefinition(GamemodeId, "My Mode", "..."),
    new GamemodeMenuEntry(MenuId, "My Mode", "...", GamemodeId));
if (hosted.TryGetValue(out var host))
{
    host.AddPauseAction(new WorldPauseAction(
        "restart", "RESTART ROUND", () => host.Controller?.Restart(), destructive: true));
}
```

It registers the gamemode and menu entry and rolls the first back if the second fails, subscribes to
session changes and defers the unsubscribe onto the mod lifetime, **replays a session that is already
running** (omitting that is why a hot reload mid-session leaves a mod that never wakes up), keeps exactly
one controller alive, and re-registers pause actions for every session. Pass `null` for the definition and
menu entry to attach to a gamemode the provider already offers, as Sandbox does with the built-in sandbox.

A throwing controller factory is treated as a failed session — partial controller disposed, diagnostic
reported, session ended as `LoadFailed` — rather than leaving the player in a broken world.

Related contracts worth knowing: `IWorldPauseMenuService` adds actions to the vanilla pause menu and
`InterceptExit`/`WorldPauseExitDecision` decide what the vanilla exit-to-menu option does during your
session; `GameScenes.MainMenuSceneName` and `IsNonGameplayScene` identify non-gameplay scenes;
`ShopItem`, `IShopWallet`/`ShopWallet`, and `ShopTransactions.TryPurchase` provide a purchase arbiter with
a stable rule order so a shop UI and game logic cannot disagree.

## Holding gameplay for modal UI

A shop, inventory, dialogue, or game-over screen needs gameplay to stop. `GameplayPause` does that in one
place instead of per surface:

```csharp
pause = new GameplayPause(Context, "mymod-shop", time.AsPauseSource(), "MYMOD_SHOP_PAUSE_FAILED");

void OpenShop()  => pause.Request();
void CloseShop() => pause.Release();
void OnUpdate(float _) => pause.Tick(Context.Time.Frame.UnscaledDeltaTime);
```

It prefers a Chronos world freeze, degrades to suspending player control when Chronos is absent or its
hooks are unresolved, reports a total failure once rather than every frame, and reacquires a hold the host
takes away mid-session. Tick it with an **unscaled** delta — a scaled clock stops while the world is frozen,
which would freeze the retry loop too. `Kind` reports whether an actual world freeze or only the
player-control fallback is holding.

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
