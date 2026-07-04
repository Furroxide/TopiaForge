# Sandbox — the freeform creator gamemode

`mods/Robotopia.Sandbox` turns the Open Sandbox arena into Robotopia's answer to Garry's Mod sandbox:
an open creator stage where you spawn the game's own props and robots, throw them around with the
Gravity Gun, and reset the stage with one click.

The split follows the Zombies pattern: **Robotopia Worlds** owns the Open Sandbox world (the `UgcPlay`
scene plus a generated arena) and registers the `robotopia.worlds.sandbox` gamemode; **Robotopia.Sandbox**
attaches the gameplay layer — spawn menu, tools, HUD — to any session running that gamemode, and tears
everything it spawned down when the session ends.

## Playing it

Launch **Sandbox** from the in-game GAMEMODES menu (or set it as the Worlds auto-launch). Defaults:

| Input | Action |
|---|---|
| `Q` | Toggle the spawn menu |
| `Z` | Undo the last spawn |
| `F` | Freeze/unfreeze the spawned prop under the crosshair |
| Right mouse (hold) | Gravity Gun grab (from `robotopia.gravitygun`) |

All three hotkeys are rebindable from the spawn menu's TOOLS tab (persisted to the mod config).

**Spawn menu tabs:**

- **PROPS** — a searchable, virtualized list of the game's built-in UGC asset catalog (the same assets
  the in-game creator places) plus primitive shapes. Click an item to spawn it where you look. Every
  prop gets a collider and a non-kinematic rigidbody, so the Gravity Gun can grab it out of the box.
- **NPCS** — spawn native robots via RobotKit: dormant (a posed, mod-owned robot you can PROGRAM — see
  below) or autonomous (the game's own brain — it wanders, talks, and thinks). Tint, scale (0.5–2×),
  and an optional name. The tab also offers four **PROGRAM MARKERS** (red/blue/green/gold pads) —
  stable named places robots can be sent to. When RobotKit is missing or no level is loaded, the tab
  shows a hint instead.
- **TOOLS** — undo, freeze-all / unfreeze-all, hotkey rebinds, and CLEAN UP EVERYTHING (destructive
  confirm). Cleanup is also injected into the vanilla pause menu while a sandbox session runs.

A small HUD (top-left) tracks live prop/robot counts and the hotkey hints.

## Program a robot

Dormant robots are **fully programmable from a clean slate by talking to them**. Walk up to one and use
its **PROGRAM** prompt: a chat window opens (typed text, or push-to-talk voice — Tab toggles, hold `V`
to talk) and the robot answers in character. Chat freely, or give it a task:

> "follow me" · "go to the red marker" · "patrol between here and the blue marker" · "stop"

Each turn the robot's brain picks an **action** (`CHAT / IDLE / GO_TO / FOLLOW / PATROL`) and a
**target** from the closed set of names it actually knows — the player (`PLAYER`), every marker pad,
and every spawned prop/robot (named from their labels: `CRATE`, `CRATE 2`, …). The moment it accepts a
task (any action other than `CHAT`) it says so, **leaves the chat on its own, and goes to do it** — the
window closes and the program runs until you re-program it. The parse is gated deterministically: an
action with no real target degrades back to chat with a nudge, so the robot can never be programmed
against a place its brain invented.

Programs are executed by RobotKit's objective service (`IRobotObjectiveService`, see docs/RobotKit.md):
GO_TO re-chases a target that gets carried away, FOLLOW tracks live objects natively, PATROL loops
between the robot's position at program time and the target. Re-opening the chat suspends the current
program (LEAVE restores it; a new task replaces it — clean slate). Programs are **session-only**:
nothing persists across sessions, and cleanup/undo removes the robot's target name from the vocabulary.
When the brain backend is unreachable the chat still opens and degrades gracefully (the robot "can't
hear you" — status shows the brain is offline).

## The arena

`mods/Robotopia.Worlds/SandboxArenaBuilder.cs` generates a gm_construct-lite stage centred on the
player spawn: a 200×200 ground with boundary walls, a spawn platform, ramps, a block staircase up to a
lookout, pillars, cover blocks, and three tinted colour-zone pads for orientation. All of it is static
primitive geometry parented under the arena root, so the existing `UnloadArena` teardown is unchanged.
`HdrpEnvironment` still supplies the sky/exposure/sun.

## Architecture

```
Robotopia.Worlds                        Robotopia.Sandbox
────────────────                        ─────────────────
Open Sandbox world + arena              SandboxMod (IRobotopiaMod)
"Sandbox" gamemode + menu entry          └─ SessionChanged(gamemode == robotopia.worlds.sandbox)
WorldSession lifecycle                       └─ SandboxController (per session)
IWorldPauseMenuService                           ├─ PropCatalog   — UGC asset map reflection + primitives
                                                 ├─ PropSpawner   — crosshair placement + physics prep
                                                 ├─ SpawnRegistry — LIFO undo, freeze, counts, cap, cleanup
                                                 ├─ Ui/SpawnMenuWindow (QwUi Paper window, Q)
                                                 ├─ Ui/SandboxHud      (QwUi HUD layer)
                                                 ├─ RobotProgramDirector — pure request/parse for PROGRAM
                                                 └─ RobotChat + Ui/RobotChatWindow — the PROGRAM chat flow
```

- **Session-scoped**: the controller (and its UiHost, hotkeys, and everything spawned) is created on a
  matching `SessionChanged` and disposed on `SessionEnded` — vanilla pause-menu exit, a superseding
  launch, and mod unload all funnel through the same teardown.
- **Dependencies**: hard `vpmDependencies` on `robotopia.worlds >= 0.4.0` and `robotopia.robotkit
  >= 0.7.0` (robot programming is core to the mode). The Gravity Gun stays soft (`loadAfter`): it
  simply grabs whatever rigidbodies exist.
- **Spawn cap**: `maxSpawnedObjects` (default 200) refuses further spawns with a toast instead of
  letting a spawn spree melt the frame rate.

## Game bindings

Clean-room reflection into `GameCode` is declared in `bindings/robotopia.sandbox.gamebindings.json`
and validated against `baselines/gamecode.surface.baseline.json` by the test suite:

- `UgcImportHostSceneController.BuiltInAssetMap` → the scene's `UgcBuiltInAssetMap`
  (fallback: `Resources.FindObjectsOfTypeAll`).
- `UgcBuiltInAssetMap.entries` — enumerated once per session; the nested `Entry` type is not in the
  baseline, so the asset-id field is located at runtime (the `@`-prefixed string field).
- `UgcBuiltInAssetMap.TryGetPrefab(string, out GameObject)` — spawn-time prefab resolution.

Everything degrades: if any symbol is missing the catalog stays primitives-only with a single warning.

## Config (`robotopia.sandbox` config json)

`spawnMenuKey` ("Q"), `undoKey` ("Z"), `freezeKey` ("F"), `spawnDistanceMax` (40), `maxSpawnedObjects`
(200), `defaultRobotBrainMode` ("Dormant"), `showHud` (true), `chatMaxTurns` (12), `chatTemperature`
(0.6), `voiceKey` ("V").

## Verification

Build + tests per AGENTS.md, then `robotopia dev-install`, launch the game, install the package
inbox (F10), and launch Sandbox from the GAMEMODES menu. Expect in `manager.log`:

- `Sandbox gamemode content loaded (spawn menu, tools, robots).` (boot)
- `Sandbox session started in 'UgcPlay' — press Q for the spawn menu.` (launch)
- `Sandbox prop catalog loaded: N UGC assets + primitives.` (first menu use once the scene is up)
- `World session ended (…): robotopia.worlds.sandbox …` (exit — confirms teardown ran)

For robot programming: spawn a marker pad and a dormant robot, PROGRAM → "follow me" — the robot
replies, the chat closes itself, and it chases you (`Sandbox programmed '<name>': FOLLOW PLAYER` in the
log). Re-open and send it to the marker, then have it patrol; undo the marker and watch the objective
park (the robot stops and waits for the target to come back).
