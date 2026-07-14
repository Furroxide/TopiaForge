# {{DISPLAY_NAME}}

A framework service mod ({{MOD_ID}}) that publishes a typed service other mods consume via `GetService`, modeled on the Assets mod.

## Quick start

1. Validate the project: `topiaforge check package .`
2. Build and package: `topiaforge pack`
3. Install into the game: `topiaforge install`
4. Play: `topiaforge launch` — open the manager with **F10** to see the mod loaded.

## What to edit next

- `I{{TYPE_NAME}}Service.cs` — the public API other mods compile against; it ships in `apiAssemblies` so consumers get a reference assembly on restore.
- `{{TYPE_NAME}}Service.cs` — the implementation registered at load.

New to modding? Follow `docs/YourFirstMod.md` in the TopiaForge repository; `docs/Modding.md` covers services and `apiAssemblies`.
