# {{DISPLAY_NAME}}

A UI mod ({{MOD_ID}}) that opens a TopiaForgeUi-backed window through the safe `IUiService`.
Its named action defaults to **U** and rejects the framework-reserved F8 and F10 keys. The window
demonstrates text, row/column layout, scrolling, buttons, a toggle, slider, text input, dropdown,
and bounded virtual list without referencing Unity.

## Quick start

1. Restore the pinned SDK: `topiaforge restore`
2. Build and test: `dotnet build` then `dotnet test tests/{{ASSEMBLY_NAME}}.Tests`
3. Validate and package: `topiaforge check package .` then `topiaforge pack`
4. Install and play: `topiaforge install` then `topiaforge launch`; press **U** to toggle the window.

## What to edit next

- `{{TYPE_NAME}}Mod.cs` — the named action, declarative controls, callbacks, and safe window lifecycle.
- `{{TYPE_NAME}}Config.cs` — configurable nonreserved toggle key.
- `tests/` — a real NUnit UI lifecycle and interaction test using the captured fake UI service.

New to modding? Follow `docs/YourFirstMod.md` in the TopiaForge repository; `docs/UiKit.md` documents the TopiaForgeUi widgets.
