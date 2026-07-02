# QuantumWorks Mod SDK

## Getting set up

**Consuming mods needs no developer tools** — install via the launcher, or `robotopia install <package>` then
`robotopia launch`.

To **develop** mods, validate your machine first:

- `robotopia doctor` — audits the toolchain (.NET, Node, Unity, Git) with versions and install links, and
  reports project status. Read-only; consumer-friendly (missing dev tools are informational, not failures; pass
  `--strict` for CI).
- `robotopia setup` — same audit plus safe auto-fixes (installs the Automerge sidecar dependencies) and clear
  guidance for anything that needs a manual install.

Only the **.NET SDK 8+** is required to build mods. **Node.js 20+** and **Unity** are optional (UGC live-sync
authoring only). Build/pack commands fail fast with actionable guidance when the toolchain is missing.

Implement `Robotopia.Mods.IRobotopiaMod`:

```csharp
public sealed class MyMod : IRobotopiaMod
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

- `schemaVersion`: must be `1`
- `id`: stable unique id, for example `author.gravitygun`
- `name`, `version`, `author`, `description`
- `entryAssembly`: DLL inside the package
- `entryType`: fully qualified type implementing `IRobotopiaMod`
- `dependencies`: mods that must be enabled and loaded first
- `optionalDependencies`: optional integrations
- `conflicts`: mods that must not be installed/enabled together
- `loadAfter`: optional soft ordering
- `supportedGameVersionRange`, `supportedLoaderVersionRange`: launcher-enforced version ranges
- `category`, `tags`, `icon`, `screenshots`, `homepage`, `source`, `license`, `hashes`: launcher metadata
- `permissions`: descriptive only in v1

Loaded C# assemblies cannot be unloaded from Unity Mono, so enable, disable, update, and uninstall actions are staged and marked restart-required when needed.

Optional SDK services are available through `context.GetService<T>()`:

- `IModFileService`: safe package/data/config file paths.
- `IAssetBundleService`: package-relative AssetBundle load, typed asset lookup, SpawnAsset-style instantiation,
  asset-name listing, bundle caching, and per-mod cleanup. Published by the `Robotopia.Assets` framework mod.
- `IPromptOverrideRegistry`: prompt override registration, deterministic effective override resolution, disposable
  registrations, owner cleanup, and conflict diagnostics. Published by the `Robotopia.Prompts` framework mod.
- `IUgcLiveSyncService`: live UGC content sync — hot-reload level content into the running game from a watched
  export folder or an external editor's live Automerge document (published by the `Robotopia.UgcLiveSync` mod).
- `IRobotAgentService`: spawn standard-agent robots that come up native (body, animation, native locomotion),
  start from a default and override only the behaviour/visuals you need, and access the player (position, the
  player object, damage, control suspension). Published by the `Robotopia.RobotKit` mod. See [RobotKit.md](RobotKit.md).

These services are additive. Existing mods that only use `IRobotopiaMod.OnLoad`, `OnUnload`, config, logging, update, and scene events remain compatible.

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

Asset and prompt helpers are opt-in framework services. Declare the dependency in `robotopia.mod.json` before using
the convenience extensions:

```json
"dependencies": [
  { "id": "robotopia.assets", "versionRange": ">=0.1.0" },
  { "id": "robotopia.prompts", "versionRange": ">=0.1.0" }
],
"loadAfter": ["robotopia.assets", "robotopia.prompts"]
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

Build branded in-game UI (windows, HUDs, modals, toasts) with the **QuantumWorks UI kit** —
a loader-shipped library, not a service: add a reference to `Robotopia.Mods.UnityUi.dll`
(no manifest dependency needed) and create a host from your context:

```csharp
var ui = QwUi.For(context);
var window = ui.Window("settings", "MY MOD");        // draggable, ESC-closes, persists its rect
window.Content.Toggle("Enable the thing", true, v => { });
window.Content.Button("DO IT", () => ui.Toast("Done.", QwTone.Success));
ui.Hotkey(QwKey.F7, window.Toggle);
// OnUnload: ui.Dispose();
```

The kit renders the launcher's brand in-game (two schemes: light Paper for tools, dark HUD
for gameplay overlays), with theming, accessibility (high contrast, UI scale, reduced
motion), motion presets, virtualized lists, pooled world-anchored labels, and a strict
zero-steady-state-allocation contract for HUD updates. See [UiKit.md](UiKit.md) and the
`robotopia.uigallery` dev mod (F8) for the full catalog.

## UGC Live Content Sync

Author UGC levels in the Unity Editor and hot-reload them into the running game with no restart. The game side
is the `Robotopia.UgcLiveSync` framework mod (`IUgcLiveSyncService`); the authoring side is the
`com.robotopia.ugc-companion` Unity Editor package, scaffolded by `robotopia new mod --unity-companion`. See
[UgcLiveSync.md](UgcLiveSync.md) for the full workflow, the shared export-JSON contract, the coordinate-handedness
rule, and the security model. For the Creator-Companion (VCC-parity) experience — multi-project management, Unity
detection, project/package templates, and the VPM package manager — see [CreatorCompanion.md](CreatorCompanion.md)
and [UnityVpm.md](UnityVpm.md).

```csharp
var ugc = context.GetService<IUgcLiveSyncService>();
ugc?.StartLocalSession(new UgcLiveSyncRequest(watchFolder: @"C:\path\to\watch"));
```

## Robots & Standard Agents

Spawn enemies, companions, or NPCs as **standard-agent robots** — clones of the game's own robot that come up
native (body, animation, native locomotion) — then start from a default and override only the behaviour and
visuals you need, without re-deriving any GameCode reflection. Movement is the game's own pathing (it routes
around geometry, re-paths as a chased target moves, and animates natively); the brain is dormant by default
(mod-driven) or autonomous (a native thinking NPC). The `Robotopia.RobotKit` framework mod publishes
`IRobotAgentService`; declare a dependency on `robotopia.robotkit` (and `loadAfter` it). See
[RobotKit.md](RobotKit.md) for the full API, the behaviour/combat model, and a worked example (the
`Robotopia.Zombies` gamemode is built on it).

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

The `Robotopia.Performance` mod applies runtime, fully-reversible performance levers — HDRP post-FX kills
via an injected high-priority override Volume, dynamic resolution, quality / reflection-probe forcing,
frame pacing, and telemetry throttling. It ships presets (`off` / `balanced` / `performance` / `potato`)
plus per-effect overrides in `config/robotopia.performance.json`. It needs no other mod and touches the
game only through clean-room reflection, so a missing member degrades one lever to a no-op rather than
breaking. See [Performance.md](Performance.md) for the full preset table and config reference.

For **root-cause** fixes that cost no fidelity, the separate `Robotopia.PerfFixes` mod applies only
**behavior-identical** optimizations — caching `Camera.main` per frame, removing per-collision GC
allocations (`reuseCollisionCallbacks` + pooled `CollisionEventProxy` dispatch) — so the game does exactly
the same work, just more cheaply. It is the right choice for "fix the stutter, don't change my graphics".
See [PerfFixes.md](PerfFixes.md).
