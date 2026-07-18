using System;
using System.Collections.Generic;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static class TestingKitTests
    {
        public static void Run()
        {
            TestInMemoryAuthoringServices();
            TestDeterministicGameplayServices();
            TestSchedulerAndControlledCompletion();
            TestExpectedCancellationResults();
            TestBundleWorldContentOwnership();
            TestSpecialistModuleFakes();
            TestCompleteCoreServiceContext();
            TestDeclarativeUiComposition();
            TestLifecycleAndLeaks();
            TestPartialLoadFailureCleanup();
            TestEveryOwnedResourceAcrossTenReloads();
            TestEveryOwnedResourceAfterPartialLoadFailure();
            Console.WriteLine("TestingKitTests passed.");
        }

        private static void TestInMemoryAuthoringServices()
        {
            using var context = new FakeModContext();
            var definition = new ConfigDefinition<ProbeConfig>(
                2,
                () => new ProbeConfig { Value = 4 },
                value => value.Value >= 0
                    ? OperationResult<bool>.Success(true)
                    : OperationResult<bool>.Failure(
                        ModErrorCode.InvalidArgument,
                        "Value cannot be negative."),
                (_, value) => OperationResult<ProbeConfig>.Success(
                    new ProbeConfig { Value = value.Value + 1 }));
            var defaults = context.Config.Load(definition);
            Assert(defaults.Succeeded && defaults.Value!.Value == 4,
                "config creates and validates defaults before a value is saved");
            var saved = new ProbeConfig { Value = 9 };
            Assert(context.Config.Save(definition, saved).Succeeded &&
                   ReferenceEquals(context.Config.Load(definition).Value, saved),
                "config validates and returns the stored typed value");
            context.Config.Seed(1, new ProbeConfig { Value = 2 });
            Assert(context.Config.Load(definition).Value!.Value == 3,
                "config migration is controlled with an explicit stored schema");

            Assert(context.Storage.Save("progress", saved).Succeeded &&
                   context.Storage.Load<ProbeConfig>("progress").Value!.Value == 9,
                "typed storage round-trips values without touching disk");
            Assert(context.Storage.SetStoryFlag("chapter-one/terminal-opened", false).Succeeded &&
                   context.Storage.TryGetStoryFlag("chapter-one/terminal-opened", out var storyFlag) &&
                   !storyFlag,
                "mod-owned story flags preserve an explicitly stored false value");
            Assert(context.Storage.DeleteStoryFlag("chapter-one/terminal-opened").Succeeded &&
                   !context.Storage.TryGetStoryFlag("chapter-one/terminal-opened", out _),
                "mod-owned story flags can be removed without exposing save paths");
            Assert(context.Files.WriteDataTextAsync("nested/save.txt", "ready").Result.Succeeded &&
                   context.Files.ReadDataTextAsync("nested/save.txt").Result.Value == "ready",
                "content-based data files round-trip without revealing paths");
            context.FileSystem.SetPackageText("content/story.txt", "chapter one");
            Assert(context.Files.ReadPackageTextAsync("content/story.txt").Result.Value == "chapter one",
                "tests can seed package content directly");
            AssertThrows<ArgumentException>(() => context.Files.PackageFileExists("../escape.dll"),
                "fake content operations reject traversal");

            context.Logger.Info("started");
            context.Logger.Error(new InvalidOperationException("boom"), "failed");
            Assert(context.Logger.Entries.Count == 2 &&
                   context.Logger.Count(CapturedLogLevel.Error) == 1 &&
                   context.Logger.Entries[1].Exception is InvalidOperationException,
                "captured logger retains severity, order, and exceptions");
        }

        private static void TestDeterministicGameplayServices()
        {
            using var context = new FakeModContext();
            var inputResult = context.Input.RegisterAction(new InputActionDefinition(
                "activate",
                "Activate",
                new[] { InputBinding.Key("F") }));
            Assert(inputResult.TryGetValue(out var inputHandle) && inputHandle is FakeInputAction,
                "input registration returns a typed successful operation result");
            var input = (FakeInputAction)inputHandle!;
            var pressedDuringFrame = false;
            context.Events.SubscribeUpdate(_ => pressedDuringFrame = input.WasPressed);
            context.Input.SetValue("activate", 1f);
            context.AdvanceFrame(TimeSpan.FromMilliseconds(16));
            Assert(pressedDuringFrame && input.IsHeld && !input.WasPressed,
                "input edges remain visible during a frame and clear afterwards");

            context.Input.IsUiFocused = true;
            Assert(!input.IsHeld && input.Value == 0f,
                "UI focus suppresses actions that opt into suppression");

            var alternateResult = context.Input.RegisterAction(new InputActionDefinition(
                "alternate",
                "Alternate",
                new[] { InputBinding.Key("F"), InputBinding.GamepadButton(InputGamepadButton.West) }));
            Assert(alternateResult.TryGetValue(out var alternate),
                "a second uniquely named input action registers successfully");
            Assert(context.Input.GetConflicts().Count == 2,
                "input conflicts report the shared physical control for both affected actions");
            Assert(alternate!.Rebind(new[] { InputBinding.GamepadAxis(InputGamepadAxis.RightTrigger) }).Succeeded &&
                   context.Input.GetConflicts().Count == 0,
                "runtime rebinding resolves conflicts without changing the action identity");
            Assert(alternate.ResetBindings().Succeeded && alternate.Bindings.Count == 2,
                "actions restore keyboard, mouse, or gamepad defaults deterministically");
            var duplicate = context.Input.RegisterAction(new InputActionDefinition(
                "alternate",
                "Duplicate alternate",
                new[] { InputBinding.Key("G") }));
            Assert(!duplicate.Succeeded && duplicate.ErrorCode == ModErrorCode.Conflict,
                "duplicate input names return a stable conflict instead of throwing");

            var snapshot = new PlayerSnapshot(
                new Vec3(1f, 2f, 3f),
                new Ray(Vec3.Zero, new Vec3(0f, 0f, 1f)));
            context.Player.Snapshot = snapshot;
            Assert(context.Player.TryGetSnapshot(out var observed) && ReferenceEquals(observed, snapshot),
                "player snapshots are set explicitly");
            context.Player.Health = new PlayerHealthSnapshot(75f, 100f);
            Assert(context.Player.Damage(new PlayerDamageRequest(30f, "test.hazard")).Value!.Current == 45f &&
                   context.Player.Heal(10f, "test.reward").Value!.Current == 55f,
                "player health damage and healing are deterministic and bounded");
            var control = context.Player.AcquireControl("testing");
            Assert(control.Succeeded && context.Player.ActiveControlLeaseCount == 1,
                "player-control leases are observable");

            var entity = context.Entities.Create("Cube", new Vec3(0f, 0f, 2f));
            context.Physics.RaycastHit = new PhysicsHit(
                entity,
                entity.Position,
                new Vec3(0f, 1f, 0f),
                2f);
            Assert(context.Physics.TryRaycast(snapshot.AimRay, 5f, out var hit) && ReferenceEquals(hit!.Entity, entity),
                "physics returns the configured live hit");
            context.Physics.SphereCastHit = context.Physics.RaycastHit;
            context.Physics.OverlapEntities.Add(entity);
            Assert(context.Physics.TrySphereCast(snapshot.AimRay, 0.5f, 5f, out var sphereHit) &&
                   ReferenceEquals(sphereHit!.Entity, entity) &&
                   context.Physics.Overlap(new Bounds(entity.Position, new Vec3(2f, 2f, 2f))).Count == 1,
                "shape casts and bounded overlap queries use opaque live entities");
            var transformed = new TransformState(
                new Vec3(1f, 2f, 3f),
                new Quat(0f, 0f, 0f, 1f),
                new Vec3(2f, 2f, 2f));
            Assert(context.Entities.SetTransform(entity, transformed).Succeeded &&
                   context.Entities.TryGetTransform(entity, out var readTransform) &&
                   readTransform.Equals(transformed) &&
                   context.Entities.Query(new EntityQuery(
                       center: transformed.Position,
                       radius: 1f,
                       nameContains: "cub",
                       maximumResults: 1)).Count == 1,
                "entity transform and bounded query APIs require no engine handles");
            var acquired = context.Entities.AcquireMotion(entity);
            Assert(acquired.Succeeded, "entity motion can be acquired deterministically");
            var motion = acquired.Value!;
            var moved = motion.MoveToward(new Vec3(0f, 0f, 4f), 10f, 10f, 20f, 0.1f);
            Assert(moved.Succeeded && context.Entities.ActiveMotionCount == 1,
                "motion operations update fake entity state");
            motion.Throw(new Vec3(0f, 0f, 1f), 8f);
            Assert(context.Entities.ActiveMotionCount == 0,
                "throwing releases exclusive motion ownership");
            Assert(context.Entities.Destroy(entity).Succeeded && !entity.IsAlive,
                "owned entities can be destroyed through the safe entity service");
        }

        private static void TestSchedulerAndControlledCompletion()
        {
            using var context = new FakeModContext();
            var order = new List<string>();
            Assert(context.Scheduler.NextFrame(() => order.Add("frame")).Succeeded,
                "next-frame work returns a successful operation result");
            Assert(context.Scheduler.After(TimeSpan.FromSeconds(2), () => order.Add("after")).Succeeded,
                "delayed work returns a successful operation result");
            var delay = context.Scheduler.DelayAsync(TimeSpan.FromSeconds(2));
            context.AdvanceFrame(TimeSpan.FromSeconds(1));
            Assert(string.Join(",", order) == "frame" && !delay.IsCompleted,
                "virtual time does not complete future work early");
            context.AdvanceFrame(TimeSpan.FromSeconds(1));
            Assert(string.Join(",", order) == "frame,after" && delay.IsCompletedSuccessfully &&
                   delay.Result.Succeeded,
                "virtual time completes same-deadline work in registration order");

            using var controlled = new ControlledOperation<string>(context.Lifetime.StoppingToken);
            Assert(!controlled.Task.IsCompleted, "controlled operations begin pending");
            Assert(controlled.Fail(ModErrorCode.Unavailable, "offline") &&
                   controlled.Task.Result.ErrorCode == ModErrorCode.Unavailable,
                "controlled operations expose expected failure completion");

            var cancelled = context.Scheduler.DelayAsync(TimeSpan.FromDays(1), CancellationToken.None);
            context.Dispose();
            Assert(cancelled.IsCompletedSuccessfully &&
                   cancelled.Result.ErrorCode == ModErrorCode.Cancelled &&
                   context.Scheduler.PendingCount == 0,
                "lifetime shutdown completes pending delays as stable cancellation failures without leaks");
            var rejectedSchedule = context.Scheduler.NextFrame(() => { });
            var rejectedInput = context.Input.RegisterAction(new InputActionDefinition(
                "after-stop",
                "After stop",
                new[] { InputBinding.Key("F11") }));
            var rejectedLocalization = context.Localization.Register(new LocalizationCatalog(
                "en",
                new Dictionary<string, string> { ["after-stop"] = "After stop" }));
            Assert(!rejectedSchedule.Succeeded && rejectedSchedule.ErrorCode == ModErrorCode.Cancelled &&
                   !rejectedInput.Succeeded && rejectedInput.ErrorCode == ModErrorCode.Cancelled &&
                   !rejectedLocalization.Succeeded && rejectedLocalization.ErrorCode == ModErrorCode.Cancelled,
                "post-stop registrations return stable cancellation failures instead of throwing");
        }

        private static void TestExpectedCancellationResults()
        {
            var context = new FakeModContext();
            using var caller = new CancellationTokenSource();
            var trackedBeforeDelay = context.Lifetime.TrackedResourceCount;
            var delay = context.Scheduler.DelayAsync(TimeSpan.FromHours(1), caller.Token);
            Assert(context.Scheduler.PendingCount == 1, "caller-owned delays remain pending before cancellation");
            caller.Cancel();
            Assert(delay.IsCompletedSuccessfully && delay.Result.ErrorCode == ModErrorCode.Cancelled &&
                   context.Scheduler.PendingCount == 0 &&
                   context.Lifetime.TrackedResourceCount == trackedBeforeDelay,
                "caller cancellation completes scheduler tasks normally and releases their lifetime entry");

            using var alreadyCancelled = new CancellationTokenSource();
            alreadyCancelled.Cancel();
            var token = alreadyCancelled.Token;
            context.Scenes.CompleteLoadsImmediately = false;
            var scene = context.Scenes.LoadAsync(new SceneLoadRequest("CancelledRoom"), token);
            var bundle = context.Assets.LoadBundleAsync("content/cancelled.bundle", token);
            var file = context.Files.ReadPackageBytesAsync("content/cancelled.txt", token);
            var item = context.Items.GiveAsync(new ItemGrantRequest("cancelled.item"), token);
            Assert(scene.IsCompletedSuccessfully && scene.Result.ErrorCode == ModErrorCode.Cancelled &&
                   bundle.IsCompletedSuccessfully && bundle.Result.ErrorCode == ModErrorCode.Cancelled &&
                   file.IsCompletedSuccessfully && file.Result.ErrorCode == ModErrorCode.Cancelled &&
                   item.IsCompletedSuccessfully && item.Result.ErrorCode == ModErrorCode.Cancelled &&
                   context.Scenes.PendingLoadCount == 0 && context.Assets.ActiveBundleCount == 0,
                "core async fakes represent expected cancellation as operation results without allocating resources");

            using var controlledToken = new CancellationTokenSource();
            using var controlled = new ControlledOperation<string>(controlledToken.Token);
            controlledToken.Cancel();
            Assert(controlled.Task.IsCompletedSuccessfully &&
                   controlled.Task.Result.ErrorCode == ModErrorCode.Cancelled,
                "controlled operations model expected cancellation without cancelled tasks");

            var worlds = new FakeWorldGamemodeService(context.Lifetime);
            worlds.RegisterWorld(new WorldDefinition("cancel.world", "Cancel", "Cancel", sceneName: "Cancel"));
            worlds.RegisterGamemode(new GamemodeDefinition("cancel.mode", "Cancel", "Cancel"));
            var world = worlds.LoadAsync(new WorldLoadRequest("cancel.world", "cancel.mode"), token);
            var robotKit = new FakeRobotKit(context.Lifetime);
            var query = robotKit.BrainQueries.QueryAsync(new BrainQueryRequest(
                "Cancel",
                new[] { new BrainOutputField("answer", "Answer") }), token);
            var reachable = robotKit.Agents.FindReachableSpawnAsync(
                new ReachableSpawnRequest(Vec3.Zero),
                token);
            Assert(world.IsCompletedSuccessfully && world.Result.ErrorCode == ModErrorCode.Cancelled &&
                   query.IsCompletedSuccessfully && query.Result.ErrorCode == ModErrorCode.Cancelled &&
                   reachable.IsCompletedSuccessfully && reachable.Result.ErrorCode == ModErrorCode.Cancelled &&
                   !worlds.HasPendingLoad && robotKit.BrainQueries.PendingQueryCount == 0,
                "specialist async fakes use the same stable cancellation convention");

            context.Dispose();
            context.AssertNoLeaks();
        }

        private static void TestBundleWorldContentOwnership()
        {
            var successContext = new FakeModContext();
            var factory = new BundleWorldContent(
                successContext.Assets,
                "worlds/test.bundle",
                "WorldRoot",
                TransformState.Identity);
            var created = factory.CreateAsync().Result;
            Assert(created.TryGetValue(out var content) && content.IsAlive &&
                   successContext.Assets.ActiveBundleCount == 1 &&
                   successContext.Assets.ActivePrefabCount == 1 &&
                   successContext.Assets.ActiveSpawnCount == 1,
                "bundle world content retains every handle needed by the live SDK entity tree");
            content!.Dispose();
            Assert(!content.IsAlive && successContext.Assets.ActiveSpawnCount == 0 &&
                   successContext.Assets.ActivePrefabCount == 0 &&
                   successContext.Assets.ActiveBundleCount == 0,
                "disposing bundle world content releases spawn, prefab, and bundle handles");
            successContext.Dispose();
            successContext.AssertNoLeaks();

            var prefabFailureContext = new FakeModContext();
            prefabFailureContext.Assets.PrefabLoadErrorCode = ModErrorCode.NotFound;
            var prefabFailure = new BundleWorldContent(
                prefabFailureContext.Assets,
                "worlds/test.bundle",
                "MissingRoot",
                TransformState.Identity).CreateAsync().Result;
            Assert(prefabFailure.ErrorCode == ModErrorCode.NotFound &&
                   prefabFailureContext.Assets.ActiveBundleCount == 0 &&
                   prefabFailureContext.Assets.ActivePrefabCount == 0 &&
                   prefabFailureContext.Assets.ActiveSpawnCount == 0,
                "prefab load failure releases the bundle acquired earlier in world creation");
            prefabFailureContext.Dispose();
            prefabFailureContext.AssertNoLeaks();

            var spawnFailureContext = new FakeModContext();
            spawnFailureContext.Assets.SpawnErrorCode = ModErrorCode.External;
            var spawnFailure = new BundleWorldContent(
                spawnFailureContext.Assets,
                "worlds/test.bundle",
                "WorldRoot",
                TransformState.Identity).CreateAsync().Result;
            Assert(spawnFailure.ErrorCode == ModErrorCode.External &&
                   spawnFailureContext.Assets.ActiveBundleCount == 0 &&
                   spawnFailureContext.Assets.ActivePrefabCount == 0 &&
                   spawnFailureContext.Assets.ActiveSpawnCount == 0,
                "spawn failure releases both prefab and bundle handles acquired earlier");
            spawnFailureContext.Dispose();
            spawnFailureContext.AssertNoLeaks();
        }

        private static void TestSpecialistModuleFakes()
        {
            var context = new FakeModContext();

            var prompts = new FakePromptOverrideRegistry(context);
            var firstPrompt = prompts.Register(new PromptOverrideRequest("robot.greeting", "HELLO", priority: 1));
            var secondPrompt = prompts.Register(new PromptOverrideRequest("robot.greeting", "WELCOME", priority: 2));
            Assert(firstPrompt.Succeeded && secondPrompt.Succeeded &&
                   prompts.TryGetEffectiveOverride("robot.greeting", out var prompt) &&
                   prompt!.ReplacementText == "WELCOME" && prompts.GetConflicts().Count == 1,
                "prompt fake resolves deterministic priority conflicts");

            var worlds = new FakeWorldGamemodeService(context.Lifetime)
            {
                AutoCompleteLoads = false,
            };
            var worldContent = new BundleWorldContent(
                context.Assets,
                "worlds/testing.bundle",
                "WorldRoot",
                TransformState.Identity);
            var worldRegistration = worlds.RegisterWorld(new WorldDefinition(
                "test.world",
                "Test World",
                "Deterministic",
                sceneName: "TestScene"),
                worldContent);
            var modeRegistration = worlds.RegisterGamemode(new GamemodeDefinition(
                "test.mode",
                "Test Mode",
                "Deterministic"));
            var sessionChanged = 0;
            worlds.SessionChanged += _ => sessionChanged++;
            var load = worlds.LoadAsync(new WorldLoadRequest("test.world", "test.mode"));
            Assert(worldRegistration.Succeeded && modeRegistration.Succeeded &&
                   worlds.TryGetWorldContent("test.world", out var registeredContent) &&
                   ReferenceEquals(registeredContent, worldContent) &&
                   !load.IsCompleted && worlds.HasPendingLoad && worlds.CompletePendingLoad() &&
                   load.Result.Succeeded && sessionChanged == 1,
                "world fake exposes controlled asynchronous completion and session events");

            var chronos = new FakeTimeControlService(context.Lifetime);
            var freezeResult = chronos.Freeze("test");
            Assert(freezeResult.TryGetValue(out var freeze) && chronos.IsFrozen &&
                   chronos.Step(0.1f).Value == true,
                "Chronos fake derives frozen state and records bounded stepping");
            freeze!.Dispose();
            var slowResult = chronos.Slow("test", 0.5f);
            chronos.Advance(0.2f);
            Assert(slowResult.Succeeded && Math.Abs(chronos.WorldScale - 0.5f) < 0.001f &&
                   Math.Abs(chronos.WorldDeltaTime - 0.1f) < 0.001f,
                "Chronos fake advances scaled and control clocks deterministically");
            var turnResult = chronos.BeginTurnBased("test-turns", new TurnSchedulerOptions());
            Assert(turnResult.TryGetValue(out var turns) &&
                   turns.Register(new TurnActorId("probe"), 1f).Succeeded,
                "Chronos fake creates a lifetime-owned turn scheduler");
            turns!.Tick(1f);
            Assert(turns.State == TurnState.AwaitingAction && turns.BeginAction().Succeeded &&
                   !chronos.IsFrozen && chronos.Mode == TimeMode.TurnBased &&
                   turns.EndAction().Succeeded && chronos.IsFrozen,
                "turn actions lift and then reacquire the fake world freeze");
            turns.Dispose();
            Assert(!chronos.IsFrozen && chronos.Mode == TimeMode.Slowed,
                "disposing the turn scheduler restores the preceding time state");

            var bundle = context.Assets.LoadBundleAsync("content/test.bundle").Result.Value!;
            var prefab = context.Assets.LoadPrefabAsync(bundle, "TestPrefab").Result.Value!;
            var ugc = new FakeUgcLiveSyncService(context.Lifetime);
            var overrideResult = ugc.RegisterAssetOverride(new UgcAssetOverride("@test/prefab", prefab));
            var sessionResult = ugc.StartLocalSession(new UgcLiveSyncRequest(watchFolder: "exports"));
            var snapshots = 0;
            ugc.SnapshotImported += _ => snapshots++;
            Assert(overrideResult.Succeeded && sessionResult.Succeeded &&
                   ugc.ImportSnapshot("Project", "scene", "Scene", 3, "r1").Succeeded &&
                   snapshots == 1,
                "UGC fake owns sessions and injects snapshot notifications");

            var robotKit = new FakeRobotKit(context.Lifetime);
            var spawn = robotKit.Agents.Spawn(new RobotAgentSpawnRequest(
                new Vec3(1f, 0f, 2f),
                name: "Probe"));
            Assert(spawn.TryGetValue(out var robot) && robot.MoveTo(new Vec3(3f, 0f, 4f)).Succeeded &&
                   robot.Position == new Vec3(3f, 0f, 4f),
                "RobotKit fake creates inspectable SDK-native agents");
            var target = robotKit.Objectives.RegisterTarget(
                "PLAYER",
                RobotTargetKind.Player,
                () => new RobotTargetSnapshot(Vec3.Zero));
            var objective = robotKit.Objectives.SetObjective(robot!, RobotObjective.Follow("PLAYER"));
            Assert(target.Succeeded && objective.Succeeded &&
                   robotKit.Objectives.TryGetObjective(robot!, out _),
                "RobotKit objective fake owns typed targets and objectives");

            robotKit.BrainQueries.AutoCompleteQueries = false;
            var query = robotKit.BrainQueries.QueryAsync(new BrainQueryRequest(
                "Choose",
                new[] { new BrainOutputField("choice", "Choice") }));
            Assert(!query.IsCompleted &&
                   robotKit.BrainQueries.CompleteNext(new Dictionary<string, string> { ["choice"] = "yes" }) &&
                   query.Result.Value!.TryGet("choice", out var choice) && choice == "yes",
                "RobotKit brain fake gives tests explicit operation completion control");

            robotKit.Conversations.EnqueueTurn("Ready.", "COMPLY");
            var conversation = robotKit.Conversations.BeginConversation(new RobotConversationRequest(
                "Test frame",
                new[] { "COMPLY", "REFUSE" }));
            Assert(conversation.TryGetValue(out var conversationHandle) &&
                   conversationHandle.SubmitAsync("Proceed").Result.Value!.Decision == "COMPLY",
                "RobotKit conversation fake returns queued structured turns");
            robotKit.DialogueInput.NextTranscript = "hello robot";
            var capture = robotKit.DialogueInput.BeginVoiceCapture();
            Assert(capture.TryGetValue(out var voice) &&
                   voice.StopAsync().Result.Value!.Text == "hello robot",
                "RobotKit dialogue-input fake returns deterministic voice transcripts");

            var lifetimeCancelledLoad = worlds.LoadAsync(
                new WorldLoadRequest("test.world", "test.mode"));
            Assert(!lifetimeCancelledLoad.IsCompleted && worlds.HasPendingLoad,
                "controlled world loads remain pending until completed or cancelled");
            context.Dispose();
            var rejectedPrompt = prompts.Register(new PromptOverrideRequest("robot.after-stop", "LATE"));
            Assert(prompts.ActiveRegistrationCount == 0 &&
                   rejectedPrompt.ErrorCode == ModErrorCode.Cancelled &&
                   worlds.ActiveRegistrationCount == 0 &&
                   lifetimeCancelledLoad.Result.ErrorCode == ModErrorCode.Cancelled &&
                   !worlds.HasPendingLoad &&
                   chronos.ActiveLeaseCount == 0 &&
                   ugc.ActiveLeaseCount == 0 &&
                   robotKit.Agents.ActiveAgents.Count == 0 &&
                   robotKit.Objectives.ActiveHandleCount == 0 &&
                   robotKit.Conversations.ActiveConversationCount == 0 &&
                   robotKit.DialogueInput.ActiveCaptureCount == 0,
                "specialist fake resources are released after lifetime teardown");
            context.AssertNoLeaks();
        }

        private static void TestLifecycleAndLeaks()
        {
            var order = new List<string>();
            var context = new FakeModContext();
            var runner = new ModLifecycleRunner(new ProbeMod(order), context);
            runner.Load();
            Assert(runner.IsLoaded && context.Input.ActiveActionCount == 1 &&
                   context.Events.ActiveSubscriptionCount == 1,
                "runner attaches a complete context before OnLoad");
            runner.Unload();
            Assert(string.Join(",", order) == "load,unload,second,first",
                "runner calls OnUnload before reverse-order lifetime cleanup");
            context.AssertNoLeaks();
            Assert(runner.IsFinished, "runner records completed lifecycle state");
        }

        private static void TestCompleteCoreServiceContext()
        {
            var context = new FakeModContext();
            context.Scenes.CompleteLoadsImmediately = false;
            var sceneLoad = context.Scenes.LoadAsync(new SceneLoadRequest("PuzzleRoom"));
            Assert(!sceneLoad.IsCompleted && context.Scenes.PendingLoadCount == 1,
                "scene completion can be held for in-flight assertions");
            Assert(context.Scenes.CompleteNextLoad() && sceneLoad.Result.Succeeded &&
                   context.Scenes.ActiveScene == "PuzzleRoom",
                "manual scene completion updates typed state and completes the task");

            var checkpointCalls = 0;
            context.Scenes.SubscribeCheckpointChanged(_ => throw new InvalidOperationException("expected"));
            var checkpointLease = context.Scenes.SubscribeCheckpointChanged(_ => checkpointCalls++);
            context.Scenes.SetCheckpoint(new CheckpointSnapshot(
                "puzzle-room.entry",
                "PuzzleRoom",
                new Vec3(1f, 0f, 2f)));
            Assert(context.Scenes.TryGetCheckpoint(out var checkpoint) &&
                   checkpoint!.Id == "puzzle-room.entry" &&
                   checkpointCalls == 1 &&
                   context.Logger.Count(CapturedLogLevel.Error) == 1,
                "checkpoint observation isolates failing subscribers and preserves typed current state");
            checkpointLease.Dispose();
            Assert(context.Scenes.ActiveCheckpointSubscriptionCount == 1,
                "checkpoint registrations remain disposable before lifetime cleanup");

            var entity = context.Entities.Create("Terminal", Vec3.Zero);
            var interaction = context.Interactions.Register(
                entity,
                new InteractableDefinition("USE"),
                _ => { });
            Assert(interaction.Succeeded, "interactions are lifetime-owned in the fake context");

            var bundle = context.Assets.LoadBundleAsync("assets/content.bundle").Result.Value!;
            var prefab = context.Assets.LoadPrefabAsync(bundle, "PuzzleCube").Result.Value!;
            Assert(context.Assets.Spawn(new AssetSpawnRequest(prefab, TransformState.Identity)).Succeeded,
                "asset handles and spawned entities use only SDK-native values");
            Assert(context.Audio.Play(new AudioPlayRequest("ui.confirm")).Succeeded,
                "audio playback is captured");

            var surface = context.Ui.CreateSurface(new UiSurfaceRequest("status", "Status", "Ready"));
            var accessibility = context.Ui.ApplyAccessibility(
                new UiAccessibilityPreferences(true, 1.25f, true, 0f));
            var modalResult = false;
            var modal = context.Ui.ShowModal(
                new UiModalRequest("Confirm", "Continue?"),
                confirmed => modalResult = confirmed);
            ((FakeUiModal)modal.Value!).Confirm();
            Assert(surface.Succeeded && accessibility.Succeeded
                && context.Ui.Accessibility.HighContrast && context.Ui.Accessibility.ReducedMotion
                && modalResult && context.Ui.ShowToast("Done", UiTone.Success).Succeeded,
                "UI accessibility, surfaces, explicit modal completion, and toasts are captured");

            var localization = context.Localization.Register(new LocalizationCatalog(
                "en",
                new Dictionary<string, string> { ["ready"] = "Ready" }));
            Assert(localization.Succeeded && context.Localization.Get("ready", "fallback") == "Ready",
                "localization falls back from region to language");
            context.Commands.Register(
                new CommandDefinition("ping", "Checks the mod"),
                _ => OperationResult<string>.Success("pong"));
            Assert(context.Commands.TryExecute("ping", Array.Empty<string>(), out var command) &&
                   command!.Value == "pong",
                "commands execute deterministically");
            context.Diagnostics.Report(new DiagnosticEntry("TEST001", "captured"));
            Assert(context.Diagnostics.GetSnapshot().Count == 1,
                "structured diagnostics are captured");
            Assert(context.Extensions.Register<IProbeExtension>(new ProbeExtension()).Succeeded &&
                   context.Extensions.TryGet<IProbeExtension>(out _),
                "typed extension providers can be registered and resolved");

            context.Dispose();
            context.AssertNoLeaks();
        }

        private static void TestDeclarativeUiComposition()
        {
            var context = new FakeModContext();
            var successfulButtonSubscriberCalls = 0;
            var toggleValue = false;
            var sliderValue = 0f;
            var textValue = string.Empty;
            var dropdownValue = string.Empty;
            var selectedItemId = string.Empty;
            Action isolatedButtonCallbacks = () => throw new InvalidOperationException("expected callback failure");
            isolatedButtonCallbacks += () => successfulButtonSubscriberCalls++;

            var root = new UiColumn(
                new UiText("Controls", UiTextStyle.Heading),
                new UiRow(
                    new UiButton("apply", "Apply", isolatedButtonCallbacks),
                    new UiButton("disabled", "Unavailable", () => { }, enabled: false)),
                new UiScroll(new UiColumn(
                    new UiToggle("assist", "Assist mode", false, value => toggleValue = value),
                    new UiSlider("scale", "UI scale", 0.75f, 1.5f, 1f, value => sliderValue = value),
                    new UiTextInput(
                        "name",
                        "Robot name",
                        "Topo",
                        value => textValue = value,
                        placeholder: "Name",
                        maximumLength: 5),
                    new UiDropdown(
                        "tone",
                        "Message tone",
                        new[] { new UiChoice("neutral", "Neutral"), new UiChoice("success", "Success") },
                        "neutral",
                        value => dropdownValue = value),
                    new UiVirtualList(
                        "robots",
                        new[]
                        {
                            new UiListItem("atlas", "Atlas", "Builder", "READY"),
                            new UiListItem("ember", "Ember", "Explorer")
                        },
                        value => selectedItemId = value,
                        selectedItemId: "atlas",
                        visibleRows: 2))));

            var accessibility = context.Ui.ApplyAccessibility(
                new UiAccessibilityPreferences(highContrast: true, uiScale: 1.25f, reducedMotion: true, motionIntensity: 0f));
            var creation = context.Ui.CreateSurface(new UiSurfaceRequest(
                "declarative",
                "Declarative UI",
                "Safe controls",
                content: root));
            Assert(creation.TryGetValue(out var created) && created is FakeUiSurface,
                "declarative UI surfaces are captured without a native renderer");
            var surface = (FakeUiSurface)created!;
            Assert(context.Ui.CreateSurface(new UiSurfaceRequest(
                       "declarative",
                       "Duplicate",
                       string.Empty)).ErrorCode == ModErrorCode.Conflict,
                "duplicate surface ids fail with a stable owner-scoped conflict");
            Assert(ReferenceEquals(surface.Content, root) && surface.TryFindNode("robots", out _),
                "the fake UI retains and indexes the immutable composition tree");
            Assert(accessibility.Succeeded && context.Ui.Accessibility.HighContrast &&
                   context.Ui.Accessibility.UiScale == 1.25f && context.Ui.Accessibility.ReducedMotion &&
                   context.Ui.Accessibility.MotionIntensity == 0f,
                "declarative controls retain host accessibility preferences");

            var buttonResult = surface.ActivateButton("apply");
            Assert(!buttonResult.Succeeded && buttonResult.ErrorCode == ModErrorCode.External &&
                   successfulButtonSubscriberCalls == 1 && surface.CallbackErrors.Count == 1,
                "a failing UI callback subscriber is isolated without skipping later subscribers");
            Assert(surface.ChangeToggle("assist", true).Succeeded && toggleValue &&
                   surface.TryGetToggleValue("assist", out var capturedToggle) && capturedToggle,
                "toggle callbacks and state are deterministic");
            Assert(surface.ChangeSlider("scale", 1.4f).Succeeded && sliderValue == 1.4f &&
                   surface.TryGetSliderValue("scale", out var capturedSlider) && capturedSlider == 1.4f,
                "slider callbacks enforce and capture bounded values");
            Assert(surface.ChangeText("name", "Robotopia").Succeeded && textValue == "Robot" &&
                   surface.TryGetTextValue("name", out var capturedText) && capturedText == "Robot",
                "text input applies its maximum length before callback delivery");
            Assert(surface.ChangeDropdown("tone", "success").Succeeded && dropdownValue == "success" &&
                   surface.TryGetDropdownValue("tone", out var capturedChoice) && capturedChoice == "success",
                "dropdown callbacks use stable SDK values");
            Assert(surface.SelectListItem("robots", "ember").Succeeded && selectedItemId == "ember" &&
                   surface.TryGetSelectedListItem("robots", out var capturedItem) && capturedItem == "ember",
                "virtualized-list callbacks use stable item ids");
            Assert(surface.ActivateButton("disabled").ErrorCode == ModErrorCode.InvalidState &&
                   surface.ChangeSlider("scale", 2f).ErrorCode == ModErrorCode.InvalidArgument &&
                   surface.ChangeDropdown("tone", "missing").ErrorCode == ModErrorCode.InvalidArgument,
                "fake controls return stable errors for disabled or invalid interactions");

            surface.SetBody("Updated");
            Assert(surface.Body == "Updated" &&
                   surface.SetContent(new UiColumn(new UiText("Replacement"), new UiButton("close", "Close", () => { }))).Succeeded &&
                   !surface.TryFindNode("apply", out _) && surface.TryFindNode("close", out _),
                "body updates remain compatible and composition replacement drops stale controls");
            var duplicateTree = new UiRow(
                new UiButton("same", "First", () => { }),
                new UiToggle("same", "Second", false, _ => { }));
            Assert(surface.SetContent(duplicateTree).ErrorCode == ModErrorCode.InvalidArgument &&
                   surface.TryFindNode("close", out _),
                "failed composition replacement is atomic and returns a stable validation error");
            AssertThrows<ArgumentException>(() => new UiSurfaceRequest(
                    "duplicates",
                    "Duplicates",
                    string.Empty,
                    content: duplicateTree),
                "composition validation rejects duplicate interactive ids before rendering");
            AssertThrows<ArgumentException>(() => new UiSurfaceRequest(
                    "interactive-hud",
                    "Interactive HUD",
                    string.Empty,
                    UiSurfaceKind.Hud,
                    content: new UiButton("hud-action", "Action", () => { })),
                "presentation-only HUD surfaces reject controls that would silently lack input");

            var successfulModalSubscriberCalls = 0;
            Action<bool> isolatedModalCallbacks = _ => throw new InvalidOperationException("expected modal failure");
            isolatedModalCallbacks += confirmed =>
            {
                if (confirmed) successfulModalSubscriberCalls++;
            };
            var modalResult = context.Ui.ShowModal(
                new UiModalRequest("Confirm", "Exercise isolated completion callbacks."),
                isolatedModalCallbacks);
            Assert(modalResult.TryGetValue(out var createdModal) && createdModal is FakeUiModal,
                "declarative UI tests can capture modal completion");
            var modal = (FakeUiModal)createdModal!;
            modal.Confirm();
            Assert(successfulModalSubscriberCalls == 1 && modal.CallbackErrors.Count == 1 &&
                   context.Ui.Modals.Count == 0,
                "modal completion isolates a failing subscriber and releases exactly once");

            surface.Dispose();
            Assert(context.Ui.CreateSurface(new UiSurfaceRequest(
                       "declarative",
                       "Recreated",
                       string.Empty,
                       content: new UiButton("replacement", "Replacement", () => { }))).Succeeded,
                "early surface release makes its owner-scoped id available for reuse");
            context.Dispose();
            Assert(context.Ui.Surfaces.Count == 0 &&
                   surface.ActivateButton("close").ErrorCode == ModErrorCode.NotFound &&
                   successfulButtonSubscriberCalls == 1,
                "lifetime teardown releases the surface and gates callbacks after disposal");
            Assert(context.Ui.CreateSurface(new UiSurfaceRequest("stopped", "Stopped", string.Empty)).ErrorCode ==
                       ModErrorCode.Cancelled &&
                   context.Ui.ShowModal(new UiModalRequest("Stopped", string.Empty), _ => { }).ErrorCode ==
                       ModErrorCode.Cancelled &&
                   context.Ui.ShowToast("Stopped").ErrorCode == ModErrorCode.Cancelled &&
                   context.Ui.ApplyAccessibility(UiAccessibilityPreferences.Default).ErrorCode ==
                       ModErrorCode.Cancelled,
                "fake UI creation and mutation fail with cancellation after lifetime teardown");
            context.AssertNoLeaks();
        }

        private static void TestPartialLoadFailureCleanup()
        {
            var order = new List<string>();
            var context = new FakeModContext();
            var runner = new ModLifecycleRunner(new FailingMod(order), context);
            AssertThrows<InvalidOperationException>(() => runner.Load(),
                "load errors are rethrown after cleanup");
            Assert(string.Join(",", order) == "load,unload,cleanup",
                "partial load still invokes unload and lifetime cleanup");
            context.AssertNoLeaks();
            Assert(runner.IsFinished, "failed-load cleanup completes the runner");
        }

        private static void TestEveryOwnedResourceAcrossTenReloads()
        {
            for (var cycle = 0; cycle < 10; cycle++)
            {
                var context = new FakeModContext();
                context.Scenes.CompleteLoadsImmediately = false;
                var entity = context.Entities.Create("Owned resource probe", Vec3.Zero);
                var runner = new ModLifecycleRunner(new ResourceHeavyMod(entity, failAfterRegistration: false), context);
                runner.Load();
                Assert(context.Lifetime.TrackedResourceCount >= 16,
                    "resource-heavy lifecycle should exercise every owner-bound resource family");
                runner.Unload();
                context.AssertNoLeaks();
            }
        }

        private static void TestEveryOwnedResourceAfterPartialLoadFailure()
        {
            var context = new FakeModContext();
            context.Scenes.CompleteLoadsImmediately = false;
            var entity = context.Entities.Create("Partial resource probe", Vec3.Zero);
            var runner = new ModLifecycleRunner(new ResourceHeavyMod(entity, failAfterRegistration: true), context);
            AssertThrows<InvalidOperationException>(() => runner.Load(),
                "resource-heavy partial load should surface its original failure");
            context.AssertNoLeaks();
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Testing kit test failed: " + message);
            }
        }

        private static void AssertThrows<TException>(Action action, string message)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException("Testing kit test failed: " + message);
        }

        private sealed class ProbeConfig
        {
            public int Value { get; set; }
        }

        private interface IProbeExtension
        {
        }

        private sealed class ProbeExtension : IProbeExtension
        {
        }

        private sealed class ProbeMod : TopiaForgeMod
        {
            private readonly List<string> order;

            public ProbeMod(List<string> order)
            {
                this.order = order;
            }

            protected override void OnLoad()
            {
                order.Add("load");
                Context.Lifetime.Defer(() => order.Add("first"));
                Context.Lifetime.Defer(() => order.Add("second"));
                Context.Events.SubscribeUpdate(_ => { });
                Assert(Context.Input.RegisterAction(new InputActionDefinition(
                    "test",
                    "Test",
                    new[] { InputBinding.Key("T") })).Succeeded,
                    "probe mod should register its input action");
            }

            protected override void OnUnload()
            {
                order.Add("unload");
            }
        }

        private sealed class FailingMod : TopiaForgeMod
        {
            private readonly List<string> order;

            public FailingMod(List<string> order)
            {
                this.order = order;
            }

            protected override void OnLoad()
            {
                order.Add("load");
                Context.Lifetime.Defer(() => order.Add("cleanup"));
                throw new InvalidOperationException("expected load failure");
            }

            protected override void OnUnload()
            {
                order.Add("unload");
            }
        }

        private sealed class ResourceHeavyMod : TopiaForgeMod
        {
            private readonly IEntity entity;
            private readonly bool failAfterRegistration;

            public ResourceHeavyMod(IEntity entity, bool failAfterRegistration)
            {
                this.entity = entity;
                this.failAfterRegistration = failAfterRegistration;
            }

            protected override void OnLoad()
            {
                Context.Lifetime.Defer(() => { });
                Context.Events.SubscribeUpdate(_ => { });
                Context.Events.SubscribeFixedUpdate(_ => { });
                Context.Events.SubscribeLateUpdate(_ => { });
                Context.Events.SubscribeSceneLoaded(_ => { });
                Context.Scenes.SubscribeCheckpointChanged(_ => { });
                _ = Context.Scenes.LoadAsync(new SceneLoadRequest("PendingRoom"));
                Assert(Context.Input.RegisterAction(new InputActionDefinition(
                    "resource-probe",
                    "Resource probe",
                    new[] { InputBinding.Key("R"), InputBinding.GamepadButton(InputGamepadButton.North) })).Succeeded,
                    "resource-heavy mod should register its input action");
                Assert(Context.Player.AcquireControl("resource test").Succeeded,
                    "resource-heavy mod should acquire player controls");
                Assert(Context.Entities.AcquireMotion(entity).Succeeded,
                    "resource-heavy mod should acquire entity motion");
                Assert(Context.Interactions.Register(
                    entity,
                    new InteractableDefinition("TEST"),
                    _ => { }).Succeeded,
                    "resource-heavy mod should register an interaction");

                var bundle = Context.Assets.LoadBundleAsync("assets/probe.bundle").Result.Value!;
                var prefab = Context.Assets.LoadPrefabAsync(bundle, "Probe").Result.Value!;
                Assert(Context.Assets.Spawn(new AssetSpawnRequest(prefab, TransformState.Identity)).Succeeded,
                    "resource-heavy mod should own a spawned entity");
                Assert(Context.Audio.Play(new AudioPlayRequest("test.probe", loop: true)).Succeeded,
                    "resource-heavy mod should own audio playback");
                Assert(Context.Ui.CreateSurface(new UiSurfaceRequest("probe", "Probe", "Ready")).Succeeded,
                    "resource-heavy mod should own a UI surface");
                Assert(Context.Ui.ShowModal(new UiModalRequest("Probe", "Continue?"), _ => { }).Succeeded,
                    "resource-heavy mod should own a modal");
                Assert(Context.Localization.Register(new LocalizationCatalog(
                    "en",
                    new Dictionary<string, string> { ["probe"] = "Probe" })).Succeeded,
                    "resource-heavy mod should own a localization catalog");
                Assert(Context.Commands.Register(
                    new CommandDefinition("probe", "Resource probe"),
                    _ => OperationResult<string>.Success(string.Empty)).Succeeded,
                    "resource-heavy mod should own a command");
                Assert(Context.Extensions.Register<IProbeExtension>(new ProbeExtension()).Succeeded,
                    "resource-heavy mod should own an extension provider");
                Assert(Context.Scheduler.Every(TimeSpan.FromSeconds(1), () => { }).Succeeded,
                    "resource-heavy mod should own repeating scheduled work");
                _ = Context.Scheduler.DelayAsync(TimeSpan.FromHours(1));
                _ = Context.Items.GiveAsync(new ItemGrantRequest("test.item"));

                if (failAfterRegistration)
                {
                    throw new InvalidOperationException("expected resource-heavy load failure");
                }
            }
        }
    }
}
