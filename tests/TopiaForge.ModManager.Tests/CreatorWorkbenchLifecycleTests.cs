using System;
using System.Linq;
using TopiaForge.CreatorTools;
using TopiaForge.CreatorTools.Shared;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static class CreatorWorkbenchLifecycleTests
    {
        public static void Run()
        {
            TestHideReopenAndTenLeakFreeCycles();
            TestCreatorSessionFailureRestoresOwnedState();
            TestGlobalIsolationRevocationRestoresOwnedState();
            TestGraphStopLeavesManualSpawn();
            TestContentSourceUnloadReconcilesRoster();
            Console.WriteLine("CreatorWorkbenchLifecycleTests passed.");
        }

        private static void TestHideReopenAndTenLeakFreeCycles()
        {
            using var context = CreateGameplayContext();
            using var content = new FakeCreatorContentService(context.Lifetime);
            var robots = new FakeRobotKit(context.Lifetime);
            var baselineResources = context.Lifetime.TrackedResourceCount;
            CreatorWorkbench? callbackTarget = null;
            var hideRequests = 0;
            var workbench = new CreatorWorkbench(
                context,
                SandboxOptions(),
                content,
                robots.Agents,
                () => hideRequests++,
                () => RequireWorkbench(callbackTarget).EndSession());
            callbackTarget = workbench;

            for (var cycle = 0; cycle < 10; cycle++)
            {
                Assert(workbench.Open().Succeeded, "cycle " + cycle + " should open");
                Assert(workbench.IsSessionActive && workbench.IsVisible
                    && content.ActiveSessionCount == 1
                    && context.LocalPlayer.ActiveControlLeaseCount == 1,
                    "open should own one persistent creator session and one player-control lease");
                Assert(workbench.SpawnRobot().Succeeded && robots.Agents.ActiveAgents.Count == 1,
                    "each cycle should create one manual owned robot");

                Assert(workbench.Hide().Value == true
                    && workbench.IsSessionActive && !workbench.IsVisible
                    && content.ActiveSessionCount == 1
                    && robots.Agents.ActiveAgents.Count == 1
                    && context.LocalPlayer.ActiveControlLeaseCount == 0,
                    "hide should release controls while retaining the session and its owned content");
                var hud = Surface(context, "creator-lifecycle-hud");
                Assert(hud.Body.Contains("F5 REOPEN WORKBENCH", StringComparison.Ordinal),
                    "the hidden-session HUD should tell the developer how to reopen it");

                Assert(workbench.Open().Succeeded
                    && workbench.IsSessionActive && workbench.IsVisible
                    && content.ActiveSessionCount == 1
                    && robots.Agents.ActiveAgents.Count == 1
                    && context.LocalPlayer.ActiveControlLeaseCount == 1,
                    "reopen should reacquire controls without replacing the creator session");
                Assert(workbench.EndSession().Value == true
                    && !workbench.IsSessionActive
                    && content.ActiveSessionCount == 0
                    && robots.Agents.ActiveAgents.Count == 0
                    && context.LocalPlayer.ActiveControlLeaseCount == 0,
                    "end session should remove owned content and release every session lease");
            }

            Assert(hideRequests == 20,
                "each visible-to-hidden transition should deliver one bounded dismissal request without recursion");
            workbench.Dispose();
            Assert(context.Lifetime.TrackedResourceCount == baselineResources,
                "ten cycles should return lifetime resource ownership to its context baseline");
            context.Dispose();
            context.AssertNoLeaks();
        }

        private static void TestCreatorSessionFailureRestoresOwnedState()
        {
            using var context = CreateGameplayContext();
            var content = new FakeCreatorContentService(context.Lifetime);
            var robots = new FakeRobotKit(context.Lifetime);
            CreatorWorkbench? callbackTarget = null;
            var workbench = new CreatorWorkbench(
                context,
                SandboxOptions("creator-session-failure"),
                content,
                robots.Agents,
                () => RequireWorkbench(callbackTarget).Hide(),
                () => RequireWorkbench(callbackTarget).EndSession());
            callbackTarget = workbench;

            Assert(workbench.Open().Succeeded && workbench.SpawnRobot().Succeeded,
                "session-failure fixture should have a live controlled session and owned robot");
            content.Dispose();
            context.AdvanceFrame(TimeSpan.Zero);
            Assert(!workbench.IsSessionActive
                && robots.Agents.ActiveAgents.Count == 0
                && context.LocalPlayer.ActiveControlLeaseCount == 0
                && context.Ui.Toasts.Any(toast => toast.Tone == UiTone.Danger
                    && toast.Message.Contains("became unavailable", StringComparison.Ordinal)),
                "a dead creator session should route through full restoration on the next update");

            workbench.Dispose();
            context.Dispose();
            context.AssertNoLeaks();
        }

        private static void TestGlobalIsolationRevocationRestoresOwnedState()
        {
            using var context = CreateGameplayContext();
            using var content = new FakeCreatorContentService(context.Lifetime);
            var robots = new FakeRobotKit(context.Lifetime);
            var safety = new FakeCreatorMutationSafetyService(
                context.Lifetime,
                CreatorMutationSafetyState.Ready,
                "Fake persistence isolation ready.");
            var safetyRegistration = context.Extensions.Register<ICreatorMutationSafetyService>(safety).Value!;
            CreatorWorkbench? callbackTarget = null;
            var workbench = new CreatorWorkbench(
                context,
                GlobalOptions("creator-isolation-loss"),
                content,
                robots.Agents,
                () => RequireWorkbench(callbackTarget).Hide(),
                () => RequireWorkbench(callbackTarget).EndSession());
            callbackTarget = workbench;

            Assert(workbench.Open().Succeeded, "global fixture should open for browsing");
            var window = Surface(context, "creator-isolation-loss-window");
            Assert(window.ActivateButton("enable-mutations").Succeeded && context.Ui.Modals.Count == 1,
                "global mutation should require the explicit acknowledgement modal");
            context.Ui.Modals.Single().Confirm();
            Assert(safety.ActiveLeaseCount == 1 && workbench.SpawnRobot().Succeeded,
                "acknowledgement should acquire isolation before mutation");

            safety.SetState(CreatorMutationSafetyState.Unavailable, "Isolation bridge was revoked.");
            context.AdvanceFrame(TimeSpan.Zero);
            Assert(!workbench.IsSessionActive
                && safety.ActiveLeaseCount == 0
                && robots.Agents.ActiveAgents.Count == 0
                && context.LocalPlayer.ActiveControlLeaseCount == 0,
                "revoked isolation should immediately end and restore the global creator session");

            workbench.Dispose();
            safetyRegistration.Dispose();
            context.Dispose();
            context.AssertNoLeaks();
        }

        private static void TestGraphStopLeavesManualSpawn()
        {
            using var context = CreateGameplayContext();
            using var content = new FakeCreatorContentService(context.Lifetime);
            var robots = new FakeRobotKit(context.Lifetime);
            var library = new FakeCreatorProjectLibrary();
            var projectFactory = new TestContentFactory();
            var projectSource = content.Register(new CreatorContentRegistrationRequest(
                "event-crate",
                "Event crate",
                "Graph rollback fixture.",
                CreatorContentKind.Prop,
                CreatorTransformCapabilities.All,
                projectFactory)).Value!;
            var project = new CreatorEventProject(
                1,
                "graph-rollback",
                "Graph rollback",
                string.Empty,
                CreatorProjectScope.Sandbox,
                string.Empty,
                string.Empty,
                DateTimeOffset.UtcNow,
                entities: new[]
                {
                    new CreatorProjectEntity(
                        "event-robot",
                        "Event Crate",
                        "test.creator:event-crate",
                        string.Empty,
                        TransformState.Identity,
                        spawnOnStart: true)
                },
                nodes: new[] { new CreatorGraphNode("start", CreatorGraphNodeKind.ProjectStart, Vec2.Zero) });
            Assert(library.SaveAsync(project).GetAwaiter().GetResult().Succeeded,
                "graph rollback fixture should persist in the fake library");
            var projectRegistration = context.Extensions.Register<ICreatorProjectLibrary>(library).Value!;
            CreatorWorkbench? callbackTarget = null;
            var workbench = new CreatorWorkbench(
                context,
                SandboxOptions("creator-graph-rollback"),
                content,
                robots.Agents,
                () => RequireWorkbench(callbackTarget).Hide(),
                () => RequireWorkbench(callbackTarget).EndSession());
            callbackTarget = workbench;

            Assert(workbench.Open().Succeeded && workbench.SpawnRobot().Succeeded,
                "graph rollback fixture should begin with one unrelated manual robot");
            context.AdvanceFrame(TimeSpan.Zero);
            var window = Surface(context, "creator-graph-rollback-window");
            Assert(window.SelectListItem("project-list", project.Id).Succeeded
                && window.ActivateButton("load-project").Succeeded,
                "saved project should be selectable and loadable through the real workbench UI");
            context.AdvanceFrame(TimeSpan.Zero);
            var runResult = window.ActivateButton("run-project");
            Assert(runResult.Succeeded
                && robots.Agents.ActiveAgents.Count == 1
                && content.ActiveSpawnCount == 1,
                "run should add one graph-owned object beside the manual robot; result="
                + runResult.ErrorCode + ", robots=" + robots.Agents.ActiveAgents.Count
                + ", content=" + content.ActiveSpawnCount
                + ", status=" + workbench.DescribeStatus()
                + ", callbackErrors=" + string.Join(" | ", window.CallbackErrors));
            Assert(window.ActivateButton("stop-project").Succeeded
                && robots.Agents.ActiveAgents.Count == 1
                && content.ActiveSpawnCount == 0
                && workbench.DescribeStatus().Contains("roster=1", StringComparison.Ordinal),
                "graph Stop should roll back only graph-owned content and retain manual session spawns");

            workbench.Dispose();
            projectSource.Dispose();
            projectRegistration.Dispose();
            context.Dispose();
            context.AssertNoLeaks();
        }

        private static void TestContentSourceUnloadReconcilesRoster()
        {
            using var context = CreateGameplayContext();
            using var content = new FakeCreatorContentService(context.Lifetime, "test.creator");
            var robots = new FakeRobotKit(context.Lifetime);
            var factory = new TestContentFactory();
            var source = content.Register(new CreatorContentRegistrationRequest(
                "crate",
                "Creator crate",
                "Unload lifecycle fixture.",
                CreatorContentKind.Prop,
                CreatorTransformCapabilities.All,
                factory)).Value!;
            CreatorWorkbench? callbackTarget = null;
            var workbench = new CreatorWorkbench(
                context,
                SandboxOptions("creator-source-unload"),
                content,
                robots.Agents,
                () => RequireWorkbench(callbackTarget).Hide(),
                () => RequireWorkbench(callbackTarget).EndSession());
            callbackTarget = workbench;

            Assert(workbench.Open().Succeeded, "source-unload fixture should open");
            var window = Surface(context, "creator-source-unload-window");
            Assert(window.SelectListItem("catalog-list", "content:test.creator:crate").Succeeded
                && window.ActivateButton("spawn-selected").Succeeded
                && content.ActiveSpawnCount == 1
                && factory.ActiveCount == 1,
                "registered source content should spawn through the real catalog UI");

            source.Dispose();
            context.AdvanceFrame(TimeSpan.Zero);
            Assert(content.ActiveSpawnCount == 0
                && factory.ActiveCount == 0
                && workbench.IsSessionActive
                && workbench.DescribeStatus().Contains("roster=0", StringComparison.Ordinal),
                "source unload should destroy its instances and prune stale roster entries without ending the session");

            workbench.Dispose();
            context.Dispose();
            context.AssertNoLeaks();
        }

        private static FakeModContext CreateGameplayContext()
        {
            var context = new FakeModContext();
            context.Scenes.Load("RobotopiaCity");
            context.LocalPlayer.Snapshot = new PlayerSnapshot(
                Vec3.Zero,
                new Ray(Vec3.Zero, new Vec3(0f, 0f, 1f)));
            return context;
        }

        private static CreatorWorkbenchOptions SandboxOptions(string id = "creator-lifecycle") =>
            new CreatorWorkbenchOptions(id, "CREATOR TEST", CreatorProjectScope.Sandbox, 16, true, false, 4, 0f);

        private static CreatorWorkbenchOptions GlobalOptions(string id) =>
            new CreatorWorkbenchOptions(id, "CREATOR TEST", CreatorProjectScope.Global, 16, true, false, 4, 0f);

        private static FakeUiSurface Surface(FakeModContext context, string id) =>
            context.Ui.Surfaces.Single(surface => string.Equals(surface.Id, id, StringComparison.Ordinal));

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Creator workbench lifecycle: " + message);
        }

        private static CreatorWorkbench RequireWorkbench(CreatorWorkbench? workbench) =>
            workbench ?? throw new InvalidOperationException(
                "Creator workbench callback fired before initialization completed.");

        private sealed class TestContentFactory : ICreatorContentFactory
        {
            private int nextId;
            public int ActiveCount { get; private set; }

            public OperationResult<ICreatorSourceInstance> Spawn(TransformState transform)
            {
                ActiveCount++;
                return OperationResult<ICreatorSourceInstance>.Success(
                    new TestContentInstance("creator-workbench-" + (++nextId), transform, () => ActiveCount--));
            }
        }

        private sealed class TestContentInstance : ICreatorSourceInstance
        {
            private Action? release;
            private TransformState transform;
            private readonly FakeEntity entity;

            public TestContentInstance(string id, TransformState transform, Action release)
            {
                this.transform = transform;
                this.release = release;
                entity = new FakeEntity(id, "Creator workbench content", transform.Position)
                {
                    Rotation = transform.Rotation,
                    Scale = transform.Scale
                };
            }

            public IEntity Entity => entity;
            public bool IsAlive => release != null && entity.IsAlive;
            public bool TryGetTransform(out TransformState value)
            {
                value = transform;
                return IsAlive;
            }
            public OperationResult<TransformState> SetTransform(TransformState value)
            {
                if (!IsAlive) return OperationResult<TransformState>.Failure(ModErrorCode.InvalidState, "Content instance is disposed.");
                transform = value;
                entity.Position = value.Position;
                entity.Rotation = value.Rotation;
                entity.Scale = value.Scale;
                return OperationResult<TransformState>.Success(value);
            }
            public void Dispose()
            {
                var callback = release;
                release = null;
                entity.Destroy();
                callback?.Invoke();
            }
        }

        private sealed class PassiveCreatorRouter : ICreatorToolHostService
        {
            public CreatorToolHostDescriptor? ActiveHost => null;
            public OperationResult<ICreatorToolHostRegistration> RegisterHost(CreatorToolHostRegistrationRequest request) =>
                OperationResult<ICreatorToolHostRegistration>.Failure(ModErrorCode.Unavailable, "Not needed by this host test.");
            public OperationResult<bool> Toggle() => OperationResult<bool>.Success(false);
            public OperationResult<bool> CloseActive(CreatorToolCloseReason reason = CreatorToolCloseReason.Requested) =>
                OperationResult<bool>.Success(false);
        }
    }
}
