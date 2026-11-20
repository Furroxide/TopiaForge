# {{DISPLAY_NAME}}

A UI mod ({{MOD_ID}}) that opens a TopiaForgeUi-backed window through the safe `IUiService`.
Its named action defaults to **U** and rejects the framework-reserved F8 and F10 keys. The window
demonstrates text, row/column layout, scrolling, buttons, a toggle, slider, text input, dropdown,
and bounded virtual list without referencing Unity.

The window lives as long as the mod: scene loads keep it, open or closed as the mod left it, so the
mod creates it once and handles no scenes.

The manifest applies the Survival sample theme when the window opens. Turn on **Preview alternate
palette** to try the second theme declared in the same package, then turn it off to restore the
manifest default. The preview changes the palette while keeping the window and its control values.

The notification preview shows the same packaged card in two boxes. **Contain** shows the whole
card and **Cover** crops it to fill the box. The small mark takes its tint from the active palette,
and the badge follows the notification toggle. The sample bundle and its receipt are included in
the project. Its artwork is distributed under CC0 1.0, with the notice in [LICENSES/ui-art.txt](LICENSES/ui-art.txt).

## Quick start

1. Restore the pinned SDK: `topiaforge restore`
2. Build and test: `dotnet build` then `dotnet test tests/{{ASSEMBLY_NAME}}.Tests`
3. Validate and package: `topiaforge check package .` then `topiaforge pack`
4. Install and play: `topiaforge install` then `topiaforge launch`; press **U** to toggle the window.

## What to edit next

- `{{TYPE_NAME}}Mod.cs` — the named action, declarative controls, callbacks, and safe window lifecycle.
- `{{TYPE_NAME}}Config.cs` — configurable nonreserved toggle key.
- `ui/themes/` — the manifest's default theme and the alternate preview palette.
- `tests/` — a real NUnit UI lifecycle and interaction test using the captured fake UI service.

New to modding? Follow `docs/YourFirstMod.md` in the TopiaForge repository; `docs/UiKit.md` documents the TopiaForgeUi widgets.
