---
title: Test a mod
description: Run fast lifecycle tests with TopiaForge.Mods.Testing and NUnit.
---

# Test a mod

Every V1 scaffold includes a small NUnit project that references `TopiaForge.Mods.Testing` version
`1.0.0-rc.1`. The testing library is runner-neutral, so an existing project may use another test runner.
Tests run without a Robotopia installation or a Unity editor.

## Arrange, load, drive, unload

Create a `FakeModContext`, configure the fakes your behavior needs, and load the real mod class with
`ModLifecycleRunner`. Drive input, time, scenes, physics, or controlled operations explicitly.
Always unload and finish with `context.AssertNoLeaks()`.

This complete example is inserted from the compiled minimal scaffold:

<!-- topiaforge-snippet path="templates/mod/minimal/tests/{{ASSEMBLY_NAME}}.Tests/{{TYPE_NAME}}ModTests.cs" -->

Run the generated tests from the mod root:

```sh
dotnet test --configuration Release
```

`topiaforge dev` runs this test step before it packs or installs the mod.

## Included test doubles

- `FakeModContext` wires every core V1 service and exposes strongly typed fake instances.
- `CapturedModLogger` records levels, messages, and exception chains.
- `InMemoryModConfigService`, `InMemoryLocalModStorageService`, and `InMemoryModFiles` keep tests isolated.
- `FakeInputService` drives named actions and transition states.
- `DeterministicGameTime` and `DeterministicModScheduler` advance only when the test asks.
- `FakeSceneService`, `FakeLocalPlayerService`, `FakeEntityService`, and `FakePhysicsService` control gameplay state.
- `FakeAssetService`, `FakeAudioService`, and `FakeUiService` expose active handles and captured requests;
  `FakeUiSurface` also finds controls by id, invokes form/list/graph interactions, captures state and
  dismissal, and isolates callback failures.
- `FakeInteractionService` and `FakeItemService` cover interactables and held-item flows.
- `FakeExtensionService` registers Creator Content, RobotKit, Worlds, Chronos, Prompts,
  multiplayer, or custom providers.
- `FakeRobotKit` covers agents, objectives, controlled brain queries, conversations, and voice input.
- `FakeCreatorContentService`, `FakeCreatorMutationSafetyService`,
  `FakeCreatorProjectLibrary`, and `FakeCreatorSceneTarget` cover authenticated catalog
  registrations, owned spawns, exclusive temporary edits, fail-closed persistence isolation, and
  local event-project validation/storage.
- `FakeWorldGamemodeService`, `FakeTimeControlService`, `FakePromptOverrideRegistry`, and
  provide deterministic specialist-module behavior and leak-observable handles;
  use `TryGetWorldContent` to exercise a registered custom-world factory without an engine installation.
- `ControlledOperation<T>` lets a test choose when asynchronous work completes, fails, or cancels.

## Test failure paths

Cover at least these lifecycle paths for every acquired resource:

1. Successful load, behavior, unload, and reload.
2. A thrown or failed operation halfway through load.
3. Shutdown while asynchronous work is pending.
4. Early handle disposal followed by lifetime cleanup.
5. One event subscriber throwing while another still receives the event.

For runtime-level event tests, also cover the deterministic failure circuit: a successful callback resets
the consecutive-failure streak, three consecutive failures disable only that subscription, and disposing and
re-subscribing rearms it. Assert that diagnostics name the callback and phase without emitting one log entry
per failed frame, including when failures alternate with successes; sustained healthy delivery is required to
rearm failure diagnostics.

`ModLeakAssertions` reports active actions, subscriptions, registrations, handles, surfaces,
scheduled work, and extension providers together. A leak failure therefore points to the resource
family that remained live instead of merely timing out.
