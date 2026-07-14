# Chronos — game-time control framework

`TopiaForge.Chronos` is a framework mod that publishes **`ITimeControlService`** — the single, leak-proof
authority over Unity's `Time.timeScale` / `Time.fixedDeltaTime` for the whole mod ecosystem. It's the reusable
foundation for time-bending gamemodes: a hard **freeze** (turn-based / RPG pause / freeze-to-talk), a
continuous or input-driven **slow-mo** (Superhot), bounded **stepping**, and a full **turn scheduler**.

Resolve it with `context.GetService<ITimeControlService>()` and declare a dependency on `io.github.furroxide.topiaforge.chronos`
(`loadAfter` it). All ops degrade gracefully — when the engine hooks can't be resolved, `IsAvailable` is
`false` and effects become no-ops rather than throwing.

## Why a single owner (the leak the studio hit)

`Time.timeScale` is a global mutable singleton. The game's own slow-mo was cut because it was a fire-and-forget
write with no owner and no reset — it leaked into menus and the next gamemode. Chronos fixes that structurally:

- **One writer.** Chronos is the only mod-side writer of `timeScale`/`fixedDeltaTime`.
- **Derived, never last-writer-wins.** Every effect is a ref-counted, owner-tagged **lease**; the effective
  scale is derived from *all* active leases — any freeze ⇒ `0`, else the product of slow factors × the driver
  base. Two flows can't clobber each other.
- **`fixedDeltaTime` co-scaled off a once-captured baseline** (`base × max(scale, floor)`), never the live
  value — so the physics step slows smoothly in slow-mo and can't drift across gamemode loads.
- **Force-reset on every teardown** — scene change, owner teardown (`UnregisterOwner` releases *only* that
  mod's leases), dispose, and even a thrown frame (try/finally around the tick). Leak-into-the-next-gamemode is
  structurally impossible.
- **Coexists with a native pause.** If Chronos sees an external `timeScale==0` it didn't set (the game's
  `FreezeGame`/pause menu), it yields instead of fighting.

## Two clocks

Read these instead of `Time.*` directly so your code freezes (or doesn't) with the world correctly:

- **WorldClock** — *scaled* (`WorldDeltaTime`/`WorldTime`): native robots, physics, sim entities.
- **ControlClock** — *unscaled* (`ControlDeltaTime`/`ControlTime`): the player (when exempt), HUD, UI,
  countdowns, and the drivers themselves.

## Surface

```csharp
public interface ITimeControlService
{
    bool IsAvailable { get; }
    float WorldScale { get; } float WorldDeltaTime { get; } float WorldTime { get; }
    float ControlDeltaTime { get; } float ControlTime { get; }
    bool IsFrozen { get; } TimeMode Mode { get; }

    ITimeLease Freeze(string usage, bool suspendPlayer = false); // scale 0; optionally disable the FPS controller + free the cursor
    ITimeLease Slow(string usage, float scale);                  // steady slow-mo (leases multiply)
    ITimeLease ExemptPlayer(string usage);                       // keep the player full-speed while the world is slow (Superhot)
    ITimeLease SetDriver(string usage, ITimeDriver driver);      // recompute the scale each control tick (e.g. Superhot ramp)
    void Step(float seconds); void StepFixed(int ticks);         // advance a frozen world by a bounded slice (RTwP / turn)
    ITurnScheduler BeginTurnBased(string usage, TurnSchedulerOptions options);
    void ForceReset();
}
```

Every effect returns an `ITimeLease` — **dispose it to remove the effect** (idempotent). Leases are the whole
safety story.

## The three modes

- **Superhot** — `SetDriver(new SuperhotTimeDriver())` + `ExemptPlayer(...)`. The driver ramps the world scale
  from how much the player is moving/aiming/firing (idle → ~0.03 floor, acting → 1.0, asymmetric: snap up, ease
  down), and the exemption scales the native FPS controller's move speed up by `1/scale` so *you* stay
  full-speed (look is already frame-based, so it needs no compensation). `SuperhotTimeDriver` is a pure,
  Unity-free `ITimeDriver` in the SDK — reuse or replace it.
- **RPG real-time-with-pause** — toggle a `Freeze(...)` lease; the player command UI reads the ControlClock so
  it stays live while the world is at 0; `Step()` advances "one beat".
- **Turn-based** — `BeginTurnBased(...)` hard-freezes the world and returns an `ITurnScheduler` that runs
  registered actors in initiative/energy order, **lifting time only for the actor that is acting** (others
  idle), then re-freezing. Drive it: while `State == AwaitingAction`, command `CurrentActor` (and make sure no
  other actor has a queued move — it'd advance during the lift), call `BeginAction()`, then `EndAction()` when
  it finishes. Dispose the scheduler to end turn-based mode.

## Reference consumer — Zombies

`TopiaForge.Zombies` (v0.9.0) dogfoods Chronos two ways: the **JACK-IN** freeze-to-talk acquires a `Freeze`
lease (so native robots + physics halt at `timeScale 0`, not just a per-entity stop), and a **`superhotMode`**
config toggle acquires a `SetDriver(SuperhotTimeDriver)` + `ExemptPlayer` lease for a "the horde only moves
when you do" mode. Both are released through the same lease discipline on every teardown.

## Limitations (from the GameCode decompile)

- The player FPS controller runs on *scaled* time and has no unscaled path, so Superhot requires the
  reflect-scale exemption (guarded; degrades to "player also slowed" if the controller fields can't be
  resolved between builds).
- `timeScale` is global: you can't exempt one *native* robot from slow-mo (only the player, via reflection,
  and your own mod components via the ControlClock). The native engine only exposes a per-robot opt-out for the
  current conversation target.
- `timeScale=0` halts `FixedUpdate`/coroutines — Chronos timers and any UI must live on the ControlClock; the
  turn scheduler steps by lifting time rather than relying on a dead `FixedUpdate`.
- Audio: native FMOD pauses wholesale and the local TTS already reads `timeScale`; Chronos does not pitch-shift
  audio in slow-mo in this version.
