# TopiaForge Mod SDK

> New here? Follow the step-by-step walkthrough first: [YourFirstMod.md](YourFirstMod.md). This page is the
> reference.

## Install the CLI

All commands in these docs use the `topiaforge` executable from the release zip:

1. Download the release zip for your OS: `TopiaForge-windows-x64.zip`, `TopiaForge-macos-universal.zip`,
   or `TopiaForge-linux-x64.zip`.
2. Extract it. The `topiaforge` executable sits at the zip root (`topiaforge.exe` on Windows; on macOS a
   `topiaforge` shim sits beside `TopiaForge.app`).
3. Add that folder to your `PATH`.
4. Verify: `topiaforge doctor`.

Working from a source checkout instead? Run `dart pub get --enforce-lockfile` once in
`apps/topiaforge_cli`, then substitute `dart run topiaforge <command>` (from that directory) wherever these
docs say `topiaforge <command>`.

## Getting set up

**Consuming mods needs no developer tools** — install via the launcher, or `topiaforge install <package>` then
`topiaforge launch`.

To **develop** mods, validate your machine first:

- `topiaforge doctor` — audits the toolchain (.NET, Node, Unity, Git) with versions and install links, and
  reports project status. Read-only; consumer-friendly (missing dev tools are informational, not failures; pass
  `--strict` for CI).
- `topiaforge setup` — same audit plus safe auto-fixes (installs the Automerge sidecar dependencies) and clear
  guidance for anything that needs a manual install.

Only the repository-pinned **.NET SDK 10.0.301** is required to build mods. **Node.js 20+** and **Unity** are optional (UGC live-sync
authoring only). Build/pack commands fail fast with actionable guidance when the toolchain is missing.

## Platform support

The tools (CLI, launcher, loader install) run on Windows, macOS, and Linux:

- **Windows** — native game at `%LOCALAPPDATA%\Tomato Cake\launcher\Robotopia`; BepInEx injects via the
  `winhttp.dll` doorstop proxy.
- **macOS** — native game bundle at `~/Library/Application Support/Tomato Cake/launcher/Robotopia.app`;
  the launcher installs the BepInEx unix build beside the app and launches with the doorstop `DYLD` environment
  (a `run_bepinex.sh` escape hatch ships too).
- **Linux** — the game is the Windows build running under Proton/Wine. Select the game folder inside your
  prefix (no auto-detect), run Repair to install the Windows BepInEx, and launch through your usual launcher
  with `WINEDLLOVERRIDES="winhttp=n,b"`. Setting `wineCommand` in the launcher settings lets the launcher
  start the game directly.

Building mods on a machine without a game install: set `ROBOTOPIA_GAME_DIR` (or the `RobotopiaManagedDir`
MSBuild property) to a folder containing the game's managed reference assemblies.

Implement `TopiaForge.Mods.ITopiaForgeMod`:

```csharp
public sealed class MyMod : ITopiaForgeMod
{
    public void OnLoad(IModContext context)
    {
        context.Logger.Info("Loaded");
        context.Update += deltaTime => { };
        context.SceneLoaded += scene => { };
    }

    public void OnUnload()
    {
    }
}
```

Manifest fields:

- `$schema`: optional URL of the manifest JSON schema; scaffolded manifests point at
  `schemas/topiaforge.mod.schema.json` on GitHub so editors validate and autocomplete.
- `schemaVersion`: must be `3`
- `name`: stable unique package id, for example `author.gravitygun`
- `displayName`, `version`, `description`
- `author`: `{ "name": ..., "email": ..., "url": ... }` (`author` must be an object and `name` is required)
- `entryAssembly`: DLL inside the package
- `entryType`: fully qualified type implementing `ITopiaForgeMod`
- `vpmDependencies`: mods that must be enabled and loaded first, as `{ "mod.id": "version range" }`
- `dependencies` / `optionalDependencies`: list form, entries as
  `{ "id": "mod.id", "versionRange": ">=1.0.0" }` (`dependencies` entries may also set
  `"optional": true`)
- `conflicts`: mods that must not be installed/enabled together; entries as
  `{ "id": "mod.id", "versionRange": ..., "reason": ... }`
- `loadAfter`: optional soft ordering
- `supportedGameVersionRange`, `supportedLoaderVersionRange`, `supportedSdkVersionRange`: launcher-enforced
  version ranges (see [Version ranges](#version-ranges))
- `worldGamemodes`: gamemodes this mod registers, shown in the level-select menu; entries as
  `{ "id": ..., "name": ..., "description": ... }` (`id` and `name` required)
- `apiAssemblies`: DLLs (package-relative paths) exported for other mods to compile against; consumers get
  reference assemblies on `topiaforge restore`
- `category`, `tags`, `icon`, `screenshots`, `homepage`, `source`, `license`, `licenseFiles`: launcher metadata (see
  [Categories](#categories))
- `hashes`: reserved integrity metadata — `topiaforge pack` does not write it; the package-level sha256
  travels in the registry entry / lockfile instead (see [ModPackaging.md](ModPackaging.md))
- `permissions`: descriptive user-facing capability labels (see [Permissions](#permissions))

Manifest keys are canonical-only. Schema version 3 rejects retired aliases rather than silently normalizing them.

### Version ranges

All version-range fields (`vpmDependencies` values, dependency/conflict `versionRange`, and the
`supported*VersionRange` trio) share one grammar:

| Form | Example | Matches |
|---|---|---|
| any | `*` (or empty) | every version |
| exact | `1.2.3` | that version only |
| wildcard | `1.x`, `1.2.x` | any version in that line |
| comparators | `>=1.2.0 <2.0.0` | space-separated bounds; `>`, `>=`, `<`, `<=`, `=` |

Versions use SemVer 2.0 precedence. A release version sorts after its prereleases, numeric prerelease identifiers sort
numerically before alphanumeric identifiers, and build metadata (`+ci`) does not affect ordering. Wildcard ranges do
not select prereleases; a comparator set selects a prerelease only when the set explicitly names a prerelease with the
same major/minor/patch tuple.

### Permissions

`permissions` entries are descriptive, user-facing capability labels shown in the launcher. Known values:

`asset-bundles`, `filesystem`, `filesystem-watch`, `harmony-patch`, `hud`, `input`, `microphone`, `navigation`,
`network`, `particles`, `physics`, `physics-settings`, `player-control`, `player-token`, `prompt-overrides`,
`quality-settings`, `render-settings`, `robot-spawning`, `scene-management`, `time`, `ugc-livesync`,
`remote-ai`, `speech-to-text`, `world-service`

Use `remote-ai` for remote inference. Capabilities are disclosure, not a security sandbox:
every C# package executes inside the game process with the player's authority. Install/update confirmation shows the
package source and hash plus the aggregate capabilities of required dependencies. See
[PrivacyAndCapabilities.md](PrivacyAndCapabilities.md) for the sensitive-data contract and required declarations.

An unknown value is a validation **warning** — and warnings fail the zero-finding publishing bar
([PublishingYourMod.md](PublishingYourMod.md)).

### Categories

`category` is display metadata. Values in use: `Framework`, `Gameplay`, `Performance`, `Utility`,
`DevTool`. Exactly two behaviors hang off it:

- `DevTool` mods are excluded from `topiaforge pack --all` and from release payloads unless
  `--include-dev-mods` is passed.
- `Framework` mods are grouped under a **Libraries & frameworks** section at the end of the launcher's
  discovery/browse lists for non-developer users — they're usually installed automatically as dependencies
  rather than browsed for.

Loaded C# assemblies cannot be unloaded from Unity Mono, so enable, disable, update, and uninstall actions are staged and marked restart-required when needed.

## Scaffolding and manifest management from the CLI

`topiaforge new mod <id>` scaffolds a local-only project from a template (`topiaforge list templates`). The no-argument
path uses an explicit placeholder author, `NOASSERTION`, and a no-grant license notice so it cannot accidentally pass
publication. Supply `--author` and `--license` deliberately for a publishable project; non-MIT/Apache expressions also
require one or more `--license-file` inputs:

| Template | Modeled on | What you get |
|---|---|---|
| `minimal` (default) | the hello sample | Entry class logging load/scene events |
| `gameplay` | Gravity Gun | Config + per-frame controller split; `input`, `physics`, `hud` permissions |
| `gamemode` | Zombies | Worlds-service gamemode + menu entry registration; depends on `io.github.furroxide.topiaforge.worlds` + `io.github.furroxide.topiaforge.robotkit` with `loadAfter`; a `worldGamemodes` entry |
| `service` | Assets | A published `I<Name>Service` via `IModServiceRegistry`, exposed through `apiAssemblies` |
| `ui` | UI Gallery | An F8-toggled TopiaForgeUi window (references `TopiaForge.Mods.UnityUi`) |
| `asset` | asset-companion flow | `IAssetBundleService` load/spawn stub + the Unity companion project scaffolded by default |
| `world` | Sandbox | A bundle-backed world registered via the Worlds service (`RegisterWorldFromBundle`); pairs with `topiaforge world link|build|play` (see [CustomWorlds.md](CustomWorlds.md)) |

Every manifest field is settable at scaffold time — repeatable flags repeat (`--tag a --tag b`):

```powershell
topiaforge new mod author.waves --template gamemode --name "Waves" `
  --author "You" --license MIT --category Gameplay `
  --tag waves --permission hud --dependency io.github.furroxide.topiaforge.chronos@">=0.1.0" `
  --gamemode "author.waves.survival:Waves:Survive the waves." `
  --game-version-range ">=0.1.0 <0.2.0"
```

`schemaVersion` is pinned to 3 and is intentionally not a scaffold flag. (`hashes` is not one either:
`topiaforge pack` does not write it — the package-level
sha256 travels in the registry entry / lockfile instead; see [ModPackaging.md](ModPackaging.md).) Add
`--unity-companion` for the Unity authoring project, or
`--live-sync [--transport localFolder|automerge] [--watch folder]` to preconfigure UGC live sync (implies the
companion; see [UgcLiveSync.md](UgcLiveSync.md)).

After creation, manage the manifest without hand-editing JSON — every edit is validated before it is written
(same rules as `check package`, which accepts both a packed `.topiaforgemod` and an unpacked project folder):

```powershell
topiaforge mod show                                 # pretty-print manifest + validation issues
topiaforge mod set version 0.2.0                    # scalar fields (license, category, author, ranges, ...)
topiaforge mod add permission time                  # tags, permissions, load-after, screenshots, api-assemblies
topiaforge mod add dependency io.github.furroxide.topiaforge.worlds@">=0.3.0"
topiaforge mod add gamemode "my.mode:My Mode:Description"
topiaforge mod remove tag old-tag
```

Optional SDK services are available through `context.GetService<T>()`:

- `IModFileService`: safe package/data/config file paths.
- `IAssetBundleService`: package-relative AssetBundle load, typed asset lookup, SpawnAsset-style instantiation,
  asset-name listing, bundle caching, and per-mod cleanup. Published by the `TopiaForge.Assets` framework mod.
- `IPromptOverrideRegistry`: prompt override registration, deterministic effective override resolution, disposable
  registrations, owner cleanup, and conflict diagnostics. Published by the `TopiaForge.Prompts` framework mod.
- `IUgcLiveSyncService`: live UGC content sync — hot-reload level content into the running game from a watched
  export folder or an external editor's live Automerge document (published by the `TopiaForge.UgcLiveSync` mod).
- `IRobotAgentService`: spawn standard-agent robots that come up native (body, animation, native locomotion),
  start from a default and override only the behaviour/visuals you need, and access the player (position, the
  player object, damage, control suspension). Published by the `TopiaForge.RobotKit` mod. See [RobotKit.md](RobotKit.md).

These services are additive. Existing mods that only use `ITopiaForgeMod.OnLoad`, `OnUnload`, config, logging, update, and scene events remain compatible.

`context.GetService<T>()` returns `null` when a service is not registered. For cleaner call sites, the SDK adds
two extension methods on `IModContext`:

- `RequireService<T>()` — returns the service or throws a descriptive error naming `T` (for services your mod
  cannot run without).
- `TryGetService<T>(out T service)` — returns `true` and the service when registered, else `false` (for optional
  integrations).

```csharp
var robots = context.RequireService<IRobotAgentService>();            // throws if RobotKit is missing
if (context.TryGetService<IUgcLiveSyncService>(out var ugc)) { /* optional */ }
```

Asset and prompt helpers are opt-in framework services. Declare the dependency in `topiaforge.mod.json` before using
the convenience extensions:

```json
"vpmDependencies": {
  "io.github.furroxide.topiaforge.assets": ">=0.1.0",
  "io.github.furroxide.topiaforge.prompts": ">=0.1.0"
},
"loadAfter": ["io.github.furroxide.topiaforge.assets", "io.github.furroxide.topiaforge.prompts"]
```

```csharp
var bundle = context.LoadAssetBundle("AssetBundles/my-mod").Bundle;
var prefab = bundle == null ? null : context.LoadAsset<object>(bundle, "assets/prefabs/widget.prefab").Asset;
if (prefab != null) context.SpawnAsset(prefab);

var promptHandle = context.RegisterPromptOverride(
    "robot.greeting",
    "Use this replacement prompt text.",
    priority: 10,
    description: "My mod's greeting rewrite");
```

The SDK also provides Unity-free `Vec3` (x, y, z) and `RobotColor` (r, g, b, a) structs used by
vector/colour-carrying service contracts so the abstractions assembly stays free of any `UnityEngine`
reference; convert to/from `UnityEngine.Vector3`/`Color` on your side.

## In-game UI

Build branded in-game UI (windows, HUDs, modals, toasts) with the **TopiaForge UI kit** —
a loader-shipped library, not a service: add a reference to `TopiaForge.Mods.UnityUi.dll`
(no manifest dependency needed) and create a host from your context:

```csharp
var ui = TopiaForgeUi.For(context);
var window = ui.Window("settings", "MY MOD");        // draggable, ESC-closes, persists its rect
window.Content.Toggle("Enable the thing", true, v => { });
window.Content.Button("DO IT", () => ui.Toast("Done.", TopiaForgeTone.Success));
ui.Hotkey(TopiaForgeKey.F7, window.Toggle);
// OnUnload: ui.Dispose();
```

The kit renders the launcher's brand in-game (two schemes: light Paper for tools, dark HUD
for gameplay overlays), with theming, accessibility (high contrast, UI scale, reduced
motion), motion presets, virtualized lists, pooled world-anchored labels, and a strict
zero-steady-state-allocation contract for HUD updates. See [UiKit.md](UiKit.md) and the
`io.github.furroxide.topiaforge.uigallery` dev mod (F8) for the full catalog.

## UGC Live Content Sync

Author UGC levels in the Unity Editor and hot-reload them into the running game with no restart. The game side
is the `TopiaForge.UgcLiveSync` framework mod (`IUgcLiveSyncService`); the authoring side is the
`io.github.furroxide.topiaforge.ugc-companion` Unity Editor package, scaffolded by `topiaforge new mod --unity-companion`. See
[UgcLiveSync.md](UgcLiveSync.md) for the full workflow, the shared export-JSON contract, the coordinate-handedness
rule, and the security model. For the Creator-Companion (VCC-parity) experience — multi-project management, Unity
detection, project/package templates, and the VPM package manager — see [CreatorCompanion.md](CreatorCompanion.md)
and [UnityVpm.md](UnityVpm.md).

```csharp
var ugc = context.GetService<IUgcLiveSyncService>();
ugc?.StartLocalSession(new UgcLiveSyncRequest(watchFolder: @"C:\path\to\watch"));
```

## Custom Worlds

Ship a **fully custom world** — modeled in Blender, assembled in Unity, packed as a prefab in an
AssetBundle — and register it as a playable world that appears in the game's GAMEMODES menu. The
runtime seam is `IWorldGamemodeService.RegisterWorld(WorldDefinition, ICustomWorldContent)` (from
`io.github.furroxide.topiaforge.worlds >= 0.5.0`); the one-call convenience is `context.RegisterWorldFromBundle(...)`,
which wires the bundle prefab + a Sandbox-paired menu entry. The authoring loop is
`topiaforge new mod <id> --template world` + `topiaforge new unity-world <Name> --mod <modDir>` +
`topiaforge world build|play`. See [CustomWorlds.md](CustomWorlds.md) for the prefab contract
(SpawnPoint marker, no custom scripts, colliders, optional HDRP Volume) and the full walkthrough.

```csharp
var worlds = context.RequireService<IWorldGamemodeService>();
context.RegisterWorldFromBundle(worlds, new BundleWorldOptions
{
    Id = "mymod.worlds.skyisland",
    Name = "Sky Island",
    BundleRelativePath = "AssetBundles/sky-island.bundle",
});
```

## Scene Coordination

Single-mode scene loads are **last-write-wins**: if two mods dispatch loads, the later one silently replaces
the earlier one's world (the classic symptom is a flash of the first scene, then a black or empty one). The
manager publishes `ISceneCoordinator` to arbitrate this — it is always available, from the first `OnLoad`.

The rules:

- **Any mod that loads a scene asks first.** Call `RequestTransition` and only load on approval. Dispose the
  returned claim when the transition resolves (your scene arrived, or your load was abandoned).
- **Automatic triggers yield.** Anything that is not a direct user action — auto-load on start, timers, file
  watchers — passes `SceneTransitionPriority.Automatic` and is **refused** while any claim is active. Degrade
  gracefully: defer, skip, or attach when the scene arrives by other means. Never retry-loop the load.
- **User actions supersede.** A menu/overlay button passes `SceneTransitionPriority.UserInitiated` and is
  always approved. The superseded mod finds out through its own scene handling — `TopiaForge.Worlds` ends its
  session with `WorldSessionEndReason.SceneReplaced` (a clean teardown plus a log line naming the new owner)
  when a foreign claimed scene lands over it.
- **Check the world state too.** `IWorldGamemodeService.CurrentSession` tells you a world/gamemode session is
  live; the optional `IWorldTransitionState.IsTransitionInFlight` capability tells you its scene load is still
  in the air. A session holds a scene claim for its whole lifetime, so honoring the coordinator covers both
  automatically.

When starting through the framework services, pass `SceneTransitionPriority.Automatic` to the
`WorldLoadRequest` or `UgcLiveSyncRequest` constructor for automatic work. Omitting the argument keeps
direct player actions user-initiated.

```csharp
var scenes = context.RequireService<ISceneCoordinator>();
var decision = scenes.RequestTransition(new SceneTransitionRequest(
    context.ModId, "UgcPlay", SceneTransitionPriority.Automatic, "attach my content"));
if (decision.Approved)
{
    LoadMyScene();                      // however your mod loads it
    // ... on the next scene arrival:
    decision.Claim!.Dispose();          // release so automatic transitions unblock
}
else
{
    context.Logger.Info("Deferred: " + decision.Message);  // attach later, when the scene shows up
}
```

## Robots & Standard Agents

Spawn enemies, companions, or NPCs as **standard-agent robots** — clones of the game's own robot that come up
native (body, animation, native locomotion) — then start from a default and override only the behaviour and
visuals you need, without re-deriving any GameCode reflection. Movement is the game's own pathing (it routes
around geometry, re-paths as a chased target moves, and animates natively); the brain is dormant by default
(mod-driven) or autonomous (a native thinking NPC). The `TopiaForge.RobotKit` framework mod publishes
`IRobotAgentService`; declare a dependency on `io.github.furroxide.topiaforge.robotkit` (and `loadAfter` it). See
[RobotKit.md](RobotKit.md) for the full API, the behaviour/combat model, and a worked example (the
`TopiaForge.Zombies` gamemode is built on it).

```csharp
var robots = context.GetService<IRobotAgentService>();
var bot = robots?.Spawn(new RobotAgentSpawnRequest(new Vec3(x, y, z))
{
    Gait = RobotGait.Run,
    Tint = new RobotColor(0.55f, 1f, 0.35f),
});
// each frame — native locomotion path-finds, collides, grounds, animates, and re-paths to the moving player:
if (robots != null && robots.TryGetPlayerObject(out var player)) bot?.Chase(player);
```

## Performance

The `TopiaForge.Performance` mod applies runtime, fully-reversible performance levers — HDRP post-FX kills
via an injected high-priority override Volume, dynamic resolution, quality / reflection-probe forcing,
frame pacing, and telemetry throttling. It ships presets (`off` / `balanced` / `performance` / `potato`)
plus per-effect overrides in `config/topiaforge.performance.json`. It needs no other mod and touches the
game only through clean-room reflection, so a missing member degrades one lever to a no-op rather than
breaking. See [Performance.md](Performance.md) for the full preset table and config reference.

For **root-cause** fixes that cost no fidelity, the separate `TopiaForge.PerfFixes` mod applies only
**behavior-identical** optimizations — caching `Camera.main` per frame, removing per-collision GC
allocations (`reuseCollisionCallbacks` + pooled `CollisionEventProxy` dispatch) — so the game does exactly
the same work, just more cheaply. It is the right choice for "fix the stutter, don't change my graphics".
See [PerfFixes.md](PerfFixes.md).

## Publishing

Ready to ship? The pipeline is: validate to zero findings (`topiaforge check package`) → `topiaforge pack` →
host the `.topiaforgemod` at a stable https URL → `topiaforge registry add-entry` → PR to the official
registry. [PublishingYourMod.md](PublishingYourMod.md) is the step-by-step walkthrough;
[RegistryFormat.md](RegistryFormat.md) covers the source/index/entry formats and self-hosting your own
package source; [ModPackaging.md](ModPackaging.md) documents exactly what `pack` puts in the package.
