---
title: Core services
description: Use the always-available, owner-scoped TopiaForge V1 services.
---

# Core services

These owner-scoped services are the supported way to interact with Robotopia without taking a
dependency on Unity or Robotopia implementation assemblies. Every mod derives from `TopiaForgeMod`.
TopiaForge attaches a non-null `IModContext` before
calling `OnLoad()` and keeps it attached through `OnUnload()`. Lifecycle callbacks, event
subscribers, and engine-facing SDK calls run on Robotopia's main thread.

The context is owner-scoped: it already knows which mod is calling. You never pass an owner id,
package root, or global cleanup target.

## Service map

| Property | Use it for |
| --- | --- |
| `Identity` | Stable package id, display name, and complete `SemanticVersion`. |
| `Runtime` | Robotopia, loader, SDK, platform, architecture, provider versions, and unavailable-capability reasons. |
| `Logger` | Attributed debug, information, warning, and exception-chain messages. |
| `Lifetime` | Shutdown cancellation and reverse-order cleanup. |
| `Events` | Frame, fixed-frame, late-frame, and scene-load subscriptions. |
| `Files` | Bounded package-file reads and installation-local persistent data-file reads/writes without raw paths. |
| `Config` | Process-local typed defaults, validation, migrations, reset, and atomic save. |
| `LocalStorage` | Installation-local typed values that are not save-scoped or replicated. |
| `Input` | Named rebindable keyboard, mouse, and gamepad actions plus conflict reporting. |
| `Time` | Current frame, fixed-frame, and late-frame samples. |
| `Scheduler` | Main-thread next-frame, delayed, repeating, and cancellable work. |
| `LocalPlayer` | Process-local camera aim, position, health, damage/heal, and control leases. |
| `Scenes` | Active/loaded scenes, checkpoint observation, and typed scene loading. |
| `Entities` | Opaque entity transforms, bounded queries, destruction, and motion leases. |
| `Physics` | Raycasts, sphere casts, and bounded overlap queries. |
| `Interactions` | Interactable registration and current focus. |
| `Items` | Held-item observation, give, and drop operations. |
| `Assets` | Package bundle and prefab handles, then lifetime-owned spawn operations. |
| `Audio` | Framework notification cues and playback handles. Synthesized tones, not your own audio — see below. |
| `Ui` | HUDs, windows, fullscreen tools, modals, toasts, and accessibility preferences rendered by TopiaForgeUi. |
| `Localization` | Locale catalogs and fallback lookup. |
| `Commands` | Namespaced commands and invocation. |
| `Diagnostics` | Bounded structured reports mirrored to the attributed log. |
| `Extensions` | Typed providers exposed by declared dependencies. |

Locality is part of the V1 contract. `Files`, `Config`, `LocalStorage`, `Input`, `Time`,
`LocalPlayer`, `Interactions`, `Items`, `Audio`, and `Ui` describe this game process only; they do
not silently synchronize between peers. `IEntity.Id` and scene instance ids are process-local
correlation keys, not network identities. A future authoritative world-state service will be a
separate specialist contract rather than a reinterpretation of local storage. Hosts without an
interactive player or presentation surface return the usual typed `Unavailable` results.

## Start from a compiled template

The gameplay template demonstrates a named input action, player aim, a raycast, logging, and a
toast without Unity or Robotopia implementation references. This block is inserted from the same
template source that CI scaffolds, compiles, tests, packs, and validates:

<!-- topiaforge-snippet path="templates/mod/gameplay/{{TYPE_NAME}}Controller.cs" -->

## Configuration that repairs itself

A stored config document is written by hand, by the launcher, by the CLI, or by an interrupted save,
so it can hold anything the type system allows: `NaN`, negatives, inverted minimum/maximum pairs,
unknown enum strings. Implement `ISelfNormalizingConfig` on the config type and put every bound in
`Normalize()`:

```csharp
[DataContract]
public sealed class MyConfig : ISelfNormalizingConfig
{
    [DataMember(Name = "maxRange")] public float MaxRange { get; set; } = 20f;

    public void Normalize()
    {
        if (float.IsNaN(MaxRange) || MaxRange < 1f || MaxRange > 100f) { MaxRange = 20f; }
    }
}
```

`ConfigDefinition<T>` then calls `Normalize()` on every path the config service validates — defaults,
load, migration, and save — so a bounded document is part of the contract rather than something each
mod has to remember. `Normalize()` must be idempotent and must not throw: it runs precisely on values
already known to be untrustworthy. Pass a `validate` delegate only for rules normalization cannot
express, such as rejecting a document outright.

## Results and expected failures

SDK operations use `OperationResult<T>` when an unavailable binding, invalid state, conflict, or
missing content is expected. Check `Succeeded`, or use `TryGetValue(out value)` and log
`ErrorCode` plus `ErrorMessage`. `ModErrorCode` values are stable; the message is for people.
Config validators and migrators, and command handlers, use the same result type rather than a
service-specific boolean or error wrapper.

Input, localization, and scheduler registration return results too. A duplicate input action is a
`Conflict`; a registration attempted while the mod is stopping is `Cancelled`; and an unavailable
Robotopia binding is `Unavailable`. Only use a returned handle after `TryGetValue` succeeds.

Use `Try...` methods for cheap queries such as current player state or a raycast. Asynchronous
work returns `Task<OperationResult<T>>` and combines caller cancellation with
`Context.Lifetime.StoppingToken`. Exceptions are reserved for programming-contract violations,
such as a null callback or a negative scheduling interval.

Engine-facing services must be entered from a lifecycle callback, SDK event, scheduled callback,
or an SDK async continuation. If background work needs to re-enter Robotopia, queue it with
`Context.Scheduler.NextFrame` and handle its result. Calling an engine adapter directly from a worker
thread fails before touching Unity with `TFSDK100` and points to that remediation.

## Event dispatch and failure isolation

Event subscriptions are delivered in registration order from an immutable snapshot. Subscribing or
disposing while an event is being delivered affects the next delivery, not the current one. The runtime
rebuilds that snapshot only when subscriptions change, so healthy frame, fixed-frame, and late-frame
delivery adds zero steady-state allocations beyond work performed by the subscriber itself.

Every subscription has an independent failure circuit. A successful invocation resets its failure streak.
The first failure is logged with the callback method and event phase, repeated exception details are
suppressed, and a third consecutive failure logs the circuit transition and disables only that subscription.
Intermittent failures remain in the same suppressed diagnostic episode until 60 consecutive healthy callbacks
rearm one future failure log, preventing an alternating throw/success callback from flooding logs indefinitely.
Other subscribers continue in order. An opened circuit remains disabled for that subscription's lifetime;
dispose it and create a new subscription to retry deliberately. Lifetime teardown still removes both active
and disabled subscriptions.

## Scene transition semantics

The original scene callback remains available when only the loaded scene name matters. It fires
once for each completed load:

```csharp
Context.Events.SubscribeSceneLoaded((string sceneName) =>
    Context.Logger.Info("Loaded " + sceneName));
```

Use the typed overload when a mod must distinguish replacement loads, additive content, and later
activation of an additively loaded gameplay scene:

```csharp
Context.Events.SubscribeSceneLoaded((SceneLoadEvent scene) =>
{
    if (!scene.IsWorldReplacement)
    {
        return;
    }

    Context.Logger.Info("Active world: " + scene.SceneName);
});
```

`Mode` reports `Single` or `Additive`, `IsActive` reports whether that scene is currently active,
and `IsWorldReplacement` accepts every single replacement; for additive transitions it
accepts an activated gameplay scene while filtering loader/menu overlays. Detailed subscribers can
therefore observe an additive scene once when it loads and again if it later becomes active. The extension
falls back to `Single` plus active metadata on older hosts, and both overloads return
lifetime-owned subscriptions. SDK scene-load continuations resume on Robotopia's main thread; do
not opt out with `ConfigureAwait(false)` before making another engine-facing call.

Use the complete lifecycle stream when teardown or duplicate additive scene names matter:

```csharp
Context.Events.SubscribeSceneLifecycle(scene =>
{
    Context.Logger.Debug($"{scene.SceneName} #{scene.SceneInstanceId}: {scene.Phase}");
});
```

`Phase` is `Loaded`, `Activated`, or `Unloaded`. `SceneInstanceId` is a process-local correlation key,
so equal scene names loaded additively can still be distinguished; never persist it. `Mode` retains the
original mode for native transitions. Startup history is unavailable, so initial replay normalizes the active
snapshot to `Single` and background snapshots to `Additive`. `IsActive` describes state after the transition,
and `IsInitial` marks startup replay of a scene that was already loaded when TopiaForge became ready.
Already-loaded background additive scenes receive
lifecycle-only `Loaded` notifications before the active scene retains its legacy/detailed load callback and receives
`Loaded` then `Activated` lifecycle notifications. A scene reported active at load is likewise normalized as
`Loaded` then `Activated`.
When Unity publishes `sceneLoaded` before `activeSceneChanged`, `Activated` follows only after the native
activation. The startup replay and its native echo are deduplicated. Simpler/older event hosts can provide a
load-only fallback with instance id zero; unload-aware mods should check host support through
`ISceneLifecycleEventSource` when that distinction is mandatory.

## Lifetime ownership

Every SDK subscription, registration, asset handle, spawned entity, UI surface, playback, lease,
and scheduled operation is automatically owned by the current mod lifetime. Handles remain
disposable when you want to release them early.

Use `Context.Lifetime.Track(resource)` for your own `IDisposable` and
`Context.Lifetime.Defer(action)` for a custom cleanup callback. Cleanup is idempotent and runs in
reverse registration order after normal unload and partial-load failure. Keep `OnUnload()` only
for non-SDK state that cannot be tracked at acquisition time.

## Input and UI focus

Register an `InputActionDefinition` with a stable action name and friendly label. Read
`WasPressed`, `IsHeld`, `WasReleased`, or `Value` from its handle. Bindings are rebindable and
conflicts are available from `Context.Input.GetConflicts()`.

Use `InputMouseButton`, `InputGamepadButton`, `InputAxis`, and `InputGamepadAxis` when creating
non-keyboard bindings. These SDK names describe physical position or purpose, so mods never depend
on Unity button ordinals or platform-specific native enums. Keyboard bindings use portable SDK key
names such as `F`, `Space`, or `F8`.

Gameplay actions are suppressed while text entry or another framework UI surface owns focus.
Use the configurable action in the `ui` template rather than reserving a function key globally.
F5 routes the shared Creator workbench, F8 opens UiGallery in development installs, and F10 opens
the manager; ordinary mods must not claim those defaults.

## UI accessibility

`Context.Ui.Accessibility` reports the effective UI scale, high-contrast state, reduced-motion
state, and motion intensity. Apply player-configurable values with
`Context.Ui.ApplyAccessibility(new UiAccessibilityPreferences(...))` and handle the returned
`OperationResult`. TopiaForgeUi propagates the effective values to every safe HUD, window,
fullscreen tool, modal, and toast; consumer mods do not maintain a separate theme or animation
system.

## Audio, interactions, and items

`Context.Audio` plays **synthesized notification tones**, not sampled audio. The tone is picked from intent
words in the cue id — `danger`, `failure`, `warning`, `success`, `confirm` — and any other id produces a
stable derived tone rather than an error:

```csharp
Context.Audio.Play(new AudioPlayRequest("mymod.success", 0.8f));   // a confirmation beep
```

This matters because an unrecognised id still returns success. A mod that ships `gunshot.ogg` and calls
`Play("mymod.gunshot")` gets `Succeeded == true` and hears a beep. To use your own audio, put an
`AudioSource` on a prefab in your package's asset bundle and spawn it through `Context.Assets`:

```csharp
// Drive both loads from PendingOperation<T> and poll them from your update; never wait on the task.
var spawned = Context.Assets.Spawn(new AssetSpawnRequest(
    emitterPrefab,
    new TransformState(muzzlePosition, Quat.Identity, Vec3.One)));
```

`Context.Interactions` registers an interactable on an entity you already own and reports what the player
is currently focused on, so a mod can add its own prompt without touching the game's interaction system:

```csharp
var terminal = Context.Interactions.Register(
    terminalEntity,
    new InteractableDefinition("Use requisitions terminal", maximumDistance: 3f),
    _ => shop.Open());
if (terminal.TryGetValue(out var registration))
{
    // Lifetime-owned; dispose the registration to remove the prompt early.
}
```

`Context.Items` observes the held item and performs give and drop operations. Both are process-local: they
describe this game process and do not synchronize between peers.

## Typed math and opaque entities

Safe contracts use `Vec2`, `Vec3`, `Quat`, `Ray`, `Bounds`, and SDK color values. Entity and
asset interfaces are opaque handles with ordinary state and lifetime operations. These types keep
mods portable across supported runtimes and make tests deterministic.

See [specialist modules](Modules.md) for creator content, robots, worlds, time control, prompts,
UGC, and multiplayer, or open the generated C# reference from the developer site for every member
and default value.
