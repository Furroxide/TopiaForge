# RobotKit — Standard-Agent Robots

RobotKit lets a mod **spawn standard-agent robots** — clones of the game's own robot that come up the way a
native robot does (native body, humanoid animation, head/look-at, and **native locomotion**) — and then
**override only the behaviour and visuals it needs**. You start from a default, out-of-the-box-like robot and
lean on the game's own systems for everything you don't change: movement is the game's own pathing and
animation, death is the game's own ragdoll/cleanup, and (optionally) the brain is the game's own LLM agent.

The capability is published by the `Robotopia.RobotKit` framework mod as the `IRobotAgentService` SDK service.
It exists so that "spawn enemies/companions/NPCs that walk the world" mods do not each have to re-derive the
brittle, decompile-driven GameCode reflection it takes to bring a robot up, take over its decisions, and route
it. The `Robotopia.Zombies` gamemode is built entirely on this service.

## What "lean on standard agents" means

A spawned robot is a **real game robot**, not a hand-driven puppet:

- **Movement is native.** You express intent — `MoveTo(point)` or `Chase(target)` — and the game's own
  `WalkSession`/`LocomotionController` carries it out: it path-finds around geometry, follows the route,
  **re-paths on its own as a chased target moves**, recovers when it gets stuck, grounds on slopes/steps, and
  drives the walk animation. RobotKit does **not** re-implement navigation.
- **The brain is native, and dormant by default.** A spawned robot keeps a valid (but quiet) LLM agent so the
  body behaves correctly, while making no plans and no RoboAPI calls — the mod owns its decisions. Opt into
  `RobotBrainMode.Autonomous` to let the robot think, talk, and wander for itself like any game NPC.
- **Combat is native.** Damage, death, and ragdoll go through the game's own `Health` pipeline.

## Architecture

```
your gamemode mod                       Robotopia.RobotKit (framework mod)             GameCode.dll
─────────────────                       ──────────────────────────────────             ────────────
context.GetService<IRobotAgentService>()
Spawn(request) ───────────────────────► resolve robot prefab ────────────────────────► PooledSpawner / loaded assets
                                        instantiate (inactive incubator)
                                        configure brain (Dormant/Autonomous) ─────────► BehaviorTree disabled,
                                        apply tint / name / scale / speeds              LLMAgent set dormant
                              ◄──────── IRobotAgent (wraps the spawned GameObject)
AddComponent<MyBehaviour>(agent.GameObject)   // your health/score/attacks (optional)

each frame:
agent.Chase(player) ──────────────────► (service tick) run native walk to target ─────► WalkSession.Walk →
                                        re-path as target moves, follow, animate         LocomotionController.FollowPath
on defeat:
agent.Kill(...) ──────────────────────► native death ─────────────────────────────────► Health.Damage → ragdoll + cleanup
```

The service owns a `DontDestroyOnLoad` root with an always-inactive **incubator**: a clone is instantiated
under it so its native `Awake`/`OnEnable` fire only **after** the brain is configured, then it is reparented
live and activated as a fully native (but mod-driven) robot. A single per-frame tick (driven from
`IModContext.Update`) keeps each robot's native walk running toward its current intent; robots are cleared
automatically on scene change.

## When it is available

- **`IsAvailable`** is `true` once a spawnable robot prefab and the locomotion symbols resolve. Robot prefabs
  only exist once a gameplay level is loaded, so poll this (it is cheap once resolved) rather than assuming it
  at startup. `Spawn` returns `null` while unavailable.
- **`IsNavigationAvailable`** is `true` when the game's pathfinder is present in the current scene. When
  `false` (e.g. a scene with no robots), a spawned robot can still stand and animate but cannot path to a
  target until a pathfinder exists. The service logs which state it is in on the first spawn:
  - `RobotKit: spawning standard agents — native locomotion via WalkSession.`
  - `RobotKit navigation: native pathfinder available.` / `… unavailable; robots can stand and animate but
    cannot path until one exists.`

## SDK surface

Defined Unity-free in `src/Robotopia.Mods.Abstractions/RobotControl.cs` (positions use `Vec3`, colours use
`RobotColor`; the spawned object is exposed as `object`, a `UnityEngine.GameObject`).

```csharp
public interface IRobotAgentService
{
    bool IsAvailable { get; }
    bool IsNavigationAvailable { get; }
    IReadOnlyList<IRobotAgent> ActiveAgents { get; }

    IRobotAgent? Spawn(RobotAgentSpawnRequest request);

    bool TryGetPlayerPosition(out Vec3 position);
    bool TryGetPlayerObject(out object gameObject);  // the player GameObject, for Chase()
    bool DamagePlayer(float amount, string source);
    void SetPlayerControlsEnabled(bool enabled);

    IReachableSpawn BeginFindReachableSpawn(ReachableSpawnRequest request); // reachable-only spawn placement
}

public interface IReachableSpawn   // pollable handle; driven by the service tick
{
    bool IsComplete { get; }       // search finished (found a point or exhausted candidates)
    bool Found { get; }            // a usable point was found (valid once IsComplete)
    Vec3 Position { get; }         // ground-snapped spawn point (valid once Found)
}

public interface IRobotAgent
{
    string Id { get; }
    object GameObject { get; }        // UnityEngine.GameObject — attach your own component here
    bool IsAlive { get; }             // false once despawned/destroyed/killed (a ragdoll-stun stays alive)
    Vec3 Position { get; }            // feet/base
    Vec3 HeadPosition { get; }        // top-of-body anchor (scale-aware): headshots + world-anchored HUD
    RobotBrainMode BrainMode { get; }

    bool IsMoving { get; }
    bool HasReachedTarget { get; }
    void MoveTo(Vec3 position);        // walk to a fixed point and stop (single native walk)
    void Chase(object targetGameObject); // continuously pursue a live GameObject (native repath/track)
    void Stop();                      // stop & idle natively
    float MoveSpeed { get; set; }     // best-effort override of native gait speed (m/s); 0 = prefab default
    float TurnSpeed { get; set; }     // best-effort override of native turn speed; 0 = prefab default
    float StopDistance { get; set; }  // native minStopDistance
    RobotGait Gait { get; set; }      // Walk | Run | Sprint

    void SetTint(RobotColor color);   // material-property-block tint over all renderers
    void SetEmote(string emojiShortcode); // native facial expression
    void SetName(string name);
    void SetScale(float scale);
    void SetInteraction(RobotInteractionOptions options); // native talk, disabled talk, or custom callback

    bool ApplyDamage(float amount, RobotDamageType type, string source); // native Health.Damage
    void Kill(RobotDamageType type, string source);                      // force native death (ragdoll + cleanup)
    void Ragdoll();                   // native knockdown; self-recovers
    void Knockback(Vec3 impulse);     // native impulse (ragdolls if strong enough)

    void Despawn();
}

public enum RobotBrainMode { Dormant, Autonomous }
public enum RobotGait { Walk, Run, Sprint }
public enum RobotDamageType { Normal, Fire, Electricity, Poison, Water }  // mirrors native DamageType
public readonly struct RobotColor { float R, G, B, A; }
```

`RobotAgentSpawnRequest(Vec3 position, Vec3? facing = null)` carries the spawn pose plus `BrainMode`
(default `Dormant`), `Gait` (default `Run`), optional `MoveSpeed`/`TurnSpeed` overrides (0 = keep prefab
default), `StopDistance`, `Tint`, `Name`, `Scale` (default 1), and `Interaction` (default native talk).

## Player interactions

RobotKit agents keep the base game's **Talk to ...** prompt by default. Mods can tune or replace that surface:

```csharp
request.Interaction = RobotInteractionOptions.NativeTalkAtDistance(8f);
request.Interaction = RobotInteractionOptions.DisableNativeTalk();

request.Interaction = RobotInteractionOptions.Custom(new RobotCustomInteraction("JACK IN", ctx =>
{
    // ctx.Agent is the IRobotAgent; ctx.Hand is the native player hand transform as object.
})
{
    Distance = 10f,
    ScreenRectExpansion = 0.05f,
});
```

Custom interactions use the game's native interactable selection and prompt UI, but disable native talk for that
agent so the custom prompt is selected reliably. Call `agent.SetInteraction(...)` to change the policy after spawn.

## Reachable spawn placement

Spawning an enemy at a random point near the player is a trap: a downward raycast happily finds ground on a
rooftop, a ledge, a walled-off courtyard, or any island the player cannot walk to — the enemy then stands there
forever (it can neither reach the player nor be reached), and in a wave gamemode a single such straggler can stop
the wave from ever ending. `BeginFindReachableSpawn` solves this by reusing the **game's own pathfinder** to
confirm a candidate is reachable before you commit a robot to it.

```csharp
var search = robots.BeginFindReachableSpawn(new ReachableSpawnRequest(playerPos)
{
    MinRadius = 10f, MaxRadius = 28f, MaxCandidates = 18, HeightOffset = 0.25f,
});
// poll on later frames (the service tick advances the search across frames):
if (search.IsComplete && search.Found)
    robots.Spawn(new RobotAgentSpawnRequest(search.Position, facing) { /* … */ });
```

How it works: the service generates ring candidates around `Origin`, ground-snaps and walkability-filters each
with the native grid sampler (`RaycastedGraph.SampleAt`), then runs one `Pathfinder.Pathfind` per survivor and
accepts the first whose `Path.complete` is true — exactly the gate the native `WalkSession` uses to decide a robot
can walk to a target. Pathfinding is frame-budgeted and main-thread only, so the search is **asynchronous**: it
runs across frames under the service tick and you poll the handle (never block on it). When the scene has no
pathfinder (`IsNavigationAvailable == false`) it degrades to a best-effort grounded point with no reachability
guarantee. If no candidate is reachable, `Found` is `false` — delay and retry rather than spawning anywhere.

## Behaviour model

- **`MoveTo(point)`** runs one native walk to a fixed position and stops within `StopDistance`. `HasReachedTarget`
  flips true on arrival.
- **`Chase(targetGameObject)`** pursues a live object. Because it hands the game a live target, the native walk
  **re-paths on its own** as the target moves — you call it once (or every frame; it is cheap and idempotent for
  the same target) and the robot keeps closing to within `StopDistance`, then idles until the target moves away
  again. Get the player object from `IRobotAgentService.TryGetPlayerObject`.
- **`Stop()`** clears the intent; the robot idles natively (its own idle animation).
- The robot only walks while its native locomotion is in control (it has spawned, grounded, and is not
  ragdolling). Intents issued while it is mid-fall or ragdolled resume automatically once it recovers.

## Combat model

There is **no native robot-vs-player attack** in the game, so melee/ranged damage *to the player* stays in your
mod (detect range in your own component, then call `IRobotAgentService.DamagePlayer`). For damage *to the robot*:

- `ApplyDamage` routes through the robot's native `Health` (native hurt/death/ragdoll). Note native health
  **regen is always-on**, so an enemy with fixed hit-points is best tracked in the mod (a simple `float`) with
  `Kill(...)` called on defeat — exactly what Zombies does.
- `Kill` forces the native death immediately: the robot ragdolls and the corpse cleans up natively. `IsAlive`
  becomes `false` and the service drops the handle.
- `Ragdoll`/`Knockback` are non-lethal native reactions (the robot self-recovers).

## Robot brain queries (`IRobotBrainQueryService`)

RobotKit also publishes a second service: **ask a robot's LLM brain a structured question** and get a
machine-readable answer back, proxied through the game's own RoboAPI backend (the same inference the native
robots think with — `llama-3.3-70b` via `/agent/check3`). This is the reusable "talk to the brain" primitive
for mods that want a robot to *decide* something in its own words — persuasion, dialogue choices, a talk-down
boss, a puzzle robot — without re-deriving any token/HTTP/wire plumbing.

It follows the **same pollable-handle idiom** as `BeginFindReachableSpawn`: a brain round-trip is a network
request (typically a few hundred ms, occasionally ~1s), so you **never block a frame** — start a query, poll the
handle, read the result once `IsComplete`. The HTTP call runs off the main thread and its result is marshalled
back on the service tick, so callers stay single-threaded.

```csharp
public interface IRobotBrainQueryService
{
    bool IsAvailable { get; }                       // backend token resolvable + service live
    IRobotBrainQuery BeginQuery(BrainQueryRequest request);
}

public interface IRobotBrainQuery                   // pollable handle, driven by the service tick
{
    bool IsComplete { get; }
    bool Found { get; }                             // the brain produced a usable answer
    BrainQueryResult Result { get; }
}

// Mirrors the backend's structured-output shape, Unity-free:
new BrainQueryRequest(prompt, new[]
{
    new BrainOutputField("action", "how you react", BrainFieldType.String,
        allowedStrings: new[] { "comply", "freeze", "flee", "resist" }), // a closed enum
    new BrainOutputField("bark", "a short line you say"),                 // free text
}) { Temperature = 0.8f, Usage = "my-mod" };

// BrainQueryResult: { bool Available; bool Succeeded; IReadOnlyDictionary<string,string> Values; string? Error }
//   result.TryGet("action", out var action);  // each requested field, rendered as a string
```

Design rules the service enforces / you should follow:

- **Deterministic-first.** A brain query is a pure *enrichment* layer. Resolve your own deterministic outcome at
  frame 0 (so it is instant and works offline), then let the brain answer — or not — a beat later. Never gate core
  gameplay on the network.
- **Degrades to unavailable, never throws.** When the backend is unreachable, the player's token is missing/expired
  (a 401 invalidates the cache), or the build lacks what it needs, `IsAvailable` is `false` and a started query
  completes with `Result.Available == false`. Always have a fallback.
- **`Succeeded` means "a well-formed answer came back", not "the model approved of it".** The backend also returns
  its own `success` self-grade inside the values (the model judging whether it met your `SuccessDescription`); it is
  noisy — e.g. a hostile robot that correctly picks `resist` still self-grades `success:false`. RobotKit deliberately
  does **not** let that self-grade suppress the answer, so `result.TryGet("action"/"bark"/…)` stays readable and your
  gameplay owns the win condition. If you specifically want the self-grade, read `result.TryGet("success", …)`.
- **Cost-aware.** Each query spends a call against the **player's own** backend token (read from
  `robo_token.json`). Gate queries behind a cooldown/resource — never per-frame, never per-robot in a crowd
  (amortize one call over the group). Hardening (single-flight cap, hard ~3s timeout, length-clamped values) lives
  in the service so a slow/expensive backend can never stall a frame or run up unbounded cost.

Declare a dependency on `robotopia.robotkit >= 0.5.0` (the version that adds this service).

## Robot conversations (`IRobotConversationService`)

A **multi-turn** layer over the single-shot brain query (RobotKit **0.6.0+**): the player and a robot go back and
forth, and the robot answers *in its own words* and *chooses* a structured reaction each turn. The backend call is
stateless, so the conversation carries its own transcript and re-sends a compact history each turn — you just
`Submit` a line and poll.

```csharp
public interface IRobotConversationService
{
    bool IsAvailable { get; }
    IRobotConversation BeginConversation(RobotConversationRequest request);
}

public interface IRobotConversation                 // pollable, driven by the service tick
{
    bool IsThinking { get; }                         // a turn is in flight
    bool TurnReady { get; }                          // a fresh turn landed (latched until next Submit)
    string LastReply { get; }                        // the robot's spoken line (free text — HUD juice)
    string LastDecision { get; }                     // one of RobotConversationRequest.DecisionOptions
    int TurnCount { get; } int MaxTurns { get; } bool Ended { get; }
    void Submit(string playerText); void End();
}

var convo = svc.BeginConversation(new RobotConversationRequest(
    systemFrame: "You are an infected robot… stay in character, never break the fiction.",
    decisionOptions: new[] { "CONVERT", "STAND_DOWN", "FLEE", "REFUSE" })
{
    GroundTruthFacts = new Dictionary<string,string> { ["hp"] = "40/120", ["faction"] = "infected" },
    MaxTurns = 3, Temperature = 0.8f,
});
convo.Submit(playerLine);
// …poll each frame… when convo.TurnReady: read convo.LastReply (show it) + convo.LastDecision (drive game state)
```

**Dual channel — the engine owns the win condition.** Each turn yields a free-text `LastReply` (flavour) *and* a
`LastDecision` drawn from the closed `DecisionOptions` set. Only the decision should touch game state, and you
should **gate** it behind your own rules (e.g. only honour a powerful outcome once a disposition meter clears a
threshold) so eloquent player text moves the odds but is never a one-shot "I-win". `GroundTruthFacts` are injected
each turn as authoritative state the robot **cannot be gaslit about** (HP, faction, whether it was just shot), and
the player's line is wrapped as explicitly-untrusted input. Out-of-set / unavailable answers come back empty so you
fall back to your deterministic outcome.

## Player dialogue input — text + voice (`IPlayerDialogueInputService`)

Captures what the player *says* to a robot the same two ways the **base game** does (verified by decompile): typed
text, or **push-to-talk voice** transcribed through `/agent/stt` (16 kHz mono PCM16-LE, gzipped — the only format
the backend accepts). RobotKit **0.6.0+**.

```csharp
public interface IPlayerDialogueInputService
{
    bool IsVoiceAvailable { get; }                   // a mic exists AND the backend can transcribe
    IVoiceCapture BeginVoiceCapture();               // start recording (push-to-talk down)
}

public interface IVoiceCapture                       // pollable, driven by the service tick
{
    bool IsRecording { get; } bool IsComplete { get; } bool Found { get; }
    string Text { get; }                             // the transcript, once complete
    void Stop();   // push-to-talk released → transcribe
    void Cancel();
}
```

For the **typed** path use the shared `Robotopia.Mods.TextInputBuffer` (a Unity-free `Input.inputString`
accumulator with backspace/submit/clamp) so every mod buffers text the same way. Voice degrades gracefully: no
mic / no backend → `IsVoiceAvailable == false` and you fall back to text. Mirror the base game's UX — **Tab**
toggles text/voice, robot replies are shown as on-screen subtitles (no TTS), and recording uses a held key or an
on-screen button.

`Robotopia.Zombies`' **JACK IN** verb (v0.8.0) is the reference consumer for all three services: aim at a robot,
open a channel (the horde freezes), type or speak, and its brain decides whether to convert/stand-down/flee — with
the deterministic `OverrideDecision` "robot psychology" only seeding the persuasion gate, not authoring the outcome.

## Example

```csharp
public sealed class MyMod : IRobotopiaMod
{
    private IModContext? context;
    private IRobotAgentService? robots;

    public void OnLoad(IModContext context)
    {
        this.context = context;
        robots = context.GetService<IRobotAgentService>();   // or context.RequireService<…>()
        context.Update += OnUpdate;
    }

    private void Spawn(Vec3 where)
    {
        var agent = robots?.Spawn(new RobotAgentSpawnRequest(where)
        {
            BrainMode = RobotBrainMode.Dormant,              // mod-driven; Autonomous = native thinking NPC
            Gait = RobotGait.Run,
            StopDistance = 1.8f,
            Tint = new RobotColor(0.55f, 1f, 0.35f),         // override visuals to the extent you need
            Name = "My Enemy",
        });
        if (agent == null) return;                           // not available yet, or no prefab
        var go = (UnityEngine.GameObject)agent.GameObject;
        go.AddComponent<MyEnemyBehaviour>().Bind(agent);     // your health/score/attacks (optional)
    }

    private void OnUpdate(float dt)
    {
        if (robots != null && robots.TryGetPlayerObject(out var player))
            foreach (var bot in robots.ActiveAgents)
                bot.Chase(player);                           // native pathing tracks + routes to the player
    }

    public void OnUnload() { if (context != null) context.Update -= OnUpdate; }
}
```

Manifest: declare the dependency so RobotKit loads first.

```json
"dependencies": [{ "id": "robotopia.robotkit", "versionRange": ">=0.2.0" }],
"loadAfter": ["robotopia.robotkit"]
```

## Reference consumer

`mods/Robotopia.Zombies` is the canonical consumer. `ZombiesController` resolves `IRobotAgentService` and
`Spawn`s each wave enemy as a tinted, dormant-brain robot; `ZombieEnemyController` (attached to the spawned
robot) only does the Zombies-specific work — `Chase` the player, attack in range, mod-tracked health with hit
flashes, and a native ragdoll death via `Kill`. All movement, collision, path-finding, grounding, and animation
are the game's own.

## End-to-end verification

Build and deploy (`dotnet build mods/Robotopia.RobotKit/...`, then `tools/install-local.ps1`), launch the game,
and start a gamemode that spawns robots (e.g. pick **Zombies** from the menu). Confirm in
`…/RobotopiaModManager/logs/manager.log`:

- `Robotopia RobotKit loaded; IRobotAgentService + IRobotBrainQueryService registered (poll IsAvailable once a level is loaded).` (on boot)
- `RobotKit: spawning standard agents — native locomotion via WalkSession.` (first spawn)
- `RobotKit navigation: native pathfinder available.` (first spawn, when a pathfinder is present)
- `RobotKit: brain queries enabled — robot decisions can consult the RoboAPI backend (llama-3.3-70b).` (first live brain query)

and observe robots routing **around** walls toward the player, animating with the game's own walk blend,
stopping at the player to attack, and **ragdolling/cleaning up on death**.

## Notes & limitations

- The service degrades gracefully: all GameCode access is guarded, so a build that renames the symbols leaves
  `IsAvailable` `false` rather than throwing.
- In `Autonomous` brain mode the robot drives itself; mod movement intents (`MoveTo`/`Chase`) are inert.
- Movement tunables (gait speeds, turn speed) are best-effort overrides of the native serialized values; when a
  build does not expose them the robot uses its prefab defaults.
- The service drives navigation and the brain seam only — bespoke gameplay (factions, scoring, custom attacks)
  stays in the consumer mod (attach a component to `agent.GameObject`).
