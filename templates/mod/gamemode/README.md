# {{DISPLAY_NAME}}

A Manifest V6 gamemode package (`{{MOD_ID}}`). Its launch target selects Open Sandbox and the declared `{{TYPE_NAME}}Gamemode` factory. The manager loads the world, establishes player/spawn readiness, and then calls `StartAsync` once within a session scope.

## Quick start

1. Set your author and license information in `topiaforge.mod.json`.
2. Restore and test: `topiaforge restore`, then `dotnet test tests/{{ASSEMBLY_NAME}}.Tests`.
3. Validate, build and package: `topiaforge check package .`, then `topiaforge pack`.
4. Install with `topiaforge install`, start the game, and select the `{{DISPLAY_NAME}}` launch target.

## What to edit

- `topiaforge.mod.json`: `contributions.gamemodes` binds the public, parameterless factory type; `contributions.launchTargets` defines the menu title, mode, world policy and transition. Keep the required Worlds dependency. Declaration IDs are authoritative; do not duplicate factory identity in code.
- `{{TYPE_NAME}}Gamemode`: receives a ready `IGamemodeSession` and returns one `IGamemodeController`. A failed or cancelled start is cleaned up by the session lifetime.
- `{{TYPE_NAME}}Controller`: owns round logic and uses `session.Context` for every service allocation. Its update subscription and pause action end with the session. Restart goes through the bound session operation, so a stale action cannot restart a later session.
- `tests/`: checks package loading without gameplay startup, controller cleanup, bound restart and cancellation before allocation. These deterministic SDK tests do not establish scene timing, geometry, native pause integration or spawn placement; verify those in the installed game.

The scaffold uses `world-service` and `hud`. Add other capabilities and dependencies when the implementation needs them. For robots, add RobotKit and resolve its service from `session.Context`.

Use the supplied scoped context instead of retaining a package context in a factory. Notifications only observe committed lifecycle state. Manifest V6 deliberately excludes `options`, `optionValues` and `sessionExtensions`.

See `docs/ManifestV6.md` and `docs/YourFirstMod.md` in the TopiaForge repository.
