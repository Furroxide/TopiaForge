---
title: Chronos
description: Coordinate Robotopia time effects and turns through lifetime-owned V1 leases.
---

# Chronos

Chronos is TopiaForge's optional V1 module for coordinating Robotopia time. It provides freezing, slow
motion, player exemption, dynamic drivers, and turn scheduling so mods do not fight over
Robotopia's global timing state.

## Add Chronos

```sh
topiaforge mod add chronos
topiaforge restore
```

Resolve `ITimeControlService` with `Context.RequireExtension<ITimeControlService>()`. The module
command also declares `io.github.furroxide.topiaforge.chronos` as a required runtime dependency.

## Lease model

`Freeze`, `Slow`, `ExemptPlayer`, `SetDriver`, and `BeginTurnBased` return disposable leases. The
effective world scale is derived from all active leases instead of using last-writer-wins state.
Releasing a lease restores the state implied by remaining leases; mod lifetime cleanup handles
partial loads and unload automatically.

Use Chronos service clocks for behavior that should obey world scale or continue while Robotopia's
world is paused. UI, input, and deadlines generally use control time. Simulation actors and world
timers use world time.

## Turn scheduling

`ITurnScheduler` registers typed `TurnActorId` values and advances them using bounded options.
Dispose the scheduler to end the mode and release its freeze. Keep actor decisions asynchronous and
cancellable; never block a frame waiting for a turn action.

## Graceful availability

Check `IsAvailable` and explain an unavailable Robotopia binding through runtime capability
metadata. A mod should disable only its time-bending feature, release any already-acquired leases,
and preserve ordinary Robotopia gameplay.

See [Specialist modules](Modules.md#chronos) and the generated C# API reference for requests,
drivers, signals, modes, and scheduler members.
