# {{DISPLAY_NAME}}

A world gamemode mod ({{MOD_ID}}) that registers with the Worlds service and appears in the level-select menu, modeled on the Zombies mod.

## Quick start

1. Validate the project: `robotopia check package .`
2. Build and package: `robotopia pack`
3. Install into the game: `robotopia install`
4. Play: `robotopia launch` — the gamemode appears under **GAMEMODES**.

## What to edit next

- `{{TYPE_NAME}}Mod.cs` — the gamemode lifecycle (start, tick, end conditions).
- `robotopia.mod.json` — the `worldGamemodes` entry defines the menu id/name/description (`robotopia mod add gamemode id:Name:desc`); depends on `robotopia.worlds` and `robotopia.robotkit`.

New to modding? Follow `docs/YourFirstMod.md` in the QuantumWorks repository; see `docs/RobotKit.md` for robots and standard agents.
