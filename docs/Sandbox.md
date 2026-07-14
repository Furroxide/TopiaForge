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
- **ROBOTS** — the live fleet roster: every spawned robot with its current program and state badge
  (`FOLLOW PLAYER` + `MOVING`, `AUTONOMOUS`, `NONE`, …), refreshed live. Per-robot actions: **PROGRAM**
  opens the chat remotely (no walk-up needed — the menu closes and the chat opens), **FOLLOW ME** and
  **IDLE** program instantly with no LLM involved. Autonomous robots are refused when
  `reprogramAutonomousRobots` is off, same as the walk-up verb.
- **TOOLS** — undo, freeze-all / unfreeze-all, hotkey rebinds, and CLEAN UP EVERYTHING (destructive
  confirm). Cleanup is also injected into the vanilla pause menu while a sandbox session runs.

A small HUD (top-left) tracks live prop/robot counts and the hotkey hints.

## Program a robot

Dormant robots are **fully programmable from a clean slate by talking to them**. Walk up to one and use
its **PROGRAM** prompt (or hit PROGRAM on the ROBOTS tab): a chat window opens (typed text, or
push-to-talk voice — Tab toggles, hold `V` to talk) and the robot answers in character. The window also
offers deterministic **FOLLOW ME**, **IDLE**, and **SET FREE** actions that do not spend a brain turn;
SET FREE clears mod control and returns the robot to its native autonomous brain. Chat freely, or give
it a task:

> "follow me" · "go to the red marker" · "patrol between here and the blue marker" · "wander around
> here" · "run away from me" · "tell ROBOT 2 to follow me" · "stop"

Each turn the robot's brain picks an **action**
(`CHAT / IDLE / GO_TO / FOLLOW / PATROL / WANDER / FLEE / REPROGRAM / AUTONOMOUS`) and a **target** from
the closed set of names it actually knows — the player (`PLAYER`), every marker pad, and every spawned
prop/robot (named from their labels: `CRATE`, `CRATE 2`, …). Robot targets come with live awareness
facts every turn: where they are *and what they are running* ("`ROBOT 2: another robot, 8 m north-east
of you; currently: FOLLOW PLAYER (moving)`"), so "who's following me?" gets a real answer. The robot
also plays a matching **emote** with each line — derived from its chosen action (thumbs-up when it takes
a move, a wave as it heads off), a free native garnish that spends no LLM output field. The moment it
accepts a task (any action other than `CHAT`) it says so, **leaves the chat on
its own, and goes to do it** — the window closes and the program runs until you re-program it. The
parse is gated deterministically: an action with no real target degrades back to chat with a nudge, so
the robot can never be programmed against a place its brain invented. If the brain returns `FOLLOW`
without its structured target, the parser may recover a single unambiguous known target from the
operator's original wording (for example, “follow me” resolves to `PLAYER`); ambiguous or unknown names
still degrade to chat.

**Robots reprogramming robots.** A `REPROGRAM` decision carries two more closed-set fields — the task
(`IDLE/GO_TO/FOLLOW/PATROL/WANDER/FLEE`, never another REPROGRAM) and that task's target (which may be
the messenger itself: "tell ROBOT 2 to follow you"). The talking robot becomes a **courier**: it
physically walks to the target robot, hands the program over (both emote, a toast narrates —
"`ROBOT 1 reprogrammed ROBOT 2: FOLLOW PLAYER`"), and then holds position in the `DELIVERED` state.
Only robot-kind targets can be reprogrammed; a recipient can't be told to target itself (except
"wander around yourself", which normalises to wandering in place). If a courier delivers to a robot
you're mid-chat with, the delivery wins over LEAVE's restore — but an explicitly accepted program still
beats the delivery.

Programs are executed by RobotKit's objective service (`IRobotObjectiveService`, see docs/RobotKit.md):
GO_TO re-chases a target that gets carried away, FOLLOW tracks live objects natively, PATROL loops
between the robot's position at program time and the target, WANDER roams around its home (or a named
anchor — "wander near the red marker" orbits the pad, and the orbit drifts if the anchor moves), FLEE
keeps its distance and re-aims as the threat moves. Re-opening the chat suspends the current
program (LEAVE restores it; a new task replaces it — clean slate). Programs are **session-only**:
nothing persists across sessions, and cleanup/undo removes the robot's target name from the vocabulary.
When the brain backend is unreachable the chat still opens and degrades gracefully (the robot "can't
hear you" — status shows the brain is offline).

## Robots reacting to each other

Idle dormant robots that pass within ~4 m of each other exchange a quick greeting — both emote and a
toast narrates ("`ROBOT 1 beeps at ROBOT 2.`") — with a per-pair cooldown (~60 s) so it stays ambient,
never spammy. Robots mid-task (couriering, seeking, fleeing) and autonomous robots don't mingle, and
the scan pauses while a chat is open. Config: `ambientGreetings` (default on — it's free).

Opt-in on top: `ambientBanter` (default **off** — each exchange spends one brain token) upgrades a
greeting to a short two-line LLM exchange grounded in what each robot is currently doing, shown as two
toasts. Globally rate-limited by `banterCooldownSeconds` (default 90, min 30); when the backend is
unreachable it degrades silently to the plain greeting.

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
                                                 │   └─ Ui/RobotRosterTab — the ROBOTS tab (pooled rows)
                                                 ├─ Ui/SandboxHud      (QwUi HUD layer)
                                                 ├─ RobotProgramDirector — pure request/parse for PROGRAM
                                                 ├─ RobotChat + Ui/RobotChatWindow — the PROGRAM chat flow
                                                 └─ RobotAmbience — greetings, banter, delivery reactions
```

- **Session-scoped**: the controller (and its UiHost, hotkeys, and everything spawned) is created on a
  matching `SessionChanged` and disposed on `SessionEnded` — vanilla pause-menu exit, a superseding
  launch, and mod unload all funnel through the same teardown.
- **Dependencies**: hard `vpmDependencies` on `robotopia.worlds >= 0.5.4` and `robotopia.robotkit
  >= 0.8.0` (robot programming — including wander/flee and the reprogram courier — is core to the
  mode). The Gravity Gun stays soft (`loadAfter`): it simply grabs whatever rigidbodies exist.
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
(0.6), `voiceKey` ("V"), `reprogramAutonomousRobots` (true), `ambientGreetings` (true), `ambientBanter`
(false — each exchange spends a brain token), `banterCooldownSeconds` (90, min 30).

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

For the new powers: "wander around here" (the robot roams and pauses near its spot), "run away from me"
then walk at it (it hops away, stands watchful at distance), and — with two robots up — "tell ROBOT 2
to follow me": the courier walks over, both emote, the hand-over toast fires
(`ROBOT 1 reprogrammed ROBOT 2: FOLLOW PLAYER`), and ROBOT 2 starts following. Check the Q → ROBOTS tab
throughout: badges track programs and states live, FOLLOW ME / IDLE apply instantly, PROGRAM opens the
chat from the menu. Leave two robots idling next to each other for the greeting (emotes + `X beeps at
Y.`); flip `ambientBanter` on in the config for the token-costed two-line exchange. Finally, LEAVE a
chat after a courier delivered to the robot you were talking to — the delivered program must survive
(no restore-clobber).
