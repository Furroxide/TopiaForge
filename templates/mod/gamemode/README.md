# {{DISPLAY_NAME}}

A world gamemode mod ({{MOD_ID}}) that registers with the Worlds service and appears in the level-select menu, modeled on the Zombies mod.

## Quick start

1. Validate the project: `topiaforge check package .`
2. Build and package: `topiaforge pack`
3. Install into the game: `topiaforge install`
4. Play: `topiaforge launch` — the gamemode appears under **GAMEMODES**.

## What to edit next

- `{{TYPE_NAME}}Session` — one round: created when a session starts, disposed when it ends. Put wave timers,
  scoring, and win conditions here.
- `{{TYPE_NAME}}Mod.cs` — registration and pause-menu actions. `GamemodeHost<T>` owns the wiring.
- `topiaforge.mod.json` — the `worldGamemodes` entry defines the menu id/name/description (`topiaforge mod add gamemode id:Name:desc`); depends on `io.github.furroxide.topiaforge.worlds`.
- `tests/{{ASSEMBLY_NAME}}.Tests/` — an NUnit lifecycle test with a deterministic Worlds fake and leak assertions.

The scaffold declares only the capabilities it actually uses (`world-service`, `hud`). Add more as you reach
for them — for robots, run `topiaforge mod add robotkit` and declare `robot-spawning`.

`GamemodeHost<T>` registers the gamemode and menu entry (rolling the first back if the second fails),
subscribes to session changes, replays a session that is already running, keeps exactly one controller alive,
and re-registers your pause actions for every session. Registration leases, event subscriptions, and the
update callback are all released automatically when the mod unloads or fails partway through loading.

New to modding? Follow `docs/YourFirstMod.md` in the TopiaForge repository; see `docs/RobotKit.md` for robots and standard agents.
