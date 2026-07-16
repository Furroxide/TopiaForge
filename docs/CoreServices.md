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
| `Files` | Bounded package-file reads and persistent data-file reads/writes without raw paths. |
| `Config` | Typed defaults, validation, migrations, reset, and atomic save. |
| `Storage` | Typed save-scoped values and mod-owned story flags. |
| `Input` | Named rebindable keyboard, mouse, and gamepad actions plus conflict reporting. |
| `Time` | Current frame, fixed-frame, and late-frame samples. |
| `Scheduler` | Main-thread next-frame, delayed, repeating, and cancellable work. |
| `Player` | Camera aim, position, health, damage/heal, and control leases. |
| `Scenes` | Active/loaded scenes, checkpoint observation, and typed scene loading. |
| `Entities` | Opaque entity transforms, bounded queries, destruction, and motion leases. |
| `Physics` | Raycasts, sphere casts, and bounded overlap queries. |
| `Interactions` | Interactable registration and current focus. |
| `Items` | Held-item observation, give, and drop operations. |
| `Assets` | Package bundle and prefab handles, then lifetime-owned spawn operations. |
| `Audio` | Framework audio cues and playback handles. |
| `Ui` | HUDs, windows, modals, toasts, and accessibility preferences rendered by TopiaForgeUi. |
| `Localization` | Locale catalogs and fallback lookup. |
| `Commands` | Namespaced commands and invocation. |
| `Diagnostics` | Bounded structured reports mirrored to the attributed log. |
| `Extensions` | Typed providers exposed by declared dependencies. |

## Start from a compiled template

The gameplay template demonstrates a named input action, player aim, a raycast, logging, and a
toast without Unity or Robotopia implementation references. This block is inserted from the same
template source that CI scaffolds, compiles, tests, packs, and validates:

<!-- topiaforge-snippet path="templates/mod/gameplay/{{TYPE_NAME}}Controller.cs" -->

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

## UI accessibility

`Context.Ui.Accessibility` reports the effective UI scale, high-contrast state, reduced-motion
state, and motion intensity. Apply player-configurable values with
`Context.Ui.ApplyAccessibility(new UiAccessibilityPreferences(...))` and handle the returned
`OperationResult`. TopiaForgeUi propagates the effective values to every safe HUD, window, modal,
and toast; consumer mods do not maintain a separate theme or animation system.

## Typed math and opaque entities

Safe contracts use `Vec2`, `Vec3`, `Quat`, `Ray`, `Bounds`, and SDK color values. Entity and
asset interfaces are opaque handles with ordinary state and lifetime operations. These types keep
mods portable across supported runtimes and make tests deterministic.

See [specialist modules](Modules.md) for robots, worlds, time control, prompts, and UGC, or open
the generated C# reference from the developer site for every member and default value.
