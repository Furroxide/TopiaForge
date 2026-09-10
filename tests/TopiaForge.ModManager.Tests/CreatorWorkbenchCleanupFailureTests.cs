using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.CreatorContent;
using TopiaForge.CreatorTools.Shared;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static class CreatorWorkbenchCleanupFailureTests
    {
        private const string Failure = "The content source failed while despawning its instance.";

        public static void Run()
        {
            foreach (var route in new[] { "stop", "graph", "remove", "undo", "cleanup", "end", "restart", "load", "create", "delete", "creator-robot" })
            {
                using var fixture = new Fixture(route == "creator-robot");
                if (route == "undo") fixture.SpawnManualContent();
                else fixture.Start();
                var instance = fixture.Primary.Instances.Single();
                instance.ThrowOnDispose = true;
                var before = fixture.Context.Ui.Toasts.Count;
                switch (route)
                {
                    case "stop":
                    case "creator-robot": fixture.Click("stop-project"); break;
                    case "graph":
                        var removed = ((ICreatorEventRuntime)fixture.Workbench).Execute(
                            Node("remove", CreatorGraphNodeKind.DespawnContent, ("entityId", "a")));
                        Check(!removed.Succeeded && removed.ErrorCode == ModErrorCode.External && removed.ErrorMessage.Contains(Failure),
                            "graph despawn must return its production source failure");
                        Check(!fixture.Context.Ui.Toasts.Any(toast => toast.Message == "removed-successfully"),
                            "failed despawn must not fire a successful removal event");
                        break;
                    case "remove":
                        Check(fixture.Window.SelectListItem("roster-list", "project:a").Succeeded, "select project-owned source");
                        fixture.Click("remove-selected");
                        break;
                    case "undo": CheckFailure(fixture.Workbench.Undo(), route); break;
                    case "cleanup": CheckFailure(fixture.Workbench.CleanUpEverything(), route); break;
                    case "end":
                        var ended = fixture.Workbench.EndSession();
                        Check(!ended.Succeeded && ended.ErrorMessage.Contains(Failure), "End Session must report failed owned cleanup");
                        break;
                    case "restart": fixture.Click("run-project"); break;
                    case "load": fixture.Click("load-project"); fixture.Frame(); break;
                    case "create": fixture.Click("new-project"); break;
                    case "delete":
                        fixture.Click("delete-project");
                        fixture.Context.Ui.Modals.Last().Confirm();
                        fixture.Frame();
                        Check(fixture.Library.ListAsync().GetAwaiter().GetResult().Value!.Projects.Count == 1,
                            "failed cleanup must stop project deletion before the library changes");
                        break;
                }
                if (route != "graph" && route != "undo" && route != "cleanup" && route != "end")
                    Check(fixture.Context.Ui.Toasts.Skip(before).Any(toast => toast.Tone == UiTone.Danger && toast.Message.Contains(Failure)),
                        route + " must preserve failed cleanup feedback instead of reporting success");
                Check(instance.DisposeCalls == 1, route + " must attempt failed source cleanup once");
                if (route == "creator-robot")
                    Check(instance.Entity is IRobotAgent && instance.AliveBeforeDispose,
                        "creator source must own robot cleanup before its exposed RobotKit handle is destroyed");
                Check(fixture.Primary.Instances.Count == 1, route + " must not silently start a replacement source");
                var retiresAll = route != "graph" && route != "remove" && route != "undo";
                if (route != "undo")
                    Check(fixture.Control.Instances.Single().DisposeCalls == (retiresAll ? 1 : 0),
                        route + " must preserve or finish unrelated source cleanup as requested");
                if (route != "cleanup" && route != "end")
                    Check(fixture.ManualRobot.IsAlive, route + " must preserve the unrelated manual robot");
                else
                    Check(!fixture.ManualRobot.IsAlive, route + " must finish remaining owned cleanup despite failure");
            }
            TestRemovalCannotRestoreRetiredHistory();
            TestStartPreservesOriginalAndCleanupFailures();
            Console.WriteLine("CreatorWorkbenchCleanupFailureTests passed (11 production-source failure routes, session retirement, and startup rollback).");
        }

        private static void TestRemovalCannotRestoreRetiredHistory()
        {
            using var fixture = new Fixture();
            fixture.Start();
            fixture.Primary.Instances.Single().OnDispose = () =>
                Check(fixture.Workbench.EndSession().Succeeded, "source disposal ends the old session");
            Check(fixture.Window.SelectListItem("roster-list", "project:a").Succeeded, "select reentrant source");
            fixture.Click("remove-selected");
            Check(!fixture.Workbench.IsSessionActive && fixture.Primary.Instances.Single().DisposeCalls == 1,
                "source cleanup may retire its original session once");
            var undo = fixture.Workbench.Undo();
            Check(!undo.Succeeded && undo.ErrorCode == ModErrorCode.NotFound,
                "a retired removal cannot restore history after End Session cleared it");
            var status = ((UiText)((UiColumn)fixture.Window.Content!).Children.Last()).Text;
            Check(status.StartsWith("Session ended;", StringComparison.Ordinal),
                "the stale removal result cannot overwrite the session-ended status");
        }

        private static void TestStartPreservesOriginalAndCleanupFailures()
        {
            using var fixture = new Fixture();
            fixture.Primary.ThrowOnInstanceDispose = true;
            fixture.Control.FailSpawn = true;
            fixture.Load();
            fixture.Click("run-project");
            Check(fixture.Context.Ui.Toasts.Any(toast => toast.Tone == UiTone.Danger
                && toast.Message.StartsWith("Seeded second source spawn failure.", StringComparison.Ordinal)
                && toast.Message.Contains(Failure)),
                "startup failure must remain first and retain the rollback source failure");
            Check(fixture.Primary.Instances.Single().DisposeCalls == 1 && fixture.ManualRobot.IsAlive,
                "failed startup still attempts owned rollback and preserves unrelated content");
        }

        private static void CheckFailure(OperationResult<string> result, string route) =>
            Check(!result.Succeeded && result.ErrorCode == ModErrorCode.External && result.ErrorMessage.Contains(Failure),
                route + " must return the production source failure");
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Creator cleanup regression: " + message);
        }
        private static CreatorGraphNode Node(string id, CreatorGraphNodeKind kind, params (string Key, string Value)[] parameters) =>
            new CreatorGraphNode(id, kind, Vec2.Zero, parameters.ToDictionary(pair => pair.Key, pair => pair.Value));

        private sealed class Fixture : IDisposable
        {
            private readonly CreatorContentService content;
            private readonly ICreatorContentRegistration primarySource;
            private readonly ICreatorContentRegistration controlSource;
            private readonly IDisposable libraryRegistration;
            private readonly CreatorEventProject project;
            public Fixture(bool creatorRobot = false)
            {
                Context.Scenes.Load("RobotopiaCity");
                Context.LocalPlayer.Snapshot = new PlayerSnapshot(Vec3.Zero, new Ray(Vec3.Zero, new Vec3(0f, 0f, 1f)));
                content = new CreatorContentService("test.creator", Context.Runtime, Context.Logger);
                primarySource = content.Register(new CreatorContentRegistrationRequest("a", "a", "Failing source",
                    CreatorContentKind.Prop, CreatorTransformCapabilities.All, Primary)).Value!;
                controlSource = content.Register(new CreatorContentRegistrationRequest("b", "b", "Unrelated source",
                    CreatorContentKind.Prop, CreatorTransformCapabilities.All, Control)).Value!;
                project = new CreatorEventProject(1, "cleanup", "Cleanup", "", CreatorProjectScope.Sandbox, "", "", DateTimeOffset.UnixEpoch,
                    entities: new[] { Entity("a"), Entity("b") },
                    nodes: new[] { Node("start", CreatorGraphNodeKind.ProjectStart),
                        Node("removed", CreatorGraphNodeKind.EntityRemoved, ("entityId", "a")),
                        Node("observed", CreatorGraphNodeKind.ShowToast, ("text", "removed-successfully")) },
                    edges: new[] { new CreatorGraphEdge("removed", "fired", "observed", "in") });
                Check(Library.SaveAsync(project).GetAwaiter().GetResult().Succeeded, "save cleanup project");
                libraryRegistration = Context.Extensions.Register<ICreatorProjectLibrary>(Library).Value!;
                var robots = new FakeRobotKit(Context.Lifetime);
                if (creatorRobot) Primary.CreateRobot = transform => robots.Agents.Spawn(new RobotAgentSpawnRequest(transform.Position)).Value!;
                Workbench = new CreatorWorkbench(Context,
                    new CreatorWorkbenchOptions("cleanup-failure", "CLEANUP FAILURE", CreatorProjectScope.Sandbox, 16, true, true, 4, 0f),
                    content, robots.Agents, () => { }, () => { });
                Check(Workbench.Open().Succeeded && Workbench.SpawnRobot().Succeeded, "open with unrelated manual robot");
                ManualRobot = robots.Agents.ActiveAgents.Single();
                Frame();
            }
            public FakeModContext Context { get; } = new FakeModContext();
            public FakeCreatorProjectLibrary Library { get; } = new FakeCreatorProjectLibrary();
            public Factory Primary { get; } = new Factory();
            public Factory Control { get; } = new Factory();
            public IRobotAgent ManualRobot { get; }
            public CreatorWorkbench Workbench { get; }
            public FakeUiSurface Window => Context.Ui.Surfaces.Single(surface => surface.Id == "cleanup-failure-window");
            public void Frame() => Context.AdvanceFrame(TimeSpan.Zero);
            public void Click(string id) => Check(Window.ActivateButton(id).Succeeded && Window.CallbackErrors.Count == 0,
                "cleanup fixture callback " + id + ": " + string.Join(" | ", Window.CallbackErrors));
            public void Load()
            {
                Check(Window.SelectListItem("project-list", project.Id).Succeeded, "select cleanup project");
                Click("load-project"); Frame();
            }
            public void Start()
            {
                Load(); Click("run-project");
                Check(Primary.Instances.Count == 1 && Control.Instances.Count == 1, "both production sources spawned");
            }
            public void SpawnManualContent()
            {
                Check(Window.SelectListItem("catalog-list", "content:test.creator:a").Succeeded, "select manual source");
                Check(Workbench.SpawnSelected().Succeeded, "spawn manual source for undo");
            }
            public void Dispose()
            {
                Workbench.Dispose(); libraryRegistration.Dispose(); primarySource.Dispose(); controlSource.Dispose(); content.Dispose();
                Context.Dispose(); Context.AssertNoLeaks();
            }
            private static CreatorProjectEntity Entity(string id) => new CreatorProjectEntity(id, id, "test.creator:" + id,
                "", TransformState.Identity, spawnOnStart: true);
        }

        private sealed class Factory : ICreatorContentFactory
        {
            public List<Instance> Instances { get; } = new List<Instance>();
            public Func<TransformState, IRobotAgent>? CreateRobot { get; set; }
            public bool FailSpawn { get; set; }
            public bool ThrowOnInstanceDispose { get; set; }
            public OperationResult<ICreatorSourceInstance> Spawn(TransformState transform)
            {
                if (FailSpawn) return OperationResult<ICreatorSourceInstance>.Failure(ModErrorCode.External, "Seeded second source spawn failure.");
                var instance = new Instance("source-" + Instances.Count, transform, CreateRobot?.Invoke(transform))
                { ThrowOnDispose = ThrowOnInstanceDispose };
                Instances.Add(instance);
                return OperationResult<ICreatorSourceInstance>.Success(instance);
            }
        }
        private sealed class Instance : ICreatorSourceInstance
        {
            private readonly IEntity entity;
            private TransformState transform;
            public Instance(string id, TransformState transform, IRobotAgent? robot) { entity = (IEntity?)robot ?? new FakeEntity(id, id, transform.Position); this.transform = transform; }
            public IEntity Entity => entity;
            public bool IsAlive => entity.IsAlive;
            public int DisposeCalls { get; private set; }
            public bool ThrowOnDispose { get; set; }
            public Action? OnDispose { get; set; }
            public bool AliveBeforeDispose { get; private set; }
            public bool TryGetTransform(out TransformState value) { value = transform; return IsAlive; }
            public OperationResult<TransformState> SetTransform(TransformState value) { transform = value; return OperationResult<TransformState>.Success(value); }
            public void Dispose()
            {
                DisposeCalls++;
                AliveBeforeDispose = entity.IsAlive;
                if (entity is IRobotAgent robot) robot.Despawn();
                else ((FakeEntity)entity).Destroy();
                var callback = OnDispose;
                OnDispose = null;
                callback?.Invoke();
                if (ThrowOnDispose) throw new InvalidOperationException("Seeded production creator-source disposal failure.");
            }
        }
    }
}
