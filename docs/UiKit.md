---
title: In-game UI
description: Build accessible Robotopia HUDs, windows, and fullscreen tools through the safe V1 TopiaForgeUi service.
---

# In-game UI

`TopiaForge.Mods.UnityUi` is the in-game UI framework for TopiaForge mods running in Robotopia and
for the mod manager. Ordinary mods create every HUD, window, fullscreen tool, modal, and toast
through `Context.Ui`. The service is always present, owner-scoped, rendered by TopiaForgeUi, and
tied to the lifetime of the mod, or the scope, that uses it: scene loads never end a surface.
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

### Lifetime and visibility

Every surface and modal lives as long as the mod, or the scope, that created it. Scene loads never
end one, so a HUD can be made once in `OnLoad`, before the game's first scene, and a window made in a
world is still there after the next world loads, open or closed as the mod left it. A surface that a
gamemode session's scope creates ends with that session. No SDK surface ends with a scene: a mod
cannot see a scene change coming, and every flow it builds, a world session included, already has a
lifetime of its own. A modal stays open across scene loads until it is answered or closed, or its mod
or scope ends, which counts as cancelling.

A HUD shows only in gameplay by default (`UiHudVisibility.Gameplay`). TopiaForge hides it at the main
menu and while any scene loads, and shows it again in the next world, so the mod handles no scenes.
`IsVisible` reports what the player sees: it is false while TopiaForge keeps the HUD hidden, and a
`Show()` then makes it appear with gameplay. TopiaForge's hiding never raises `Dismissed`. A HUD that
should also show at the main menu opts out:

```csharp
var help = Context.Ui.CreateSurface(new UiSurfaceRequest(
    "help", "My mod", "Press K in a world.", UiSurfaceKind.Hud, 260f, 96f)
    .WithHudVisibility(UiHudVisibility.Always));
```

Windows and fullscreen tools show exactly while the mod shows them.

If the game or another mod destroys a surface's canvas anyway, the surface says so: it is never
visible again, a visible one raises `Dismissed`, its id is free for a new surface, and the mod's log
names it. Create the surface again to show it. A modal on such a canvas completes as cancelled.

### Where HUDs dock

In every level Robotopia draws its microphone label at the top left, a development build's version at
the top, a level's objective at the top right (above every mod's UI), a crosshair at the centre, and
dialogue subtitles across the middle of the bottom edge. A HUD docks by default where none of those
are: at the bottom left for a gameplay HUD, above a subtitle reserve covering the bottom 12% of the
screen, and at the middle of the left edge for a HUD shown
everywhere, which also keeps clear of the main menu's buttons at the bottom left. All HUD panels docked
to a bottom corner or edge share the subtitle reserve, so their stack stays together; the reserve follows
the screen size independently of UI scale. `WithHudAnchor`
picks another place: any corner, or the middle of any edge (`UiHudAnchor`), never the centre.

HUDs docked at one place stack a small gap apart, the first created nearest the edge, so HUDs from
different mods never overlap, and a hidden HUD gives its space up. A HUD keeps its requested size.
Its title stays on one line, and its body fills the panel inside its padding, wrapping only past the
panel's inner width: a 260-unit HUD wraps body text at 224 units, whatever the UI scale.

Toasts stack upwards from the bottom-right corner, clear of the level objective, above every HUD,
window and dialog.

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

Surfaces own the input they handle, with no code in the mod:

- **Back**, Escape or a controller's B, dismisses only the topmost surface. The game sees nothing of
  that press, and a key still held stays the surface's until it is released. The next press, with
  no surface left, reaches the game as before, so its pause menu opens.
- **Menu navigation**, the arrows, Tab, Enter and Space, and a controller's left stick, D-pad, A and
  B, belongs to a modal, a fullscreen tool and the manager overlay while they show. The menu beneath
  never moves or presses. The surface's own controls move and submit at the UI module's own repeat
  delay and rate, and the first press with none of them selected selects the first. A window takes
  navigation only while one of its controls is selected, so gameplay keeps Space and the stick
  otherwise.
- **The keyboard** belongs to a text field while it has focus, and to a key capture while it waits.
  Typing reaches the field and none of it the game; Escape cancels a capture without closing the
  window around it.
- **Everything else passes through unchanged**: the mouse, every key no surface handles, and the
  game's own controls.

A mod's action yields to the UI the same way, control by control (`suppressWhileUiFocused`, the
default): to every control while a modal, a fullscreen tool, the manager, a key capture or a text
field holds the focus; to the keys and buttons an open window handles; to every key and controller
control while the game's own UI holds the selection, since it may be typing; and, for mouse controls,
while the pointer is over UI. Its own key still reaches it at the main menu and over its own window.

TopiaForge takes these presses from the Input System, which the game's menus, pause and navigation
read. Code that reads Unity's legacy input still sees every key.

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

On Paper, labels and ghost buttons read in every tone on every surface. Muted text keeps 7:1 (WCAG
AAA) against the sunken paper a tool's content sits on. Faint text, and body text in a status or
brand tone, keep 4.5:1 (AA) against the darkest surface text sits on: the tint, or sunken paper
under the kit's pressed tint. High contrast raises all of them to 7:1. Status and brand colors keep
their own values for fills, rings, badges and display type, so a `Warning` label reads darker than
a warning badge.

Toasts reuse a few pooled views. Each toast is placed and brought in afresh, whatever its view
showed before and under reduced motion too, and shows above every window and dialog, stacked up from
the bottom-right corner.

## Testing UI without running Robotopia

`FakeUiService` captures each immutable tree. `FakeUiSurface` can find controls by id; invoke button,
toggle, slider, text, dropdown, and list interactions; select or move graph nodes; connect or remove
graph edges; change the graph viewport; inspect captured values; and report isolated callback
failures. It also implements `IUiSurfaceDismissalSource`, so hide/reopen session behavior is
deterministic without Robotopia. Disposing the test context removes every surface and gates further
callbacks; finish UI tests with `context.AssertNoLeaks()`.

`FakeUiSurface.IsVisible` follows the fake scenes as the game does: a gameplay HUD is visible only
while a gameplay scene is active and no scene load is pending. `FakeModContext` starts at the main
menu, so load a world (`context.Scenes.Load(...)`) before expecting a gameplay HUD on screen.
`IsShown` says whether the mod shows the surface, whatever TopiaForge does with it.

## Provider implementation contributors

The internal renderer lives in `src/TopiaForge.Mods.UnityUi` and the F8 UI Gallery remains its manual
QA catalog. Provider work follows the repository's dirty-setter, pooling, virtualization, theme,
layer-band, and no-steady-state-allocation rules. Those implementation details are intentionally
absent from the safe consumer contract.

A kit surface says what it takes when it joins the dismiss stack:
`TopiaForgeDismissStack.Push(surface, claim, root)`. The input filter looks at each keyboard and
controller event before any Input System action does. It holds a claimed press at rest in that event,
and in every later one until the control is released, and the kit reads the press itself. It changes
no action map, UI module or binding of the game's, so nothing has to be restored, and with no claim
every event passes at once. A surface whose root is destroyed or hidden claims nothing. A taken press
never reaches the device, so first-party code that reads a key's press through the Input System also
reads `TopiaForgeInputOwnership.KeyPressedThisFrame(key)`, as hotkeys and key captures do. The
decisions live in the Unity-free `TopiaForgeInputGate`, `TopiaForgeDismissOrder` and
`TopiaForgeNavigationDriver`, covered by `UiKitInputOwnershipTests`.

Every kit label comes from one TextMesh Pro factory, which turns on extra padding. Quicksand's
stems are thin, so without it small text gets glyph quads narrower than a pixel, and the GPU drops
whole letters: at UI scale 1.0 in a 1600x900 window, "default" rendered as "defau t".

Every text style's face comes from `TopiaForgeTokens.FaceOf`, and widgets take it from
`TopiaForgeFonts.For(style)`, never by weight. Body, label and caption text, and any style at body
size or smaller, use Quicksand Medium (500). Quicksand Light's stems are half a pixel wide at 14 px:
at UI scale 1.0 in a 1920x1080 window the dot of an i merged with its stem, and "TopiaForge" read as
"ToplaForge". Headings and numerals keep Quicksand Light, display styles use Audiowide, and bold styles
are TextMesh Pro's faux bold over their style's face.

A kit canvas starts at zero size, and Unity gives it its real size in its next canvas update. A
window shown in the frame its canvas is built finds that canvas still 0x0, so it keeps its size and
position and finishes its screen clamp on the first frame the canvas has a size; clamping against 0x0
once collapsed the UI Gallery to nothing at 1920x1080. Forcing a canvas update is no substitute: the
CanvasScaler writes its factor only when it differs from the last one, which starts at 1, so at
1920x1080 and UI scale 1 the canvas is still 0x0 after `Canvas.ForceUpdateCanvases()`. The clamp
caps a window from its designed size, so a cap taken on a smaller canvas does not stick. A window also
re-places itself on the first frame after its canvas changes size, as a new resolution or UI scale
does, so its title bar never stays off screen. Shown again during its close fade, a window stays
shown, as a fullscreen tool does.

Every kit canvas holds its sorting slot through one ledger, `TopiaForgeLayerLedger`, and gives it
back by what was allocated, never by reading the canvas: a canvas that a scene destroyed can no
longer say which order it held, and a HUD made in each world used to move one slot up the 800-slot
HUD band with every world change. A component on each canvas releases its slot whenever anything
destroys it. The host then lets go of what was on that canvas, closing a dialog as a cancellation,
and raises `UiHost.LayerLost` for the canvas's owner. Kit layers made directly stay scene-owned
unless created with `persistent: true`, which suits UI built for one scene's objects; the SDK's
surfaces and modals are always persistent.

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
list; if a comparer throws, the rows keep their supplied order and the sort clears, so no chevron
shows. Selection reports the caller's index and follows its row through re-sorts; `Keyed` makes it
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
("Relay", "3/7"). `MoveTo` keeps skips before its target but returns every later step to pending,
so retrying an earlier phase never leaves a later one skipped. Status reads without color: a number,
a check, a cross or a chevron. The active node breathes unless motion is reduced.

### Chat log (`TopiaForgeChatLog`)

A bounded HUD log that evicts its oldest line. Every line passes through `TopiaForgePlainText`:
rich-text tags, escapes, line breaks and bidirectional overrides cannot restyle or spoof the log, and
lines are bounded (300 characters by default, author names 32). While closed, the newest lines (eight
by default) show for 8 seconds and then fade over 1.5; reduced motion hides them at once instead,
and turning it off mid-fade brings them back. `SetOpen(true)` shows the whole history on a backdrop
with scrollback. Lines are pooled, so a new
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
HOST, and the talking glyph slot. Names, secondary lines and badges are plain text, and a removed
handle stays inert even after its pooled tag is reused.

### Dialogs, toasts and layout for flows across scenes

These renderer members serve screens that show what other players wrote and follow a flow across
scene loads, such as the manager's multiplayer screens. Like the widgets above, they are not part of
the safe `Context.Ui` contract.

- `Modal.Destructive(title, body, confirmLabel, onConfirm, onCancel, ...)` also says when the dialog
  ends without a confirmation: `onCancel` runs once, after the dialog has closed, whether through its
  cancel button, ESC or its owner, and never after confirming. With `plainText: true` the title and
  body render exactly as written, so markup in a player's name shows as typed; with
  `persistent: true` the dialog survives scene loads. It returns the dialog, so its owner can close
  it once the decision no longer applies, which counts as cancelling.
- `Modal.Custom(title, scheme, width, persistent)` makes the empty shell with the same choice. A
  persistent modal survives scene loads, as a dialog that follows a flow across them must; one that
  is not is destroyed with the scene it opened in. The owner closes it either way, and disposing the
  host tears it down.
- `TopiaForgeToasts.ShowPlain(...)` shows a toast whose text renders as written, markup unparsed.
  Bound the text first, through `TopiaForgePlainText`.
- `widget.SetShown(bool)` takes a widget out of its parent's layout, so the rest closes up, or puts
  it back. It is dirty-checked: a call that changes nothing rebuilds no layout. `SetVisible` keeps a
  hidden widget's space, which suits a momentary change.
- `inputField.Password()` masks what is typed; the text itself is unchanged.

See [Core services](CoreServices.md#ui-accessibility) for the surrounding context and
[Test a mod](TestingMods.md) for `FakeUiService` assertions.
