# TopiaForge UI Kit (TopiaForgeUi)

`TopiaForge.Mods.UnityUi` is the in-game UI framework for TopiaForge mods running in Robotopia and the mod
manager. It renders the TopiaForge brand (the same design system as the desktop
launcher) on uGUI + TextMeshPro, and ships with the loader — reference the DLL from your
mod project and go; no manifest dependency is needed.

```xml
<ProjectReference Include="..\..\src\TopiaForge.Mods.UnityUi\TopiaForge.Mods.UnityUi.csproj" />
```

The living reference is the **UI Gallery** dev mod (`mods/TopiaForge.UiGallery`, F8
in-game): every widget, both schemes, all accessibility modes.

## Quickstart

```csharp
public sealed class MyMod : ITopiaForgeMod
{
    private UiHost? ui;

    public void OnLoad(IModContext context)
    {
        ui = TopiaForgeUi.For(context);                       // wires mod id, data dir, logger

        var window = ui.Window("settings", "MY MOD");  // draggable, persists its rect
        window.Content.Label("Hello from the brand.", TopiaForgeTextStyle.Body);
        window.Content.Toggle("Enable the thing", true, v => { });
        window.Content.Button("DO IT", () => ui.Toast("Done.", TopiaForgeTone.Success));

        ui.Hotkey(TopiaForgeKey.F7, window.Toggle);            // dual input-backend hotkey
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
| `TopiaForgeScheme.Paper` | Full-screen tools, windows, dialogs, menus | Warm paper surfaces, ink text — the launcher look |
| `TopiaForgeScheme.Hud` | Gameplay overlays drawn over the world | Translucent dark panels, paper text, bright accents |

Both resolve from one semantic role set (`Surface`, `Primary`, `Accent`, `Danger`, …) so
they read as one brand. **Never hardcode hex colors** — take colors from
`TopiaForgeTone` (labels/bars/badges accept tones) or the resolved theme
(`host.Theme(scheme)`); custom colors passed to `SetColor` are automatically re-toned in
high-contrast mode.

The brand-orange `Primary` is constant everywhere. A mod may override the *accent* only
(`TopiaForgeUiOptions.Accent` / `host.SetAccent`); on Paper the kit auto-darkens it until it
reads (≥ 4.5:1).

## Widgets (container factories)

Containers (`Column`, `Row`, `Stack`, `Grid`, panels, window content) expose factories:
`Label`, `Button`/`IconButton`, `Toggle`/`Checkbox`, `Slider`, `Tabs`/`NavRail`,
`Input`/`SearchInput`, `Keybind`, `Dropdown`, `Badge`, `Scroll`, `ListView<T>`
(virtualized), `ListRow`, `Card`, `SectionHeader`, `KeyValueRow`, `ProgressBar`/`StatBar`,
`PipRow`, `Panel`, `Image`/`FreeImage`, `Divider`, `Spacer`.

Two method families, one convention:

- **Build-time chainers** return the widget: `.Dock(TopiaForgeCorner.TopLeft)`, `.Size(w,h)`,
  `.Fixed/FixedHeight/Flex/FillWidth`, `.Tone(TopiaForgeTone.Success)`,
  `.Thresholds(warn, crit)`, `.Tooltip("…")`, `.Dynamic()`, `.Free()`.
- **Runtime setters** return void and **dirty-check**: `SetText`, `SetFraction`,
  `SetVisible`, `SetEnabled`, `SetColor`, `SetSelected`… Call them every frame; they
  cost nothing while the value is unchanged.

## Cards & grids

`Grid(cellWidth, cellHeight, gap)` wraps: it fits as many fixed-size columns as its
container is wide and flows the rest onto new lines (Flutter `GridView.extent`
semantics), re-flowing automatically on resize. Put it inside `Scroll().Content` for a
scrollable gallery.

`Card(title, onClick)` is the grid's cell widget: a preview area (`SetPreviewTexture`
accepts any `Texture`; `SetPreviewIcon` picks the placeholder), a caption, and an
optional corner chip (`SetBadge`). Hover strengthens the ring, press reuses the button
sticker motion, and `.Tooltip("…")` adds hover details after the standard 450 ms.

```csharp
var grid = pane.Scroll().Content.Grid(118f, 148f, TopiaForgeGap.Sm);
var card = grid.Card("Tree Model", () => Spawn(item))
               .Tooltip("Tree Model\n@topiaforge/tree-model");
card.SetBadge("UGC", TopiaForgeTone.Accent);
card.SetPreviewTexture(thumbnail);      // null shows the placeholder icon
```

Pool cards when rebinding (filtering, live catalogs): membership in the grid must be
toggled with `card.Go.SetActive(...)` — `SetVisible` hides via CanvasGroup, so a
"hidden" card would still occupy its cell. The kit does not own preview textures;
destroy them when your feature tears down.

## Shop pane & window

`TopiaForgeShopPane` is a ready-made shop: balance readout over a card grid of SDK `ShopItem`s
(price badges, affordability dimming, per-run purchase caps with MAX badges, toast
feedback). The pane owns presentation and the purchase *transaction* (through the SDK's
`ShopTransactions` arbiter, debiting an `IShopWallet`); what a purchase *does* stays in
your mod — subscribe to `Purchased` and switch on the item id. `TopiaForgeShopWindow` hosts the
pane in a standard window (ESC/X close, cursor lease, drag/persist), which makes a
complete shop about ten lines:

```csharp
var wallet  = new ShopWallet(500);                     // or implement IShopWallet over your own state
var catalog = new[]
{
    new ShopItem("mymod.heal", "FIELD REPAIR", "+50 integrity.", 400, "HULL"),
    new ShopItem("mymod.armor", "PLATING", "+25 max integrity.", 900, "HULL", maxPurchases: 2),
};
var shop = ui.ShopWindow("shop", "SUPPLY DROP", catalog, wallet);
shop.Pane.CanPurchase = item => item.Id != "mymod.heal" || hp < maxHp;  // optional host gate
shop.Pane.Purchased  += item => Apply(item.Id);        // wallet already debited
shop.Closed          += ResumeRound;                   // fires for ESC and the X alike
ui.Hotkey(TopiaForgeKey.F6, shop.Toggle);
```

Call `shop.Tick()` (or `Pane.Tick()`) per frame while open — it is dirty-checked and
free at steady state, and re-evaluates `CanPurchase` gates that can flip without a
wallet event. `Pane.ResetPurchases()` starts a new run (per-run caps re-arm); earn
credits with `wallet.Earn(...)` and the balance label, dimming, and badges follow the
wallet's `BalanceChanged` automatically. Pausing gameplay while the shop is open is the
host's job (a gamemode gates its own timers on `IsOpen`/`Closed`; pair with a Chronos
`Freeze` lease for a world-hold) — see Zombies' FIELD REQUISITIONS for the reference
wiring. The gallery's SHOP tab exercises every state.

## HUD patterns

```csharp
var hud   = ui.HudLayer("myhud");                       // dark scheme, raycast off
var panel = hud.Scaled.Panel(TopiaForgePanelStyle.HudPanel)
                .Dock(TopiaForgeCorner.TopLeft).Size(380, 200);
var col   = panel.Column(TopiaForgeGap.Sm, TopiaForgeGap.Md);
var wave  = col.Label(TopiaForgeTextStyle.Numeral);
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
- **Toasts** (`ui.Toast(text, tone)` / `TopiaForgeToasts`): queued, max four visible, top-right.
- **Layers/sorting**: canvases are allocated inside bands — HUD < windows < modals <
  toasts < debug, all above the game's UI. Never set `Canvas.sortingOrder` yourself.
- **Hotkeys** (`ui.Hotkey(TopiaForgeKey.F7, action)`): polled through whichever input backend
  the game runs; letter keys are suppressed while a text field has focus. Pair with
  `Keybind(...)` fields for rebinding.
- **Callback isolation**: button/input/list/hotkey callbacks and public TopiaForgeUi events are
  invoked independently; one throwing consumer is logged and cannot starve later subscribers.
- **Host lifetime**: after `UiHost.Dispose`, creation, theme, toast, modal, accent, and
  hotkey operations throw `ObjectDisposedException` instead of leaking process-global state.
- **Cursor**: windows/modals lease it automatically. For custom gameplay modals hold a
  `TopiaForgeCursorLease` — it re-asserts the unlock every frame (the game re-locks per frame).
- **ESC limitation**: BepInEx UI cannot consume the key before the game sees it; the
  dismiss stack closes only the topmost surface per press.

## Accessibility

Player-wide settings are live-applied through `TopiaForgeTheme` (no rebuilds — widgets
re-tint in place):

- `TopiaForgeTheme.HighContrast` — re-tones both schemes; custom `SetColor` values are
  emphasized automatically.
- `TopiaForgeTheme.UiScale` (0.75–1.5) — canvas-level scaling.
- `TopiaForgeTheme.ReducedMotion` — transitions become instant, pulses/punches stop.
- `TopiaForgeTheme.MotionScale` (0–2) — player-wide motion intensity.

The manager's Settings tab owns those process-wide values. A mod must not mutate them.
Pass mod-specific preferences through `TopiaForgeUiOptions.AccessibilityProfile`, or call
`host.SetAccessibilityProfile(...)`. High contrast and reduced motion can only
strengthen the player's global choices; UI scale composes and clamps to 0.75–1.5;
motion intensity multiplies the global value. Read `host.EffectiveHighContrast`,
`host.EffectiveUiScale`, `host.EffectiveReducedMotion`, and `host.EffectiveMotion`
inside custom effects. Host changes retheme only that host. Zombies is the reference
for `hudHighContrast` and `hudMotionIntensity`.

## Performance contract

The kit guarantees: dirty-checked setters, pooled toasts/list-rows/floaters/tweens, one
procedural sprite atlas (chrome batches), TMP re-meshes only on change, zero
steady-state allocation in its own per-frame paths.

You must: call setters with raw values instead of building strings per frame (use the
`SetText(prefix, int)` overload or cache composed strings), pool anything you spawn per
event, keep per-frame work inside `.Dynamic()` subtrees, and never `Destroy`+rebuild on
a timer.

`TopiaForgeDebugOverlay.Toggle()` shows live frame time, font tier, input backend, theme state,
and tween/lease/canvas counters.

## Fonts & the brand bundle

Text is TextMeshPro. Fonts resolve through a tiered chain, logged at init:

1. **Brand bundle** (`TopiaForge Body SDF` and `TopiaForge Display SDF`) — embedded inside
   `TopiaForge.Mods.UnityUi.dll`; built by `topiaforge unity build-ui-bundle` from
   `tools/unity-ui-bundle` with Unity 6000.0.23f1. The committed bundle and provenance manifest contain the
   neutral-named generated derivatives of the attributed, unmodified Quicksand and
   Audiowide source fonts from that pinned editor. The bundle targets `StandaloneWindows64`; on native
   macOS, a load failure continues through the fallback chain below.
2. OS font (Segoe UI) as a dynamic TMP asset.
3. The game's own TMP default.
4. Safe-mode banner (kit UI still functions; text is the only casualty).

If players report wrong-looking fonts, check the `[TopiaForgeUi]` init line in the BepInEx log
for the resolved tier.

## Versioning

Mods bind to `TopiaForge.Mods.UnityUi` by simple assembly name and the loader's copy
wins, so the public API is **additive-only within a major version**
(`AssemblyVersion` stays 1.0.0.0 across 1.x). A `MissingMethodException` naming a
TopiaForge UI type means the installed loader is older than the kit your mod compiled against —
update the loader.
