# QuantumWorks UI Kit (QwUi)

`Robotopia.Mods.UnityUi` is the in-game UI framework for Robotopia mods and the mod
manager. It renders the QuantumWorks brand (the same design system as the desktop
launcher) on uGUI + TextMeshPro, and ships with the loader — reference the DLL from your
mod project and go; no manifest dependency is needed.

```xml
<ProjectReference Include="..\..\src\Robotopia.Mods.UnityUi\Robotopia.Mods.UnityUi.csproj" />
```

The living reference is the **UI Gallery** dev mod (`mods/Robotopia.UiGallery`, F8
in-game): every widget, both schemes, all accessibility modes.

## Quickstart

```csharp
public sealed class MyMod : IRobotopiaMod
{
    private UiHost? ui;

    public void OnLoad(IModContext context)
    {
        ui = QwUi.For(context);                       // wires mod id, data dir, logger

        var window = ui.Window("settings", "MY MOD");  // draggable, persists its rect
        window.Content.Label("Hello from the brand.", QwTextStyle.Body);
        window.Content.Toggle("Enable the thing", true, v => { });
        window.Content.Button("DO IT", () => ui.Toast("Done.", QwTone.Success));

        ui.Hotkey(QwKey.F7, window.Toggle);            // dual input-backend hotkey
    }

    public void OnUnload()
    {
        ui?.Dispose();                                 // tears down every canvas/lease
    }
}
```

ESC-close, cursor unlock while visible, drag + edge snapping, screen clamping, and
position persistence are all built into the window. `Dispose` the host in `OnUnload`.

## Two schemes, one brand

| Scheme | Use for | Look |
|---|---|---|
| `QwScheme.Paper` | Full-screen tools, windows, dialogs, menus | Warm paper surfaces, ink text — the launcher look |
| `QwScheme.Hud` | Gameplay overlays drawn over the world | Translucent dark panels, paper text, bright accents |

Both resolve from one semantic role set (`Surface`, `Primary`, `Accent`, `Danger`, …) so
they read as one brand. **Never hardcode hex colors** — take colors from
`QwTone` (labels/bars/badges accept tones) or the resolved theme
(`host.Theme(scheme)`); custom colors passed to `SetColor` are automatically re-toned in
high-contrast mode.

The brand-orange `Primary` is constant everywhere. A mod may override the *accent* only
(`QwUiOptions.Accent` / `host.SetAccent`); on Paper the kit auto-darkens it until it
reads (≥ 4.5:1).

## Widgets (container factories)

Containers (`Column`, `Row`, `Stack`, `Grid`, panels, window content) expose factories:
`Label`, `Button`/`IconButton`, `Toggle`/`Checkbox`, `Slider`, `Tabs`/`NavRail`,
`Input`/`SearchInput`, `Keybind`, `Dropdown`, `Badge`, `Scroll`, `ListView<T>`
(virtualized), `ListRow`, `SectionHeader`, `KeyValueRow`, `ProgressBar`/`StatBar`,
`PipRow`, `Panel`, `Image`/`FreeImage`, `Divider`, `Spacer`.

Two method families, one convention:

- **Build-time chainers** return the widget: `.Dock(QwCorner.TopLeft)`, `.Size(w,h)`,
  `.Fixed/FixedHeight/Flex/FillWidth`, `.Tone(QwTone.Success)`,
  `.Thresholds(warn, crit)`, `.Tooltip("…")`, `.Dynamic()`, `.Free()`.
- **Runtime setters** return void and **dirty-check**: `SetText`, `SetFraction`,
  `SetVisible`, `SetEnabled`, `SetColor`, `SetSelected`… Call them every frame; they
  cost nothing while the value is unchanged.

## HUD patterns

```csharp
var hud   = ui.HudLayer("myhud");                       // dark scheme, raycast off
var panel = hud.Scaled.Panel(QwPanelStyle.HudPanel)
                .Dock(QwCorner.TopLeft).Size(380, 200);
var col   = panel.Column(QwGap.Sm, QwGap.Md);
var wave  = col.Label(QwTextStyle.Numeral);
var hp    = col.StatBar("INTEGRITY").Thresholds(warn: 0.5f, crit: 0.25f);

void OnUpdate(float dt)
{
    wave.SetText("WAVE ", currentWave);                 // concatenates only on change
    hp.SetFraction(hpFraction);                          // auto-tones by thresholds
}
```

- `hud.Scaled` — docked panels; respects `hud.SetHudScale(...)` (0.75–1.35).
- `hud.World` — world-projected layers; **never scaled** (projection accuracy).
- `hud.Floaters(n)` / `hud.SpeechBubbles(n)` — pooled world-anchored labels:
  `layer.Push(worldPos, text, color, ttl)`. Camera resolve, behind-camera culling, and
  oldest-slot reuse are built in; `Clear()` on round reset.
- `hud.Banner().Show("WAVE 3")` — punch/hold/fade transient title.
- `hud.SetInteractive(true)` only while a gameplay modal needs clicks.
- Wrap per-frame-churning subtrees in `.Dynamic()` so their canvas rebuilds don't touch
  static chrome.

## Windows, modals, layers, input

- **Windows** (`ui.Window(id, title, …)`): card chrome, drag by title bar, edge
  snapping, screen clamping, click-to-front, ESC-close (topmost first), cursor lease
  while visible, rect persisted per `owner+id` into the mod's data directory (never
  PlayerPrefs). `Closed` event; `Show/Close/Toggle`.
- **Modals** (`ui.Modal.Confirm/Destructive/ConfirmHud/Custom`): scrim + dialog card,
  OutBack entrance, ESC cancels; modals beat windows on the dismiss stack. Use
  `Destructive` for anything irreversible.
- **Toasts** (`ui.Toast(text, tone)` / `QwToasts`): queued, max four visible, top-right.
- **Layers/sorting**: canvases are allocated inside bands — HUD < windows < modals <
  toasts < debug, all above the game's UI. Never set `Canvas.sortingOrder` yourself.
- **Hotkeys** (`ui.Hotkey(QwKey.F7, action)`): polled through whichever input backend
  the game runs; letter keys are suppressed while a text field has focus. Pair with
  `Keybind(...)` fields for rebinding.
- **Cursor**: windows/modals lease it automatically. For custom gameplay modals hold a
  `QwCursorLease` — it re-asserts the unlock every frame (the game re-locks per frame).
- **ESC limitation**: BepInEx UI cannot consume the key before the game sees it; the
  dismiss stack closes only the topmost surface per press.

## Accessibility

Global, live-applied (no rebuilds — widgets re-tint in place):

- `QwTheme.HighContrast` — re-tones both schemes; custom `SetColor` values are
  emphasized automatically.
- `QwTheme.UiScale` (0.75–1.5) — canvas-level scaling.
- `QwTheme.ReducedMotion` — transitions become instant, pulses/punches stop.
- `QwTheme.MotionScale` (0–2) — HUD motion intensity; multiply your own effect
  amplitudes by `QwTheme.EffectiveMotion`.

The manager's Settings tab exposes these to players; feed your mod's config into them
(the Zombies pattern: `hudHighContrast`, `hudMotionIntensity`, `hudScale`).

## Performance contract

The kit guarantees: dirty-checked setters, pooled toasts/list-rows/floaters/tweens, one
procedural sprite atlas (chrome batches), TMP re-meshes only on change, zero
steady-state allocation in its own per-frame paths.

You must: call setters with raw values instead of building strings per frame (use the
`SetText(prefix, int)` overload or cache composed strings), pool anything you spawn per
event, keep per-frame work inside `.Dynamic()` subtrees, and never `Destroy`+rebuild on
a timer.

`QwDebugOverlay.Toggle()` shows live frame time, font tier, input backend, theme state,
and tween/lease/canvas counters.

## Fonts & the brand bundle

Text is TextMeshPro. Fonts resolve through a tiered chain, logged at init:

1. **Brand bundle** (Quicksand + Arista SDF assets) — embedded inside
   `Robotopia.Mods.UnityUi.dll`; built by `tools/build-ui-bundle.ps1` from
   `tools/unity-ui-bundle` (editor must be Unity 6000.0.x ≤ 31 — see that README).
2. OS font (Segoe UI) as a dynamic TMP asset.
3. The game's own TMP default.
4. Safe-mode banner (kit UI still functions; text is the only casualty).

If players report wrong-looking fonts, check the `[QwUi]` init line in the BepInEx log
for the resolved tier.

## Versioning

Mods bind to `Robotopia.Mods.UnityUi` by simple assembly name and the loader's copy
wins, so the public API is **additive-only within a major version**
(`AssemblyVersion` stays 1.0.0.0 across 1.x). A `MissingMethodException` naming a Qw
type means the installed loader is older than the kit your mod compiled against —
update the loader.
