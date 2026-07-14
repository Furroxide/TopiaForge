# {{DISPLAY_NAME}}

A minimal TopiaForge SDK mod for Robotopia ({{MOD_ID}}): logs load, scene, and update events.

## Quick start

1. Validate the project: `topiaforge check package .`
2. Build and package: `topiaforge pack`
3. Install into the game: `topiaforge install` (packs this folder and installs it)
4. Play: `topiaforge launch` — open the manager with **F10** to see the mod loaded.

## What to edit next

- `{{TYPE_NAME}}Mod.cs` — the entry point; hook scene loads and per-frame updates, or add config.
- `topiaforge.mod.json` — describe the mod (`topiaforge mod set <field> <value>` keeps it valid).

New to modding? Follow `docs/YourFirstMod.md` in the TopiaForge repository; `docs/Modding.md` is the full reference.
