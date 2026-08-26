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
                   robotKit.Agents.ActiveAgents.Count == 0 &&
                   robotKit.Objectives.ActiveHandleCount == 0 &&
                   robotKit.Conversations.ActiveConversationCount == 0 &&
                   robotKit.DialogueInput.ActiveCaptureCount == 0,
                "specialist fake resources are released after lifetime teardown");
            context.AssertNoLeaks();
        }

        private static void TestWorldPauseMenuFake()
        {
            var context = new FakeModContext();
            var pauseMenu = new FakeWorldPauseMenuService(context.Lifetime);
            var invoked = 0;

            var registered = pauseMenu.RegisterAction(new WorldPauseAction(
                "probe-restart",
                "RESTART",
                () => invoked++,
                destructive: true));
            Assert(registered.TryGetValue(out var registration) && pauseMenu.ActiveActionCount == 1,
                "a pause action registers and is counted");
            Assert(pauseMenu.Invoke("probe-restart") && invoked == 1,
                "an invoked pause action runs its callback");
            Assert(!pauseMenu.Invoke("missing"), "an unknown pause action id does not run anything");

            var duplicate = pauseMenu.RegisterAction(new WorldPauseAction("probe-restart", "AGAIN", () => { }));
            Assert(!duplicate.Succeeded && duplicate.ErrorCode == ModErrorCode.Conflict,
                "a duplicate pause action id is a conflict");

            var intercepted = pauseMenu.InterceptExit(_ => WorldPauseExitDecision.Block);
            Assert(intercepted.TryGetValue(out var interceptorHandle) && pauseMenu.HasExitInterceptor,
                "an exit interceptor registers");
            Assert(pauseMenu.InvokeExit(WorldSessionFixture()) == WorldPauseExitDecision.Block,
                "the registered interceptor decides the vanilla exit");
            interceptorHandle!.Dispose();
            Assert(!pauseMenu.HasExitInterceptor &&
                   pauseMenu.InvokeExit(WorldSessionFixture()) == WorldPauseExitDecision.EndSessionAndExit,
                "releasing the interceptor restores the default exit decision");

            var throwingHandle = pauseMenu.InterceptExit(_ => throw new InvalidOperationException("bad"));
            Assert(throwingHandle.Succeeded &&
                   pauseMenu.InvokeExit(WorldSessionFixture()) == WorldPauseExitDecision.EndSessionAndExit,
                "a throwing interceptor can never eat the vanilla exit button");
            throwingHandle.Value!.Dispose();

            pauseMenu.SupportsExitInterception = false;
            Assert(pauseMenu.InterceptExit(_ => WorldPauseExitDecision.Block).ErrorCode == ModErrorCode.Unavailable,
                "a host without exit interception reports Unavailable");

            pauseMenu.IsAvailable = false;
            Assert(pauseMenu.RegisterAction(new WorldPauseAction("late", "LATE", () => { })).ErrorCode
                   == ModErrorCode.Unavailable,
                "registration fails while the pause UI is unresolved");
            pauseMenu.IsAvailable = true;

            registration!.Dispose();
            Assert(pauseMenu.ActiveActionCount == 0 && !pauseMenu.Invoke("probe-restart"),
                "a released pause action stops running");

            // The lifetime owns every handle, so a gamemode that forgets to release its action still cannot leak.
            var leaked = pauseMenu.RegisterAction(new WorldPauseAction("leaky", "LEAKY", () => { }));
            Assert(leaked.Succeeded, "a second pause action registers after the first is released");
            context.Dispose();
            Assert(pauseMenu.ActiveActionCount == 0 && !pauseMenu.IsAvailable,
                "lifetime teardown releases pause actions and closes the service");
            context.AssertNoLeaks();
        }

        private static void TestGameplayPause()
        {
            var context = new FakeModContext();
            var chronos = new FakeTimeControlService(context.Lifetime);

            // Preferred source available: the world freeze wins and no player-control lease is taken.
            var pause = new GameplayPause(context, "probe-shop", chronos.AsPauseSource(), "PROBE_PAUSE_FAILED");
            Assert(!pause.IsActive && pause.Kind == GameplayPauseKind.None,
                "a new pause holds nothing until it is requested");
            pause.Request();
            Assert(pause.IsActive && pause.Kind == GameplayPauseKind.Preferred &&
                   context.LocalPlayer.ActiveControlLeaseCount == 0,
                "an available preferred source is used instead of the player-control fallback");
            pause.Release();
            Assert(!pause.IsActive && pause.Kind == GameplayPauseKind.None,
                "releasing the pause drops the preferred hold");

            // Preferred source unavailable: degrade to player control rather than failing outright.
            chronos.IsAvailable = false;
            pause.Request();
            Assert(pause.IsActive && pause.Kind == GameplayPauseKind.PlayerControl &&
                   context.LocalPlayer.ActiveControlLeaseCount == 1,
                "an unavailable preferred source degrades to suspending player control");
            pause.Release();
            Assert(context.LocalPlayer.ActiveControlLeaseCount == 0,
                "releasing the pause drops the player-control lease");

            // Nothing available: report once, then retry on the unscaled clock.
            context.LocalPlayer.AcquireControlErrorCode = ModErrorCode.Unavailable;
            pause.Request();
            Assert(!pause.IsActive && pause.IsRetrying,
                "a pause that cannot be acquired keeps wanting to be acquired");
            Assert(context.Diagnostics.GetSnapshot().Count == 1 &&
                   context.Diagnostics.GetSnapshot()[0].Entry.Code == "PROBE_PAUSE_FAILED",
                "total acquisition failure is reported once with the supplied code");
            pause.Tick(0.1f);
            pause.Tick(0.1f);
            Assert(context.Diagnostics.GetSnapshot().Count == 1,
                "retrying does not re-report the same failure every frame");

            // Recovery: the next retry after the backoff elapses reacquires without any caller involvement.
            context.LocalPlayer.AcquireControlErrorCode = ModErrorCode.None;
            pause.Tick(GameplayPause.DefaultRetrySeconds);
            Assert(pause.IsActive && pause.Kind == GameplayPauseKind.PlayerControl,
                "the pause reacquires itself once the host recovers");
            Assert(context.LocalPlayer.ActiveControlLeaseCount == 1,
                "recovery does not stack duplicate holds");

            pause.Dispose();
            Assert(!pause.IsActive && context.LocalPlayer.ActiveControlLeaseCount == 0,
                "disposing the pause releases every hold");
            pause.Dispose();
            pause.Tick(1f);
            Assert(!pause.IsActive, "disposal is idempotent and a disposed pause never reacquires");

            context.Dispose();
            context.AssertNoLeaks();
        }

        private static WorldSession WorldSessionFixture() => new WorldSession(
            "probe.world",
            "probe.gamemode",
            "Single",
            "ProbeScene",
            DateTimeOffset.UnixEpoch);
    }
}
