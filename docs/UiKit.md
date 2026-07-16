---
title: In-game UI
description: Build accessible Robotopia HUDs and windows through the safe V1 TopiaForgeUi service.
---

# In-game UI

`TopiaForge.Mods.UnityUi` is the in-game UI framework for TopiaForge mods running in Robotopia and
for the mod manager. Ordinary mods create every HUD, window, modal, and toast through `Context.Ui`. The service is
always present, owner-scoped, rendered by TopiaForgeUi, and automatically tied to the mod lifetime.
Do not create an independent canvas, event system, sorting order, cursor controller, or global
hotkey.

Start with the compiled `ui` template:

```sh
topiaforge new mod example.ui --template ui --name "Example UI" --author "You" --license MIT --version 1.0.0
```

The template source lives at `templates/mod/ui/` and is built, tested, packed, and validated in CI.
It registers a named rebindable input action, creates an `IUiSurface` with
`Context.Ui.CreateSurface(...)`, and handles `OperationResult` without taking a native dependency.

## Surfaces

Create an immutable `UiSurfaceRequest` with a stable id, title, body, `UiSurfaceKind`, bounded size,
and an optional `UiNode` composition. The returned `IUiSurface` can show, hide, update its
dirty-checked body text with `SetBody`, and atomically replace its composition with `SetContent`.
Dispose it for early release; normal unload and failed-load cleanup are automatic.

Use:

- `Hud` for compact presentation-only gameplay information that must not capture text focus;
- `Window` for movable tools and settings;
- `Modal` only for a decision that blocks the current flow; and
- `Context.Ui.ShowToast(...)` for short action results.

Destructive actions require a destructive modal and explicit confirmation. Long content belongs in
a scrollable or virtualized provider surface. Gameplay overlays should avoid steady-state allocation;
update only when their displayed value changes.

## Safe declarative controls

The V1 authoring contract is immutable and Unity-free. Compose `UiNode` values and pass the root to
`UiSurfaceRequest.Content`:

- `UiText` uses semantic style and tone;
- `UiColumn` and `UiRow` arrange bounded child sequences;
- `UiScroll` owns long composition content in a bounded scaled-unit viewport;
- `UiButton`, `UiToggle`, and `UiSlider` expose ordinary typed callbacks;
- `UiTextInput` bounds entered text and cooperates with framework input suppression;
- `UiDropdown` sends a stable choice value rather than a native index; and
- `UiVirtualList` pools a bounded data set and sends a stable item id.

Interactive controls require stable ids unique within the tree. Choices and list items are copied
when constructed, duplicate ids are rejected before rendering, and depth/node/data bounds protect
Robotopia from accidentally unbounded compositions. An individual callback failure is logged and
isolated, so later subscribers still run. Callbacks stop when the surface or mod lifetime ends.

The compiled `ui` template demonstrates every control family, accessibility changes, result
handling, and a configurable nonreserved input action. Prefer updating the body for small status
changes; use `SetContent` when the control structure itself changes.

## Input and focus

Register a descriptive action through `Context.Input` and let users rebind it. Do not reserve F8 or
F10: those belong to framework development/manager surfaces. Gameplay input is suppressed while
text entry or another framework surface owns focus.

## Accessibility

`Context.Ui.Accessibility` exposes the effective `UiAccessibilityPreferences`:

- `UiScale` for user-selected scaling;
- `HighContrast` for stronger tonal separation;
- `ReducedMotion` for removing nonessential movement; and
- `MotionIntensity` for scaling remaining motion.

Apply mod-configurable preferences with `Context.Ui.ApplyAccessibility(...)` and use the returned
effective value. TopiaForgeUi propagates it across all owned surfaces. Text and action labels must
remain meaningful without color or animation, and keyboard navigation/focus must work at every
supported scale.

## Themes and layout

TopiaForgeUi chooses Paper tokens for full-screen tools, windows, and dialogs, and HUD tokens for
gameplay overlays. Safe consumers specify semantic tone (`Neutral`, `Success`, `Warning`, or
`Danger`) rather than color literals. The provider owns layer bands, focus order, cursor state,
font fallback, motion, high contrast, and UI scale.

## Testing UI without running Robotopia

`FakeUiService` captures each immutable tree. `FakeUiSurface` can find controls by id, invoke button,
toggle, slider, text, dropdown, and list interactions deterministically, inspect their current
values, and report isolated callback failures. Disposing the test context removes every surface and
gates further callbacks; finish UI tests with `context.AssertNoLeaks()`.

## Provider implementation contributors

The internal renderer lives in `src/TopiaForge.Mods.UnityUi` and the F8 UI Gallery remains its manual
QA catalog. Provider work follows the repository's dirty-setter, pooling, virtualization, theme,
layer-band, and no-steady-state-allocation rules. Those implementation details are intentionally
absent from the safe consumer contract.

See [Core services](CoreServices.md#ui-accessibility) for the surrounding context and
[Test a mod](TestingMods.md) for `FakeUiService` assertions.
