# Multiplayer developer experience audit (2026-10-18)

Status: findings for the remaining multiplayer slices · Scope: writing multiplayer mods for Robotopia with TopiaForge

## Why this exists

The live-multiplayer plan promises authors one rule: scripts run on the host, a player's own actions run where that
player is, and everyone sees the host's world. Networking is meant to happen without author code, and anything the
runtime cannot do automatically is meant to be caught at build time with a fix. This audit tested that promise the way
a newcomer meets it.

Three small mods were built with `topiaforge new mod` from the public material only: `README.md`,
[Multiplayer](../Multiplayer.md), [Modding](../Modding.md), [Diagnostics](../Diagnostics.md) and the template READMEs.
Every step that needed code, knowledge or a workaround that the runtime, the generator or the CLI could have supplied
was logged as a friction, with its cost in lines and what the docs or analyzers said at the time. The internal design
records were read only afterwards, to name an owner for each friction.

## The three mods

| Mod | Built from | What it does |
| --- | --- | --- |
| Player counts | `multiplayer` template | The template's shared counter, turned into each player's own count, listed for every player. |
| Dice roll | `gameplay` template, then `mod add multiplayer` | A key press the host answers with a roll. The presser sees the result, everyone else a toast. |
| Player list | `ui` template, then `mod add multiplayer`, `client-local` | A window of the session's players and their ping, from the session snapshot, the same in solo play. |

All three compile, pass their own rig tests, pack and pass `check package`, before and after the fixes. Lines of C#
without blanks or comments, mod code and tests together:

| Mod | Before the fixes | After the fixes |
| --- | --- | --- |
| Player counts | 408 | 378, and its command is now owner-predicted, so the presser sees a press at once |
| Dice roll | 239 | 214, and its test project no longer needs a package added by hand |
| Player list | 311 | 311: its frictions, F3, F15 and F16, are left to WS3 3.11, the CLI and the testing kit |

The after column builds every fix below together, against the SDK those changes produce.

## Fixed by this audit

| Pull request | Friction it removes |
| --- | --- |
| #240 | F1: a command's answer needed a task list drained on later frames. Each command now has `Submit{Handler}(request, answer => ...)`, and TF1008's message and code fix lead to it. |
| #241 | F2: TFMP006 refused a predicted handler's array reads, array copies and own helpers, with no way out. |
| #242 | F11: an unresolved `[NetworkBound]` also raised TFMP004, and the template's payload file lacked the `using` it needs. |
| #243 | F7: after `mod add multiplayer`, the tests of the single-project templates failed with CS0103. |
| #244 | F18: the multiplayer page contradicted itself on live play and binding, described the Players API as working, and never stated the thread and tick rules. |
| #246 | F8: tests bound the contract by hand, before the mod had a context. |

## Friction log, by cost to authors

Cost counts what the audit's mods paid, and who else pays it. Each friction names the slice that should own its
automatic alternative and says whether the release should wait for it. Each owner is a slice of the roadmap. B1 is a
bug rather than a friction, and the one item here that held the release, until #265 fixed it.

| # | Friction | Cost | Owner | Blocks release |
| --- | --- | --- | --- | --- |
| F1 | Reading a command's answer meant polling a task list | 25 to 29 lines in every mod that reads an answer | Fixed in #240 | No |
| F2 | TFMP006 refused arrays, array copies and the mod's own helpers in predicted handlers | 11 errors on one handler; owner prediction given up | Fixed in #241 | No |
| F3 | A session's `SubscribeChanged` runs at every network tick | about 33 and 55 lines of comparison code, or an allocation every tick | WS3 3.11 | No, if 3.11 lands it |
| F4 | Per-player state is built by hand | about 45 lines in each mod that keeps a value per player | WS3 3.11, WS5 scoreboard | No |
| F5 | The host must prune players who left, and learn that a dropped player stays listed for 30 seconds | about 28 lines | WS3 3.11 | No |
| F6 | A HUD was rebuilt for every world | about 28 lines and a field per mod with a HUD | Fixed in #252 | No |
| F7 | `mod add multiplayer` broke the tests of the single-project templates | a CS0103 build failure, 1 line to fix by hand | Fixed in #243 | No |
| F8 | Tests bound the contract and registered the session by hand, before the mod had a context | 6 to 11 lines per test harness | Fixed in #246 | No |
| F9 | Each refusal needs a toast rate limit and a text per error code | about 20 lines per mod | WS3 3.12, WS5 | No |
| F10 | The contract lock is synced by hand, and `dev` fails late at pack | 2 commands per contract change | WS6 6.9 | No |
| F11 | The payload file lacked a `using`, and an unresolved bound raised TFMP004 too | 1 line and a misleading error | Fixed in #242 | No |
| F12 | The mod finds its own session, and the presser's name, by hand | 4 to 6 lines and a field per mod | WS3 3.11, generator | No |
| F13 | One invalid handler hides every generated member, so each call adds CS0103 | error noise around the real error | WS3 3.11, generator | No |
| F14 | A contract on a class other than the mod's entry type is never bound and fails at its first submit | a runtime surprise | WS3 3.11, generator | No |
| F15 | A client-local mod has no CLI path | a hand-edited manifest and V6 rules to know | CLI, WS6 6.8 | No |
| F16 | The test rig never reports ping or connection quality | a ping column cannot be tested | Testing kit, WS3 3.11 | No |
| F17 | A command with no input still needs an empty request class and `new Request()` at each call | 3 lines per command | WS3 3.11, generator | No |
| F18 | The docs contradicted themselves and left the thread and tick rules unsaid | reading time, and wrong code | Fixed in #244 | No |
| F19 | A gameplay key needs its own "only in a world" check | 5 lines | WS5b chat, connection HUD and pause entries | No |
| F20 | `dev` cannot stop before installing into the game | contributors with a real install avoid it | WS6 6.9 | No |
| F21 | A snapshot-only mod still gets the generator and an empty contract lock | harmless clutter | CLI, WS6 6.8 | No |
| F22 | Running the CLI from source outside the checkout finds two templates and no package validator | contributors only | CLI, WS6 6.8 | No |
| B1 | Stopping a gamemode or participant scope retired the whole mod's multiplayer objects | a correctness bug | Fixed in #265 (WS3 3.9) | Was yes; fixed |

## The frictions in detail

Each entry gives what an author had to do or know, what it cost, what the docs or analyzers said, and the automatic
version.

### F1: answers needed polling (fixed in #240)

- **What it took.** The template submitted a command, kept the task in a list, and on every later frame drained the
  completed ones with `GetAwaiter().GetResult()`, which TF1008 accepts only behind an `IsCompleted` check. Both mods
  that read an answer copied the loop.
- **Cost.** 29 lines in the player counts mod, 25 in the dice roll mod, and 27 in the template itself.
- **What the docs said.** TF1008 recommended `PendingOperation<T>` polled from the frame update for every SDK task.
  The multiplayer page never showed how to read a command's answer.
- **Automatic version, in #240.** `Submit{Handler}(request, answer => ...)`. The answer runs once, on the main
  thread, never after the mod or the caller's scope has stopped, and nothing is polled. TF1008 names the method, and
  its code fix rewrites the wait.

### F2: predicted handlers could not read their own data (fixed in #241)

- **What it took.** Extending the template's count into a per-player list made one owner-predicted handler fail with
  eleven TFMP006 errors: on `Array.Length`, on members of its own nested payload type, and on its own pure static
  helpers, whose `Clone` and `CopyTo` calls would have been refused next. The only escape was
  `Prediction = PredictionMode.None`, so the presser waited a round trip.
- **What the analyzer said.** "Predicted handlers may only mutate generated replicated state and emit buffered
  presentation events", with no fix offered, while the docs said predicted handlers may "read their own data".
- **Automatic version, in #241.** The generator follows array elements, allows the pure array members, and checks
  the mod's own non-virtual methods by the same rules instead of refusing them. TFMP006 now names both ways out.

### F3: the session's change notification runs at every tick

- **What it took.** Both mods that show players subscribed to `IMultiplayerSession.SubscribeChanged`. It runs at every
  network tick, because the snapshot's `Tick` advances (`SessionEngine.RunDueSteps` marks the session changed at every
  step, and a client's `UpdateClientTick` does too). Measured on the rig with nothing but time passing, 600 ticks gave
  600 notifications on a host, 601 in solo play and 400 on a joined player. A HUD or window that redraws from it
  allocates every tick, so the mods compared what they showed by hand instead.
- **Cost.** About 33 lines in the player counts mod and 55 in the player list mod.
- **What the docs said.** Nothing, until #244, which now says so.
- **Automatic version.** A participants-changed notification, or the Players API's `SubscribeJoined` and
  `SubscribeLeft` firing. Ticks could keep their own `SubscribeTick` for the few that want them. The generated session
  hooks subscribe to the same stream, so a contract with `OnMultiplayerSessionStarted` pays the per-tick call too.
- **Owner.** WS3 3.11, with the Players API's join and leave notifications.

### F4: per-player state is hand-built

- **What it took.** An immutable array of `{ player id, count }` entries keyed by the participant id as a
  `[NetworkBound(128)]` string, a copy-on-write update, a lookup, and a guessed `MaximumPlayers` bound.
- **Cost.** About 45 lines.
- **What the docs said.** Nothing about per-player values. `ReplicatedState<T>.Update` hands its updater a detached
  copy, which made the copy-on-write code unnecessary, but only IntelliSense said so until #244.
- **Automatic version.** A per-player replicated value keyed by player, or scoreboard columns every player sees.
- **Owner.** WS3 3.11, with WS5's scoreboard.

### F5: the host prunes players who left

- **What it took.** A `SubscribeChanged` handler that drops the counts of players no longer in the snapshot, run only
  where the process holds world authority, and the knowledge that a dropped player stays listed, not connected, for
  30 seconds.
- **Cost.** About 28 lines.
- **Automatic version.** Per-player state that forgets a player who leaves for good.
- **Owner.** WS3 3.11.

### F6: the HUD was rebuilt for every world (fixed in #252)

- **What it took.** As the template did: create the HUD on each world replacement, dispose it on the next, and keep a
  field for it, because a surface died with its scene.
- **Cost.** About 28 lines and a field.
- **Automatic version, in #252.** A surface lives as long as the mod or scope that made it, so the template makes its
  HUD once in `OnLoad`. TopiaForge shows a gameplay HUD in each world and hides it at the main menu and while a scene
  loads, and a window stays open or closed across scene loads, as the mod left it.

### F7: the single-project templates' tests broke after `mod add multiplayer` (fixed in #243)

- **What it took.** The `gameplay`, `ui` and `minimal` templates test a mod by compiling its sources into the test
  project. After `mod add multiplayer` and `restore`, the test project had the multiplayer contract but not the
  generator, so it failed with "CS0103: The name 'SubmitRollAsync' does not exist". Nothing said a package was missing,
  and copying both items from the mod project warned NU1504.
- **Automatic version, in #243.** `restore` gives such a test project the generator whenever the mod has it.

### F8: tests repeated the runtime's binding (fixed in #246)

- **What it took.** Every multiplayer mod's test harness bound the generated contract by hand, tracked the binding, and
  registered the session: 6 lines from the template, 11 in the dice roll and player counts mods. Bound that way, before
  the mod had a context, a session hook that reads `Context` threw in tests, which the game never does.
- **Automatic version, in #246.** `ModLifecycleRunner` binds the contract to the context's session as the runtime
  does. A test registers the session and writes no binding code.

### F9: refusals need a rate limit and a text for each code

- **What it took.** A toast for a refused press, at most one a second because every mod shares one toast queue, and a
  sentence for each `ModErrorCode` an author can meet: rate limits, conflicts, an unready session.
- **Cost.** About 20 lines per mod, the same in the template and both mods that submit.
- **Automatic version.** Toast coalescing and deduplication in the UI service, and a standard player-facing text for
  each refusal code that a mod can show as is.
- **Owner.** WS3 3.12 presentation routing, with WS5.

### F10: the contract lock is synced by hand

- **What it took.** `topiaforge restore` and then `topiaforge mod sync multiplayer` after scaffolding and after every
  contract change, before `pack` accepts the mod. `topiaforge dev` does not sync either: it builds and tests the mod,
  then fails at pack with TFDEV130. Its cause asks for `topiaforge mod sync multiplayer --project <path>`, a placeholder
  for the project it is already in, and its remediation is the pack stage's generic "Check the manifest, entry
  assembly, package files, and Release build output".
- **Automatic version.** `new mod` and `dev` sync the lock themselves.
- **Owner.** WS6 6.9 (its planned TFDEV115 auto-sync).

### F11: the payload file's missing `using` (fixed in #242)

- **What it took.** The template's payload file asks for `[NetworkBound]` on strings and arrays but had no
  `using TopiaForge.Mods;`. Following the comment gave CS0246 and also TFMP004, which claimed the member declared no
  bound at all.
- **Automatic version, in #242.** The template has the `using`, and an unresolved bound is the compiler's alone to
  report.

### F12: the mod finds its own session and the presser's name by hand

- **What it took.** The generated contract keeps its session private, so a mod that reads the snapshot calls
  `Context.RequireMultiplayer()` in `OnLoad` and keeps the session in a field, with null checks. Resolving it each frame
  would allocate, since the extension service builds a fresh array for every lookup. The dice roll mod also looked up
  the presser's display name in the snapshot by `command.SenderId`.
- **Cost.** 4 to 6 lines and a field per mod.
- **Automatic version.** A generated, allocation-free session accessor on the contract, and the sender as an `IPlayer`
  on the command context.
- **Owner.** WS3 3.11 for the sender, the generator for the accessor.

### F13: one invalid handler hides every generated member

- **What it took.** When any part of a contract fails validation, the generator emits none of it, so every call to a
  generated member adds a CS0103 beside the real error. Adding a 32-character string to the template's state gave one
  TFMP008, correctly, and a CS0103 for `SubmitIncrementAsync` beside it.
- **Automatic version.** Emit the proxy surface of a failed contract too, with bodies that cannot run, so only the real
  errors show.
- **Owner.** WS3 3.11, the next slice that changes the generated contract, for the generator.

### F14: a contract on a class other than the mod's entry type is never bound

- **What it took.** Nothing at build time: a `[MultiplayerContract]` on a helper class compiles. TopiaForge binds only
  the mod's entry type, so the helper's generated proxies throw "BindMultiplayer must succeed before a generated command
  proxy is used" at the first submit. The docs say "Mark one top-level partial class", without saying why.
- **Automatic version.** A generator diagnostic on a contract that is not a `TopiaForgeMod`, saying to move it onto the
  mod class or to bind it by hand, or a runtime that binds every contract its mod owns.
- **Owner.** WS3 3.11, the next slice that changes the generated contract, for the generator and the binder.

### F15: client-local has no CLI path

- **What it took.** `mod add multiplayer` always writes a `session` block with presence and protocol, and `mod set` has
  no multiplayer fields, so the player list mod's manifest was edited by hand, knowing from the V6 schema that
  `client-local` forbids `presence` and `protocol`.
- **Automatic version.** `mod add multiplayer --mode client-local` that writes the right block, and leaves out the
  generator and the contract lock a snapshot-only mod never uses (F21).
- **Owner.** The CLI, with WS6 6.8.

### F16: the rig never measures ping

- **What it took.** A ping column cannot be tested. The rig turns clock probes off (`ClockSyncIntervalMicros = 0`), so
  `PingMilliseconds` stays null and `Quality` stays `Unknown` whatever latency the test sets.
- **Automatic version.** The rig measures round trips on its virtual clock, so a test that sets latency sees the ping
  it implies.
- **Owner.** The testing kit, with WS3 3.11 and WS5's scoreboard tests.

### F17: a command with no input needs an empty request

- **What it took.** An empty `IncrementRequest` class and `new IncrementRequest()` at every call.
- **Cost.** 3 lines per command.
- **Automatic version.** A request-less `Submit{Handler}(answer)` overload for a command whose request type is empty.
- **Owner.** WS3 3.11, the next slice that changes the generated contract, for the generator.

### F18: the docs contradicted themselves (fixed in #244)

- **What it took.** The multiplayer page opened with "TopiaForge does not ship live multiplayer yet" and later said
  live LAN and direct sessions run behind a developer switch. It asked authors to bind the contract and later said it
  needs no binding code. It described the Players API's position, actions and per-player input and UI as working,
  although they report nothing or `Unavailable`. It never said where a mod's session comes from, which thread runs a
  multiplayer handler, or that a session's notification runs at every tick.
- **Fixed.** #244 states each of these.

### F19: a gameplay key needs an "only in a world" check

- **What it took.** A press counts only in a world, so the dice roll mod checks the active scene against
  `GameScenes.IsNonGameplayScene` before submitting.
- **Cost.** 5 lines.
- **Automatic version.** Input actions that are gameplay-only by default, with an opt-in for menus.
- **Owner.** The SDK's input service, with WS5b's chat, connection HUD and pause entries, which will suspend the game's
  own input while a player types.

### F20 to F22: smaller CLI frictions

- **F20.** `dev --no-launch` still installs into the game, so a contributor with a real install avoids `dev`. A
  `--no-install` mode would let `dev` stop after pack. Owner: WS6 6.9.
- **F21.** A snapshot-only mod still gets the generator and an empty contract lock from `mod add multiplayer`. It is
  harmless, and goes away with F15's mode flag. Owner: the CLI, with WS6 6.8.
- **F22.** Run from source outside the checkout, the CLI lists only two templates unless `TOPIAFORGE_REPOSITORY_ROOT`
  is set, and finds no package validator unless `TOPIAFORGE_PACKAGE_VALIDATOR_PATH` is set too. A release extraction is
  fine; contributors trip on it. One repository-root setting could serve both. Owner: the CLI, with WS6 6.8.

## A bug found on the way

### B1: stopping a scope retired the whole mod's multiplayer objects (fixed in #265)

A scoped context, such as a gamemode session's or a participant's, builds its own extension service with the mod's
own id (`ModContext.Scopes.cs`). When code in that scope resolves the multiplayer session, the provider creates a
second owner facade for the same mod. When the scope stops, its lifetime disposes that facade, and the facade calls
`SessionEngine.RetireOwner(owner)`, which retires every live object and pending submission of the owner, not only the
scope's.

A probe on the session engine reproduced it: a mod-level facade spawned the drone sample's drone, a second facade for
the same owner was created and its lifetime disposed, and the drone despawned (1 object, then 0) while the mod's own
lifetime was still running. On a client, the mod's own pending submission is cancelled the same way, with "The mod that
submitted this stopped."

- **Who meets it.** Any gamemode or participant controller that resolves the session from its own `Context`. Nothing
  first-party does yet, but 3.9's participant realm and WS4's Zombies and Sandbox will.
- **Fix.** Retire per facade: each facade retires the objects it spawned and the submissions it made, or the engine
  counts the facades of an owner and retires the owner only when the last one closes.
- **Fixed.** WS3 3.9 (#265) does both: each facade retires only what it made, the engine counts a mod's open facades,
  and the mod's last facade retires the rest. The release no longer waits on it.

## Not yet landed

The plan's slices for world spawns and motion (3.7), session follow (3.8), avatars (3.10) and the Players API (3.11)
had not landed during the audit. What a newcomer meets today, and what each slice should make automatic:

- **3.7, spawns and motion.** The network world (3.6) already replicates what the host spawns through the SDK. A joined
  player's own `Assets.Spawn`, `SetTransform` and motion leases are not routed to the host yet (`RuntimeReplication`
  leaves a client's spawn to 3.7), so on a client they stay in that player's process. The multiplayer page's "What
  replicates" called replicating spawned entities and scene props later work, which stopped being true of the host's
  spawns with 3.6; #244 corrects it. When 3.7 lands, a client's call should replicate with no change to the mod, and
  the page should list what is routed.
- **3.8, session follow.** A client cannot follow the host's world yet. When it lands, `IWorldSessionService`
  consumers should keep working unchanged, and a client's `StopAsync` and `RestartAsync` should fail with a sentence
  that says only the host can do that.
- **3.10, avatars.** `TryGetAvatar` and `TryGetPose` return false, and `IPlayer.Entity` is null. When avatars land, a
  mod should be able to chase or label a player without knowing whether that player is local.
- **3.11, the Players API.** The directory lists players with their names and host status, but its other members are
  inert: `SubscribeJoined` and `SubscribeLeft` return an empty subscription that never fires, which nothing reports;
  actions and scoreboard columns report `Unavailable` with "Player state and actions need the live session engine.",
  even in the test rig and solo play, which run that engine; and `Players` builds a new list on every read. 3.11 should
  make the subscriptions fire, give the interim message a reason an author can act on, and cache the list between
  participant changes. It is also the natural owner of F3, F4, F5, F12 and F16.

## What the remaining slices should take on

- **3.11 Players API:** a participants-changed notification and firing join and leave subscriptions (F3, F5); a
  per-player replicated value or scoreboard columns (F4); the sender as an `IPlayer` on the command context (F12); the
  rig's ping (F16); and, in the generator it changes anyway, the proxy surface of a failed contract (F13), a flag on a
  contract that is not the mod's entry type (F14), a request-less overload (F17), and a generated session accessor
  (F12).
- **3.12 presentation routing:** toast coalescing and a standard refusal text (F9).
- **WS5b scoreboard:** build the player list a mod would otherwise hand-roll (F4), and test its ping column on the rig.
- **WS5b chat, connection HUD and pause entries:** with its input suspension, gameplay-only input actions by default
  (F19).
- **WS6 6.8 and the CLI:** `mod add multiplayer --mode client-local` (F15, F21), and one repository-root setting for
  source runs (F22).
- **WS6 6.9 `dev --peers`:** sync the contract lock in `new mod` and `dev` (F10), and a `--no-install` mode (F20).
- **Replicated-object input:** `IReplicatedObject.SubmitInputAsync` still returns a task to poll; give it the answered
  form #240 gave commands.
- **Every slice:** a template or sample that needs a workaround is a defect in the SDK. Measure a HUD's or window's
  per-tick cost before shipping it, since the session's notification runs at every tick (F3).
