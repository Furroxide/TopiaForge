# TopiaForge.Mods.Testing

`TopiaForge.Mods.Testing` is the runner-neutral test kit for TopiaForge V1 mods. Use it from NUnit, xUnit,
MSTest, or a custom runner; the package has no dependency on any test framework, Unity, Robotopia, or a game
installation.

```csharp
var context = new FakeModContext();
var runner = new ModLifecycleRunner(new MyMod(), context);

runner.Load();
context.Input.SetValue("activate", 1f);
context.AdvanceFrame(TimeSpan.FromMilliseconds(16));
runner.Unload();

context.AssertNoLeaks();
```

The fake context provides captured logging, in-memory configuration/files/storage, named input, virtual time and
scheduling, controllable scene completion, player/entity/physics state, and all other non-null V1 core services.
SDK-owned resources are released in reverse order on ordinary unload and partial load failure.

Ordinary `new FakeModContext()` and standalone service constructors do not check thread access. For a test
that needs to catch accidental worker access, use `FakeModContext.CreateWithThreadChecks()`. The thread
calling that factory owns the checks for that context; separately created contexts have independent owners.

This opt-in mode reports `TFSDK100` for off-thread calls to the five `IEntityService` operations and to
`IUiService.ApplyAccessibility`, `CreateSurface`, `ShowModal`, `ShowToast`, `ClockTime` and `Labels`.
Fixture helpers, returned handles and other services retain their existing behavior. It checks live
engine-facing entry points, not every scoped runtime lifetime rule: a stopped strict context's
`TryGetTransform` returns `false` and identity before the thread check, but other operations retain the
fake's existing stopping behavior. For example, `SetTransform` and `Destroy` do not reproduce the scoped
runtime's early `Cancelled` result after stop.

Drive strict test frames and callbacks on the thread that created the context. In the runtime,
`Context.Scheduler.NextFrame` accepts worker registration and runs the callback on the game thread.
In a test, let the worker finish queuing before advancing the fake frame on the owner thread; the
fake scheduler does not support concurrent registration and frame driving. The factory installs no
synchronization context and does not pump frames or move continuations between threads.

`GamemodeSessionRunner` preserves controller ordering and cleanup, but does not reproduce the runtime's
host dispatch of asynchronous controller callbacks. After a pending server start, its participant start
may run on a worker. Use its default permissive contexts for asynchronous runner tests. Use thread-checked
contexts in tests whose controller calls, frame driving and completion callbacks you explicitly keep on
the creating thread; do not use them to infer runtime dispatch behavior.

`FakeUiService` captures safe declarative trees. Its `FakeUiSurface` can invoke buttons, toggles,
sliders, text input, dropdowns, virtual-list selections, and graph selection/edit/viewport requests
by stable id, inspect the resulting state, replace content, raise dismissal when hidden, and assert
that one failing callback did not starve later subscribers.

Specialist modules have first-class, engine-free fakes too:

- `FakeRobotKit` groups agent, objective, brain-query, conversation, and voice-input fakes. Brain
  queries can be held pending and completed or failed explicitly. `Agents.IsClient = true` models safe
  client spawning. `Agents.RunAsGuest(() => ...)` models a host callback acting for a guest: Autonomous
  spawns and native-brain wake calls return `NotAuthoritative`, while Dormant spawns remain available.
  Remote `FakePlayerDirectory` surface actions apply guest origin automatically. Use
  `FakeUiSurface.DismissFromPlayer()` for a player dismissal and `Hide()` for a host-initiated hide.
  Nested local callbacks retain guest origin; a captured context retains it on the originating thread,
  and returning or throwing restores the caller's scope. Construct the agent service and invoke simulated
  callbacks on the same thread: `RunAsGuest`, and spawn or native-brain calls made on another thread while
  a guest scope is visible, throw `InvalidOperationException` rather than silently acting as the host.
  `NextFrame`, `After` and `Every` work that the deterministic scheduler receives inside a guest scope runs
  with that guest's origin; a continuation after `await DelayAsync(...)` resumes on a pool thread, so schedule
  guest follow-ups with `NextFrame` or `After`. Fake modal completions need an explicit `Agents.RunAsGuest` scope; only
  remote surface actions receive guest origin automatically.
- `FakeCreatorContentService`, `FakeCreatorMutationSafetyService`,
  `FakeCreatorProjectLibrary`, and `FakeCreatorSceneTarget` cover catalog registrations, owned
  spawns, temporary native edits, persistence-isolation leases, and local event projects.
- `FakeWorldGamemodeService` owns typed registrations and lets tests pause and complete a world load.
- `FakeGamemodeSession` and `FakeGamemodeParticipantSession` hand a gamemode's server and participant controllers
  their prepared session. `GamemodeSessionRunner` starts and stops a gamemode by the runtime's rules for single
  player, a host or a joined player, so a test sees exactly which controllers run where and in what order.
- `FakeTimeControlService` derives freeze/slow state and advances scaled and control clocks only on request.
- `FakePromptOverrideRegistry` reproduces deterministic priority and conflict selection.

Pass `context.Lifetime` to each fake. This gives module handles the same reverse-order unload and failed-load
cleanup semantics as a live mod. Register a fake in `context.Extensions` only when the mod under test resolves it
through `Context.Extensions`.

The NuGet package keeps compile-time SDK contracts under `ref/` and carries the Unity-free contract
implementations under `lib/` exclusively for test execution. Production mod packages must not copy those runtime
assemblies; Robotopia's loader supplies them.
