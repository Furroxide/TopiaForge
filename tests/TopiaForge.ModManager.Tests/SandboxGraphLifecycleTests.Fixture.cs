using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.CreatorTools.Shared;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class SandboxGraphLifecycleTests
    {
        private sealed class Fixture : IDisposable
        {
            private readonly List<IDisposable> registrations = new List<IDisposable>();
            public Fixture(CreatorEventProject project)
            {
                Context.Scenes.Load("RobotopiaCity");
                Context.LocalPlayer.Snapshot = new PlayerSnapshot(Vec3.Zero, new Ray(Vec3.Zero, new Vec3(0f, 0f, 1f)));
                Content = new FakeCreatorContentService(Context.Lifetime, "test.creator");
                Robots = new FakeRobotKit(Context.Lifetime);
                foreach (var id in new[] { "a", "b", "c" })
                {
                    var factory = new Factory(id);
                    Factories[id] = factory;
                    var source = Content.Register(new CreatorContentRegistrationRequest(id, id, "Offline graph fixture.",
                        CreatorContentKind.Prop, CreatorTransformCapabilities.All, factory)).Value!;
                    Sources[id] = source;
                    registrations.Add(source);
                }
                var library = new FakeCreatorProjectLibrary();
                Assert(library.SaveAsync(project).GetAwaiter().GetResult().Succeeded, "save deterministic fixture");
                registrations.Add(Context.Extensions.Register<ICreatorProjectLibrary>(library).Value!);
                registrations.Add(Context.Extensions.Register<IRobotConversationService>(Conversations).Value!);
                registrations.Add(Context.Extensions.Register<IRobotObjectiveService>(Robots.Objectives).Value!);
                Workbench = new CreatorWorkbench(new SandboxGraphTestContext(Context, Audio, Interactions),
                    new CreatorWorkbenchOptions("graph-offline", "GRAPH OFFLINE", CreatorProjectScope.Sandbox, 16, true, true, 4, 0f),
                    Content, Robots.Agents, () => { }, () => { });
                Assert(Workbench.Open().Succeeded && Workbench.SpawnRobot().Succeeded, "open fixture with unrelated manual robot");
                ManualRobot = Robots.Agents.ActiveAgents.Single();
                Project = project;
                Load();
            }
            public FakeModContext Context { get; } = new FakeModContext();
            public SandboxGraphTestAudio Audio { get; } = new SandboxGraphTestAudio();
            public SandboxGraphTestInteractions Interactions { get; } = new SandboxGraphTestInteractions();
            public SandboxGraphTestConversations Conversations { get; } = new SandboxGraphTestConversations();
            public Dictionary<string, Factory> Factories { get; } = new Dictionary<string, Factory>();
            public Dictionary<string, IDisposable> Sources { get; } = new Dictionary<string, IDisposable>();
            public CreatorEventProject Project { get; }
            public FakeCreatorContentService Content { get; }
            public FakeRobotKit Robots { get; }
            public CreatorWorkbench Workbench { get; }
            public IRobotAgent ManualRobot { get; private set; }
            public FakeUiSurface Window => Context.Ui.Surfaces.Single(surface => surface.Id == "graph-offline-window");
            public void Frame() => Context.AdvanceFrame(TimeSpan.Zero);
            public void Load()
            {
                Frame();
                Assert(Window.SelectListItem("project-list", Project.Id).Succeeded, "select fixture project");
                Click("load-project");
                Frame();
            }
            public void Click(string id)
            {
                var result = Window.ActivateButton(id);
                Assert(result.Succeeded && Window.CallbackErrors.Count == 0, "UI fixture callback " + id + ": "
                    + result.ErrorMessage + " " + string.Join(" | ", Window.CallbackErrors));
            }
            public void Start()
            {
                Click("run-project");
                Assert(Workbench.DescribeStatus().Contains("event=running", StringComparison.Ordinal),
                    "graph should be running: " + Window.Body + " " + string.Join(" | ", Context.Ui.Toasts.Select(item => item.Message)));
            }
            public void Submit()
            {
                Assert(Window.SelectListItem("roster-list", "project:speaker").Succeeded, "select graph conversation speaker");
                Assert(Window.ChangeText("program-line", "Synthetic offline request").Succeeded, "enter synthetic request");
                Click("send-program-line");
                Assert(Conversations.Handles.Last().SubmitCalls == 1, "one deterministic turn is pending");
            }
            public void AssertGraphReleased(string cause)
            {
                Assert(Audio.ActiveCount == 0 && Interactions.ActiveCount == 0 && Conversations.ActiveCount == 0,
                    cause + " must release graph audio/interaction/conversation handles");
                Assert(Content.ActiveSpawnCount == 0 && Factories.Values.All(factory => factory.ActiveCount == 0),
                    cause + " must destroy all graph source objects");
                Assert(Robots.Agents.ActiveAgents.Count == 1 && ReferenceEquals(Robots.Agents.ActiveAgents[0], ManualRobot)
                    && ManualRobot.IsAlive && Workbench.DescribeStatus().Contains("roster=1", StringComparison.Ordinal),
                    cause + " must preserve the unrelated manual robot and roster entry");
                Assert(Workbench.DescribeStatus().Contains("event=stopped", StringComparison.Ordinal), cause + " must stop graph scheduling");
            }
            public void NextSession()
            {
                Assert(Workbench.EndSession().Succeeded, "end full offline cycle");
                Assert(Content.ActiveSessionCount == 0 && Robots.Agents.ActiveAgents.Count == 0
                    && Context.LocalPlayer.ActiveControlLeaseCount == 0 && Robots.Objectives.TargetNames.Count == 0,
                    "full cycle must immediately release session/control/robot/objective ownership");
                Assert(Workbench.Open().Succeeded && Workbench.SpawnRobot().Succeeded, "reopen next offline session");
                ManualRobot = Robots.Agents.ActiveAgents.Single();
                Load();
            }
            public void Dispose()
            {
                Workbench.Dispose();
                foreach (var registration in registrations) registration.Dispose();
                Content.Dispose();
                Context.Dispose();
                Context.AssertNoLeaks();
                Assert(Audio.ActiveCount == 0 && Interactions.ActiveCount == 0 && Conversations.ActiveCount == 0,
                    "fixture disposal must not hide resource leaks in custom observers");
            }
        }

        private sealed class Factory : ICreatorContentFactory
        {
            private readonly string id;
            public Factory(string id) { this.id = id; }
            public List<Instance> Instances { get; } = new List<Instance>();
            public int ActiveCount => Instances.Count(instance => instance.IsAlive);
            public OperationResult<ICreatorSourceInstance> Spawn(TransformState transform)
            {
                var instance = new Instance(id + "-" + Instances.Count, transform);
                Instances.Add(instance);
                return OperationResult<ICreatorSourceInstance>.Success(instance);
            }
        }
        private sealed class Instance : ICreatorSourceInstance
        {
            private TransformState transform;
            private readonly FakeEntity entity;
            public Instance(string id, TransformState transform)
            {
                this.transform = transform;
                entity = new FakeEntity(id, id, transform.Position);
            }
            public IEntity Entity => entity;
            public bool IsAlive => entity.IsAlive;
            public int DisposeCalls { get; private set; }
            public Action? OnDispose { get; set; }
            public bool TryGetTransform(out TransformState value) { value = transform; return IsAlive; }
            public OperationResult<TransformState> SetTransform(TransformState value)
            {
                transform = value;
                entity.Position = value.Position;
                return OperationResult<TransformState>.Success(value);
            }
            public void Dispose()
            {
                DisposeCalls++;
                entity.Destroy();
                var callback = OnDispose;
                OnDispose = null;
                callback?.Invoke();
            }
        }

        private static CreatorGraphNode Node(string id, CreatorGraphNodeKind kind, params (string Key, string Value)[] parameters) =>
            new CreatorGraphNode(id, kind, Vec2.Zero, parameters.ToDictionary(item => item.Key, item => item.Value));
        private static CreatorGraphEdge Edge(string from, string port, string to) => new CreatorGraphEdge(from, port, to, "in");
        private static CreatorProjectEntity Entity(string id, bool spawnOnStart = true) => new CreatorProjectEntity(
            id, id, id == "speaker" ? "io.github.furroxide.topiaforge.robotkit:default" : "test.creator:" + id,
            string.Empty, TransformState.Identity, spawnOnStart: spawnOnStart);
        private static CreatorEventProject Project(IReadOnlyList<CreatorGraphNode> nodes, IReadOnlyList<CreatorGraphEdge> edges,
            bool speaker = true) => new CreatorEventProject(1, "graph-lifecycle", "Graph lifecycle", string.Empty,
                CreatorProjectScope.Sandbox, string.Empty, string.Empty, DateTimeOffset.UnixEpoch,
                entities: speaker ? new[] { Entity("a"), Entity("b"), Entity("speaker") }
                    : new[] { Entity("a"), Entity("b"), Entity("c", false) },
                personas: new[] { new CreatorPersona("persona", "Offline persona", "Synthetic test fixture", "") },
                nodes: nodes, edges: edges);
        private static CreatorEventProject ResourceProject(bool delayedFault = false) => Project(new[]
        {
            Node("start", CreatorGraphNodeKind.ProjectStart),
            Node("audio-a", CreatorGraphNodeKind.PlayAudio, ("cueId", "graph-a")),
            Node("audio-b", CreatorGraphNodeKind.PlayAudio, ("cueId", "graph-b")),
            Node("chat", CreatorGraphNodeKind.BeginConversation, ("entityId", "speaker"), ("personaId", "persona")),
            Node("interact-a", CreatorGraphNodeKind.InteractionTrigger, ("entityId", "a")),
            Node("interact-b", CreatorGraphNodeKind.InteractionTrigger, ("entityId", "b")),
            Node("decision", CreatorGraphNodeKind.ConversationDecision, ("entityId", "speaker")),
            Node("observed", CreatorGraphNodeKind.ShowToast, ("text", "graph-callback-observed")),
            Node("delay", CreatorGraphNodeKind.Delay, ("seconds", "1")),
            Node("fault", CreatorGraphNodeKind.PlayAudio, ("cueId", "fault"))
        }, new[]
        {
            Edge("start", "fired", "audio-a"), Edge("audio-a", "success", "audio-b"), Edge("audio-b", "success", "chat"),
            Edge("interact-a", "fired", "observed"), Edge("interact-b", "fired", "observed"), Edge("decision", "fired", "observed"),
            Edge(delayedFault ? "chat" : "unreachable", "success", "delay"), Edge("delay", "done", "fault")
        }.Where(edge => edge.FromNodeId != "unreachable").ToArray());
        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Sandbox graph lifecycle (offline): " + message);
        }
    }
}
