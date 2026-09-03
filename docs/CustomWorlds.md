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
3. Declare the world under `contributions.worlds` in `topiaforge.mod.json`: its content binding, the
   transitions it supports, and where the player spawns. See [Manifest V6](ManifestV6.md).
4. Declare a launch target naming a gamemode. `io.github.furroxide.topiaforge.worlds.freeplay` is the
   neutral mode the Worlds provider implements, so shipping a world needs no gameplay dependency of
   its own.
5. Test create, session start/end, unload, and reload with `TopiaForge.Mods.Testing`.

A world is a declaration plus content. Registering one from code is not the authoring path: the
launcher reads the manifest before anything loads, which is what lets it list your world and report a
broken declaration to you rather than to a player.

`BundleWorldContent.CreateAsync()` loads and spawns through opaque asset/entity handles. Returned
content and registrations are released automatically after session teardown, unload, or failed load.

**Never block on `CreateAsync()`.** It is driven by the game's own asynchronous asset loader, so the
task completes on the main thread. Calling `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()` from
the main thread stops the frame loop that would have completed it, and the game hangs with no
recovery. Drive it with `PendingOperation<IWorldContent>` and poll that from your per-frame update;
it also hands back content that arrives after a cancel or timeout so you can release it. The analyzer
reports a blocking wait as [TF1008](Diagnostics.md#tf1008). The same rule applies to every
`IAssetService` load.

## Local `.roboworld` worlds

A world built in the official [Robotopia Creator](https://robotopia.gg/editor/) exports as a `.roboworld` file.
Worlds can load one of those from disk, through the game's own import host — the same code path the
game uses for a local export, so nothing here parses the format itself.

This path is strictly local. It signs nobody in, publishes nothing, and calls no backend: the game's
import host takes a folder and a file name and reads them off the machine you are sitting at. Worlds
touches none of the cloud entry points (`UgcPublishedProjectLoader`, `UgcAutomergeSyncClient`,
`UgcLaunchUrlStartup`) to make that true by construction rather than by promise.

Configure it in the Worlds mod config:

| Key | Default | Meaning |
| --- | --- | --- |
| `enableLocalWorlds` | `true` | Whether local exports may be loaded at all. |
| `localWorldFolder` | `""` | The folder scanned for exports. Empty means the folder the game itself scans. |

Recognized extensions are `.roboworld`, `.json`, and `.json.gz` — the three build 2409 scans for.

**The folder is a trust boundary, not a convenience.** The import host accepts any path, so Worlds
confines an import to the configured folder and refuses anything outside it *before* it checks whether
the file exists or has a usable extension — otherwise a refusal message would tell a caller which
files exist elsewhere on the machine. A sibling folder that merely shares the configured folder's name
prefix is not inside it.

An export is parsed with the game's own loader before the scene is touched, so a malformed file is
refused while the current world is still intact and the reason a player sees is the game's own
wording. After the import, Worlds checks that the host actually produced a scene: `ImportFile` returns
`void` and swallows its own failures, so "it was called" is not "it worked".

Worlds snapshots the game's import selection before overriding it and restores it when the provider
unloads. `UgcImportHostConfig` is a shipped asset shared with the game's own import host; leaving a
TopiaForge folder in it would silently change what the game does next.

Every binding on this path is `Degraded`. If a future build moves the import host, local worlds stop
loading with a stated reason and nothing else in Worlds changes behaviour.

## Hosting a gamemode

The gamemode, the worlds it can run in, and the target the player picks are declared in
`topiaforge.mod.json`. The declaration names the type that implements it:

```json
"contributions": {
  "gamemodes": [
    {
      "id": "author.mod.mode",
      "name": "My Mode",
      "implementation": { "type": "Author.Mod.MyGamemode" },
      "worldRequirements": { "transitions": ["additive-arena"], "spawn": "any" }
    }
  ]
}
```

```csharp
public sealed class MyGamemode : IGamemodeFactory
{
    public string GamemodeId => MyMod.GamemodeId;

    public OperationResult<IGamemodeController> CreateController(IGamemodeSession session) =>
        OperationResult<IGamemodeController>.Success(new MyRound(session.Mod, session.World));
}
```

`GamemodeHost<TController>` still owns session wiring — subscribing, replaying a session already
running, one controller per session, and teardown — and is what drives sessions today. Pass it no
`GamemodeDefinition` or `GamemodeMenuEntry`: those live in the manifest now, and a second copy in code
is a second source of truth.

```csharp
var hosted = GamemodeHost<MyRound>.Create(
    Context,
    Context.RequireExtension<IWorldGamemodeService>(),
    GamemodeId,
    session => new MyRound(Context, session));
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

Live acceptance for custom Robotopia worlds is Windows/Proton-only on the 0.x line. Other Robotopia code mods
remain portable when their manifest constraints and content are portable.

See [Specialist modules](Modules.md#worlds), [Manifest V6](ManifestV6.md),
and [Test a mod](TestingMods.md).
