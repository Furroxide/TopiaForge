using System;
using System.Collections.Generic;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class TestingKitTests
    {
        private static void TestImmediateLifetimeReleaseForZombieResources()
        {
            using var context = new FakeModContext();
            var baseline = context.Lifetime.TrackedResourceCount;

            var control = context.LocalPlayer.AcquireControl("zombie resource regression");
            Assert(control.TryGetValue(out var controlLease) && controlLease is FakePlayerControlLease,
                "player control preserves the inspectable fake lease identity");
            controlLease!.Dispose();
            Assert(context.Lifetime.TrackedResourceCount == baseline,
                "disposed player control releases its lifetime entry immediately");

            var playback = context.Audio.Play(new AudioPlayRequest("zombies.test"));
            Assert(playback.TryGetValue(out var audio), "zombie audio playback starts in the fake service");
            audio!.Dispose();
            Assert(context.Lifetime.TrackedResourceCount == baseline,
                "stopped zombie audio releases its lifetime entry immediately");

            var robotKit = new FakeRobotKit(context.Lifetime);
            var spawned = robotKit.Agents.Spawn(new RobotAgentSpawnRequest(Vec3.Zero, name: "Zombie"));
            Assert(spawned.TryGetValue(out var robot), "a zombie robot can be spawned for the retention regression");
            robot!.Dispose();
            Assert(context.Lifetime.TrackedResourceCount == baseline,
                "despawned zombie robots release their lifetime entries immediately");

            var chronos = new FakeTimeControlService(context.Lifetime);
            var frozen = chronos.Freeze("zombie pause");
            Assert(frozen.TryGetValue(out var freeze), "a zombie time freeze can be acquired");
            freeze!.Dispose();
            Assert(context.Lifetime.TrackedResourceCount == baseline,
                "released zombie time effects unregister immediately");

            var turnBased = chronos.BeginTurnBased("zombie turns", new TurnSchedulerOptions());
            Assert(turnBased.TryGetValue(out var scheduler), "a fake turn scheduler can be acquired");
            scheduler!.Dispose();
            Assert(context.Lifetime.TrackedResourceCount == baseline,
                "a disposed turn scheduler releases both scheduler and freeze lifetime entries");

            var conversation = robotKit.Conversations.BeginConversation(new RobotConversationRequest(
                "Zombie negotiation",
                new[] { "COMPLY", "REFUSE" }));
            Assert(conversation.TryGetValue(out var conversationHandle), "a zombie conversation can begin");
            conversationHandle!.Dispose();
            Assert(context.Lifetime.TrackedResourceCount == baseline,
                "ended zombie conversations release their lifetime entries immediately");

            var voiceCapture = robotKit.DialogueInput.BeginVoiceCapture();
            Assert(voiceCapture.TryGetValue(out var voice), "zombie dialogue voice capture can begin");
            voice!.Dispose();
            Assert(context.Lifetime.TrackedResourceCount == baseline,
                "stopped zombie voice captures release their lifetime entries immediately");

            var pause = new FakeWorldPauseMenuService(context.Lifetime);
            var action = pause.RegisterAction(new WorldPauseAction("zombies.test.restart", "Restart", () => { }));
            Assert(action.Succeeded && pause.ActiveActionCount == 1, "session pause actions retain explicit ownership");
            action.Value!.Dispose();
            Assert(pause.ActiveActionCount == 0 && context.Lifetime.TrackedResourceCount == baseline,
                "released session pause actions unregister immediately");

            var command = context.Commands.Register(
                new CommandDefinition("zombies-test", "Lifetime regression command."),
                _ => OperationResult<string>.Success("ok"));
            Assert(command.TryGetValue(out var commandRegistration)
                && context.Commands.ActiveCommandCount == 1,
                "a Zombies command registration is inspectable before early release");
            commandRegistration!.Dispose();
            Assert(context.Commands.ActiveCommandCount == 0
                && context.Lifetime.TrackedResourceCount == baseline,
                "released command registrations unregister immediately");

            var extension = context.Extensions.Register<IProbeExtension>(new ProbeExtension());
            Assert(extension.TryGetValue(out var extensionRegistration)
                && context.Extensions.ActiveProviderCount == 1,
                "a framework extension registration is inspectable before early release");
            extensionRegistration!.Dispose();
            Assert(context.Extensions.ActiveProviderCount == 0
                && context.Lifetime.TrackedResourceCount == baseline,
                "released extension registrations unregister immediately");
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

            var lifetimeSceneContext = new FakeModContext();
            lifetimeSceneContext.Scenes.CompleteLoadsImmediately = false;
            var lifetimeSceneLoad = lifetimeSceneContext.Scenes.LoadAsync(
                new SceneLoadRequest("CommittedAfterOwnerStop"));
            lifetimeSceneContext.Dispose();
            Assert(lifetimeSceneLoad.IsCompletedSuccessfully
                && lifetimeSceneLoad.Result.ErrorCode == ModErrorCode.Cancelled
                && lifetimeSceneContext.Scenes.PendingLoadCount == 0
                && lifetimeSceneContext.Scenes.CompleteNextLoad()
                && lifetimeSceneContext.Scenes.ActiveScene == "CommittedAfterOwnerStop",
                "owner shutdown suppresses a dispatched scene result without discarding the native replacement");
            lifetimeSceneContext.AssertNoLeaks();

            var worlds = new FakeLocalWorldService(context.Lifetime) { SessionId = "cancel-session" };
            var world = worlds.ImportAsync("cancel-session", "cancel.roboworld", token);
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
                   worlds.ImportedLocalWorlds.Count == 0 && robotKit.BrainQueries.PendingQueryCount == 0,
                "specialist async fakes use the same stable cancellation convention");

            context.Dispose();
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

    }
}
