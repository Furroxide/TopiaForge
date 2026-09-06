---
title: Custom Robotopia worlds and gamemodes
description: Declare V6 worlds and launch targets, start owned gamemode sessions, and import local content safely.
---

# Custom Robotopia worlds and gamemodes

Manifest V6 declares worlds, gamemodes, and launch targets under `contributions`.
The manager resolves a target against the enabled package set, prepares its world, and invokes
one gamemode factory. The Worlds module supplies Open Sandbox, Free Play, native world discovery,
session observation, local content imports, and pause-menu integration.

The public contracts live in `TopiaForge.Mods.Worlds`, with namespace `TopiaForge.Mods`.
Declare a dependency on `io.github.furroxide.topiaforge.worlds` when consuming its runtime
extensions. Keep other provider dependencies and capabilities explicit. See
[Manifest V6](ManifestV6.md) for the complete package and contribution schema.

## Start from a scaffold

```sh
topiaforge new mod example.world --template world --name "Example World" --author "You" --license AGPL-3.0-or-later --version 1.0.0
topiaforge restore --project example.world
```

Use `--template gamemode` for a gamemode factory. Keep the generated manifest, implementation
type, tests, and bundle authoring settings together. A declaration replaces imperative world
and menu registration.

Build bundles for their declared `contentTargets`. The world template's Unity bundle builder
uses `StandaloneWindows64`; declaring Linux does not turn that bundle into native Linux content.
Windows bundles may be used by the Windows game under Proton. Verify each intended host and
bundle combination before advertising support.

## Declare the selection

This contribution fragment belongs to a package named `example.world`. Its surrounding
manifest must also declare the entry assembly, Worlds dependency, bundle capability and actual
content target.

```json
{
  "contributions": {
    "worlds": [
      {
        "id": "example.world.arena",
        "name": "Example Arena",
        "content": {
          "kind": "bundle",
          "bundle": "AssetBundles/example-world.bundle",
          "prefab": "Assets/World/World.prefab"
        },
        "transitions": ["additive-arena"],
        "spawn": {
          "kind": "authored-marker",
          "markerName": "SpawnPoint"
        }
      }
    ],
    "gamemodes": [
      {
        "id": "example.world.mode",
        "name": "Example Mode",
        "implementation": {
          "type": "Example.World.ExampleGamemode"
        },
        "worldRequirements": {
          "transitions": ["additive-arena"],
          "spawn": "authored-marker"
        },
        "sceneChangePolicy": "end-session"
      }
    ],
    "launchTargets": [
      {
        "id": "example.world.play",
        "title": "Play Example World",
        "gamemode": "example.world.mode",
        "world": {
          "policy": "fixed",
          "default": "example.world.arena",
          "allowPlayerOverride": false
        },
        "transition": "additive-arena"
      }
    ]
  }
}
```

Declaration IDs use the owning package's namespace and an ASCII grammar, with a 96-character
limit. Package IDs retain their separate 64-character limit. Cross-package manifest references
require declared dependencies with satisfied versions. Implementation types bind from the
declaring package's verified assembly and must be public, concrete, and parameterless.

| World policy | Permitted selection |
| --- | --- |
| `fixed` | Only the default; player override is prohibited. |
| `list` | The default and declared `allow` entries, subject to compatibility. |
| `open` | Compatible profile worlds consenting through `openTo` or `openToAnyCompatible`, including the default. |

Absent `allowPlayerOverride` means false. A player's open-policy choice does not invent
a dependency from the target package to the selected world package. Discovered families and
instances cannot be static defaults or allow-list entries. A resolved discovered selection
records the concrete instance and its family separately.

The top-level retired `gamemodes` field remains invalid. V6 deliberately excludes `options`,
`optionValues`, and `sessionExtensions`.

## Start one owned controller

`IGamemodeFactory.StartAsync` runs after scene, content, player, and spawn readiness succeed.
Use `session.Context` for gameplay resources and dependency facades. Its lifetime exists
before activation and preserves package identity, capabilities, and dependency visibility.

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace Example.World
{
    public sealed class ExampleGamemode : IGamemodeFactory
    {
        public Task<OperationResult<IGamemodeController>> StartAsync(
            IGamemodeSession session, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            session.CancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                OperationResult<IGamemodeController>.Success(new Round(session)));
        }

        private sealed class Round : IGamemodeController
        {
            private IDisposable? update;

            public Round(IGamemodeSession session)
            {
                update = session.Context.Events.SubscribeUpdate(deltaTime =>
                {
                    if (session.CancellationToken.IsCancellationRequested) return;
                    // Advance this round using the prepared session.World.
                });
            }

            public void Dispose()
            {
                var previous = update;
                update = null;
                previous?.Dispose();
            }
        }
    }
}
```

A successful factory returns exactly one controller. Release partially constructed resources
when your own startup fails; scoped service resources also belong to session cleanup. Do not
cache a package-root context inside a controller. Keep disposal idempotent and attempt independent
cleanup even if another disposer throws.

The authoritative lifecycle is
`Idle -> Preparing -> LoadingWorld -> StartingMode -> Running -> Stopping -> Idle`.
Launch succeeds only at `Running`. Competing launches are Busy during preparation, loading,
startup, cleanup, and native drainage. Cancelling a caller does not prove the engine stopped.

Use `session.StopAsync`, `session.RestartAsync`, or `session.ReturnToMainMenuAsync` for bound
operations and inspect their results. Use their default token for self-restart or menu return;
passing the retiring session token would cancel the transition during its own teardown. An
independent caller token can cancel an admitted request. The host checks the actual caller lifetime
before admission, and a stale or stopped session cannot affect a successor. Restart revalidates
the original selection. A native scene change follows `sceneChangePolicy`: `end-session` ends
the session; `keep-controller` preserves its controller and is the default when absent.
Scene notifications never create a replacement controller.

## Provide content and discovery

Bundle declarations use the manager's built-in provider. Scoped asset services load the prefab,
and the owned instance remains until session cleanup. An `authored-marker` spawn requires
exactly one matching marker in owned content. Missing or duplicate markers fail startup.
There is no timer, camera, or zero-position substitute for readiness.

For custom content, declare `content.kind: "provider"` with `implementation.type` and implement:

```csharp
Task<OperationResult<IWorldInstance>> LoadAsync(
    IWorldLoadContext context, CancellationToken cancellationToken);
```

The returned `IWorldInstance` owns cleanup. Its `WorldReadiness` records the actual scene
instance and the resolved spawn transform applied to the player. The gamemode receives that
readiness view without world disposal authority. `provider-default` requires a validated actual
provider spawn. Honor `context.Transition` and `context.SpawnPolicy`; fail unsupported requests.

A declared `discovered` family binds an `IWorldDiscoverySource`, which also implements
`IWorldContentProvider`. Its `DiscoverAsync(IWorldDiscoveryContext, CancellationToken)` returns
immutable descriptors within `MaximumResults`. Use stable source identities rather than array
indexes or object instance IDs. Discovery cannot introduce targets or enable disabled packages.

All native scene work uses the manager's shared transition executor. Providers must not install
another dispatcher or independently return to the menu during disposal. Open Sandbox uses its
generated provider, environment, resolved native spawn, and owned kill plane. It is distinct from
the Sandbox creator gamemode. Free Play requires no Sandbox package.

Never block the game thread on world or asset tasks with `.Result`, `.Wait()`, or
`.GetAwaiter().GetResult()`. Await asynchronous work and let the engine continue its frame loop.
See [TF1008](Diagnostics.md#tf1008).

## Observe sessions and customize pause

Resolve `IWorldSessionService` through the consuming context. Read `Current` as one snapshot;
`StateChanged` follows committed changes. `WorldSessionSnapshot.Session` exposes immutable
identity and bound operations, without another package's scoped context. Use observations for
UI and diagnostics; never start or reconstruct a controller from a notification.

During `Running`, resolve `IWorldPauseMenuService` through `session.Context` and register
`WorldPauseAction` handles for that session. Destructive actions use the provider's TopiaForgeUi
confirmation. Observe asynchronous action failures and surface them to the player.

`InterceptExit` receives `WorldPauseExitContext.Session` and returns `ReturnToMainMenu` or
`Block`. Block while presenting your own confirmation. The default exit awaits the bound menu
operation and never invokes a second native exit afterward. Repeated pending clicks are Busy.
Treat `IsAvailable` and operation results as meaningful on unsupported game builds.

For modal gameplay holds, construct `GameplayPause` with `session.Context`. Chronos's
`ITimeControlService.AsPauseSource()` supplies a preferred freeze; without it the helper can
suspend player control. Tick with `session.Context.Time.Frame.UnscaledDeltaTime`, since scaled
time stops while frozen. Track the helper on the session lifetime and dispose it with the round.

## Import local exports into a running session

`ILocalWorldService` imports `.roboworld`, `.json`, and `.json.gz` exports through the
installed game's importer. It adds owned content to an existing running session. It does not
select a target or start a controller.

| Worlds configuration | Default | Meaning |
| --- | --- | --- |
| `enableLocalWorlds` | `true` | Permit local export listing and imports. |
| `localWorldFolder` | `""` | Folder to scan; empty uses the native default. |

`ListLocalWorlds()` returns files and their native scanner errors. Resolve the service from
the caller's context, capture the intended running session, and await the result:

```csharp
var local = session.Context.RequireExtension<ILocalWorldService>();
var result = await local.ImportAsync(
    session.SessionId, requestedPath, session.CancellationToken);
if (!result.Succeeded)
    session.Context.Logger.Warn("Local import failed: " + result.ErrorMessage);
```

The manager rejects stale identities, non-running sessions, competing native work, and unauthorized
multiplayer transitions before effects. The configured folder is checked before file existence
or extension; a sibling path sharing its prefix is outside the boundary. The native loader
validates exports before mutation. An unavailable importer is a refusal, and returning from
the native import method alone does not establish success.

The adapter captures the exact importer and cleanup contract before changing native state.
It requires fresh imported data and a new owned root in the prepared scene. Temporary native
selection and asset overrides are restored immediately after the call. The exact successful
root stays owned by the session until cleanup. Late native work remains owned after cancellation;
unknown completion cannot become success through a timeout.

`RegisterAssetOverride(new WorldAssetOverride(assetId, prefab))` returns a caller-owned lease
for subsequent imports. Load the prefab through that caller's scoped asset service. Replacing
an asset ID retires its prior registration without rewriting existing imported entities.

## Migration and verification

`IWorldGamemodeService`, imperative world/menu registration, `WorldSession`, `GamemodeHost`,
and notification-driven startup are retired. Move selection into V6 contributions, startup
into `IGamemodeFactory`, and running imports into `ILocalWorldService`. The removed Sandbox
ID is not an alias for Free Play or another mode.

Package format, dependency ordering, inbox, manager logs, enablement, and restart-required behavior
are preserved. `session.Context.LocalStorage` remains installation-local; it does not imply
save synchronization or authoritative multiplayer state.

Test allocation followed by constructor/start failure, cancellation in every phase, throwing
cleanup, owner unload, stale callbacks, competing native work, and delayed completion. Exercise
generated packages through production binding as well as unit fakes. See
[Test a mod](TestingMods.md) and [Live game acceptance](LiveGameAcceptance.md).

Automated tests and metadata checks do not establish scene timing, native import behavior,
authored-marker placement, Open Sandbox geometry, or pause interaction in the game. Those remain
acceptance requirements for the exact installed build and packaged content.
