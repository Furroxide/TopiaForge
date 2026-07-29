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

`FakeUiService` captures safe declarative trees. Its `FakeUiSurface` can invoke buttons, toggles,
sliders, text input, dropdowns, virtual-list selections, and graph selection/edit/viewport requests
by stable id, inspect the resulting state, replace content, raise dismissal when hidden, and assert
that one failing callback did not starve later subscribers.

Specialist modules have first-class, engine-free fakes too:

- `FakeRobotKit` groups agent, objective, brain-query, conversation, and voice-input fakes. Brain
  queries can be held pending and completed or failed explicitly.
- `FakeCreatorContentService`, `FakeCreatorMutationSafetyService`,
  `FakeCreatorProjectLibrary`, and `FakeCreatorSceneTarget` cover catalog registrations, owned
  spawns, temporary native edits, persistence-isolation leases, and local event projects.
- `FakeWorldGamemodeService` owns typed registrations and lets tests pause and complete a world load.
- `FakeTimeControlService` derives freeze/slow state and advances scaled and control clocks only on request.
- `FakePromptOverrideRegistry` reproduces deterministic priority and conflict selection.
- `FakeUgcLiveSyncService` owns sessions and asset overrides and injects snapshot, patch, and error events.

Pass `context.Lifetime` to each fake. This gives module handles the same reverse-order unload and failed-load
cleanup semantics as a live mod. Register a fake in `context.Extensions` only when the mod under test resolves it
through `Context.Extensions`.

The NuGet package keeps compile-time SDK contracts under `ref/` and carries the Unity-free contract
implementations under `lib/` exclusively for test execution. Production mod packages must not copy those runtime
assemblies; Robotopia's loader supplies them.
