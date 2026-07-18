# {{DISPLAY_NAME}}

A world gamemode mod ({{MOD_ID}}) that registers with the Worlds service and appears in the level-select menu, modeled on the Zombies mod.

## Quick start

1. Validate the project: `topiaforge check package .`
2. Build and package: `topiaforge pack`
3. Install into the game: `topiaforge install`
4. Play: `topiaforge launch` — the gamemode appears under **GAMEMODES**.

## What to edit next

- `{{TYPE_NAME}}Mod.cs` — the gamemode lifecycle (start, tick, end conditions).
- `topiaforge.mod.json` — the `worldGamemodes` entry defines the menu id/name/description (`topiaforge mod add gamemode id:Name:desc`); depends on `io.github.furroxide.topiaforge.worlds` and `io.github.furroxide.topiaforge.robotkit`.
- `tests/{{ASSEMBLY_NAME}}.Tests/` — an NUnit lifecycle test with a deterministic Worlds fake and leak assertions.

The registration leases, event subscriptions, and update callback are all released automatically when the
mod unloads or fails partway through loading.

New to modding? Follow `docs/YourFirstMod.md` in the TopiaForge repository; see `docs/RobotKit.md` for robots and standard agents.
