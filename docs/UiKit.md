---
title: In-game UI
description: Build accessible Robotopia HUDs, windows, and fullscreen tools through the safe V1 TopiaForgeUi service.
---

# In-game UI

`TopiaForge.Mods.UnityUi` is the in-game UI framework for TopiaForge mods running in Robotopia and
for the mod manager. Ordinary mods create every HUD, window, fullscreen tool, modal, and toast
through `Context.Ui`. The service is always present, owner-scoped, rendered by TopiaForgeUi, and
automatically tied to the mod lifetime.
Do not create an independent canvas, event system, sorting order, cursor controller, or global
hotkey.

Start with the compiled `ui` template:

```sh
topiaforge new mod example.ui --template ui --name "Example UI" --author "You" --license AGPL-3.0-or-later --version 1.0.0
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
- `FullscreenTool` for immersive Paper-scheme tools that fill the safe screen area;
- `Modal` only for a decision that blocks the current flow; and
- `Context.Ui.ShowToast(...)` for short action results.

Feature-detect `IUiSurfaceDismissalSource` when a tool needs to distinguish a hidden surface from
an ended session. Its `Dismissed` event fires when a visible surface is hidden by the user or
through `Hide()`, but not when the surface is disposed. Keep teardown on the owning lifetime rather
than treating every dismissal as disposal.

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
- `UiVirtualList` pools a bounded data set and sends a stable item id;
- `UiSplitPane` lays out two bounded subtrees horizontally or vertically; and
- `UiGraphCanvas` presents bounded typed nodes, ports, edges, selection, editing, and pan/zoom.

Interactive controls require stable ids unique within the tree. Choices and list items are copied
when constructed, duplicate ids are rejected before rendering, and depth/node/data bounds protect
Robotopia from accidentally unbounded compositions. An individual callback failure is logged and
isolated, so later subscribers still run. Callbacks stop when the surface or mod lifetime ends.

Graph connections are accepted only from an output port to a same-type input port. A canvas accepts
at most 512 nodes, 1,024 edges, and 32 ports per node; its callbacks report requested changes, so
the mod remains responsible for validating and publishing the next immutable tree.

The compiled `ui` template demonstrates the standard form/list controls, accessibility changes,
result handling, and a configurable nonreserved input action. The
The [Sandbox](Sandbox.md) workbench is the task-oriented reference for fullscreen split-pane
and graph composition. Prefer updating the body for small status changes; use `SetContent` when the
control structure itself changes.

## Input and focus

Register a descriptive action through `Context.Input` and let users rebind it. Do not reserve F5,
F8, or F10: those belong to the shared Creator workbench, UiGallery, and manager respectively.
Gameplay input is suppressed while text entry or another framework surface owns focus.

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

`FakeUiService` captures each immutable tree. `FakeUiSurface` can find controls by id; invoke button,
toggle, slider, text, dropdown, and list interactions; select or move graph nodes; connect or remove
graph edges; change the graph viewport; inspect captured values; and report isolated callback
failures. It also implements `IUiSurfaceDismissalSource`, so hide/reopen session behavior is
deterministic without Robotopia. Disposing the test context removes every surface and gates further
callbacks; finish UI tests with `context.AssertNoLeaks()`.

## Provider implementation contributors

The internal renderer lives in `src/TopiaForge.Mods.UnityUi` and the F8 UI Gallery remains its manual
QA catalog. Provider work follows the repository's dirty-setter, pooling, virtualization, theme,
layer-band, and no-steady-state-allocation rules. Those implementation details are intentionally
absent from the safe consumer contract.

## Multiplayer kit widgets

These renderer widgets are the building blocks for multiplayer screens such as the scoreboard, chat,
voice settings, the connecting overlay and name tags. They are not part of the safe `Context.Ui`
contract. Their math and state live in the Unity-free `Core/` folder and are covered by
`UiKitMultiplayerCoreTests`, including a check that the per-frame paths allocate nothing. The UI
Gallery's ONLINE page shows each one in its states.

### Sortable table (`TopiaForgeTableView<T>`)

`container.TableView(columns)` adds sortable headers over the virtualized list view. Build columns
with `TopiaForgeTableColumn<T>.Text`, `Number` or `Custom`, then chain `Width` or `Flex`, `Align`,
and `Sortable`, `SortBy` or `DescendingFirst`. A header click sorts by that column, a second click
flips the direction, and a chevron marks both. Sorting is stable and never reorders the caller's
list. Selection reports the caller's index and follows its row through re-sorts; `Keyed` makes it
follow the row through new data too. Rows are the list view's buttons, so focus and keyboard
behaviour match the list view, and `MoveSelection` is the Up/Down hook. Text cells are plain text,
so markup in a player name is shown literally.

### Signal bars (`TopiaForgeSignalBars`)

A connection glyph with zero to four bars and an unknown state drawn as flat stubs, so unknown
differs from no bars by shape. `SetPing` maps a round trip through `TopiaForgeSignalThresholds` (60,
120, 200 and 350 ms by default), `SetQuality` takes a normalized quality, and `SetLevel` takes a
bucketed level, so callers translate their own quality type. `ShowMilliseconds` adds a ping label
that is rewritten only when the value changes.

### Phase stepper (`TopiaForgePhaseStepper`)

`PhaseStepper(...)` and `VerticalPhaseStepper(...)` draw a step rail in which each step is pending,
active, done, failed or skipped. `Start`, `Advance`, `MoveTo`, `Fail`, `Skip`, `Complete` and `Reset`
follow `TopiaForgePhaseStepperState`, and `SetDetail` and `SetProgress` fill the line under a step
("Relay", "3/7"). Status reads without color: a number, a check, a cross or a chevron. The active
node breathes unless motion is reduced.

### Chat log (`TopiaForgeChatLog`)

A bounded HUD log that evicts its oldest line. Every line passes through `TopiaForgePlainText`:
rich-text tags, escapes, line breaks and bidirectional overrides cannot restyle or spoof the log, and
lines are bounded (300 characters by default, author names 32). While closed, the newest lines (eight
by default) show for 8 seconds and then fade over 1.5; reduced motion hides them at once instead.
`SetOpen(true)` shows the whole history on a backdrop with scrollback. Lines are pooled, so a new
message rebinds one line. Tones mark player, system, whisper and error lines.

### Talk indicator (`TopiaForgeTalkIndicator`)

A microphone glyph for idle, talking, muted, muted by host, mic busy and no mic. Each state differs in
glyph or halo (`TopiaForgeTalkPresentation`), and `ShowCaption` names it in text. The talking halo
breathes with the host's motion intensity and holds still under reduced motion; its driver runs only
while it breathes. `HideWhenIdle` suits name tags and scoreboard rows.

### Level meter (`TopiaForgeLevelMeter`)

`SetLevel(rms, peak)` takes linear samples every frame and maps them to dBFS through
`TopiaForgeLevelMath` across `Range` (-60 to 0 dB by default), rising instantly and falling smoothly.
`SetThreshold` draws the voice-activation marker and `PeakHold` the falling peak mark. The fill tone
follows the zone: muted below the threshold, success when active, warning when hot and danger when
clipping.

### Name tags (`TopiaForgeNametagLayer`)

`hud.Nametags()` tracks persistent world-space tags: unlike floaters, a tag stays until it is removed.
`Track(anchor, name)` follows any `ITopiaForgeNametagAnchor`, such as `TopiaForgeWorldPointAnchor` (an
engine-independent point) or `TopiaForgeTransformAnchor`, plus a vertical offset. Each frame the layer
projects anchors with the camera's matrices (`TopiaForgeCameraFrame`), hides tags behind the camera,
offscreen or out of range, and fades and scales the rest with `TopiaForgeNametagFalloff` (hidden
within 0.6 m and beyond 40 m by default). A handle sets the name, a secondary line, a badge such as
HOST, and the talking glyph slot. Names are plain text, and a removed handle stays inert even after
its pooled tag is reused.

See [Core services](CoreServices.md#ui-accessibility) for the surrounding context and
[Test a mod](TestingMods.md) for `FakeUiService` assertions.
