using System;
using System.Collections.Generic;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class TestingKitTests
    {
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
            var physicsHitProxy = new FakeEntity(robot!.Id, "Probe child collider", new Vec3(3f, 1f, 4f));
            Assert(robotKit.Agents.TryGetRobot(physicsHitProxy, out var mappedRobot) &&
                   ReferenceEquals(mappedRobot, robot),
                "RobotKit fake resolves proxy entities with an agent's canonical runtime id");
            var playerEntity = new FakeEntity("test-player", "Player", new Vec3(8f, 0f, 9f));
            robotKit.Agents.PlayerEntity = playerEntity;
            Assert(robotKit.Agents.TryGetPlayerEntity(out var livePlayer) &&
                   ReferenceEquals(livePlayer, playerEntity) &&
                   robot.Chase(livePlayer!).Succeeded &&
                   robot.Position == playerEntity.Position,
                "RobotKit fake exposes a live safe player entity that agents can chase");
            playerEntity.Destroy();
            Assert(!robotKit.Agents.TryGetPlayerEntity(out livePlayer) && livePlayer == null,
                "RobotKit fake does not return a stale player entity after it dies");
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

    }
}
