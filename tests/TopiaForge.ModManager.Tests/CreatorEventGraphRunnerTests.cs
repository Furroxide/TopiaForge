using System;
using System.Collections.Generic;
using TopiaForge.CreatorTools;
using TopiaForge.CreatorTools.Shared;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static class CreatorEventGraphRunnerTests
    {
        public static void Run()
        {
            TestScaledDelayAndTypedCondition();
            TestRepeatAndFrameBudget();
            TestDelayCompletionOrder();
            TestNestedNativeTriggerUsesBudgetAndTarget();
            TestTriggerTargetsAreCaseSensitive();
            TestRuntimeExceptionFailsRunner();
            TestGlobalCreatorMultiplayerPolicy();
            Console.WriteLine("Creator event graph runner tests passed.");
        }

        private static void TestScaledDelayAndTypedCondition()
        {
            var runtime = new FakeRuntime();
            var nodes = new[]
            {
                Node("start", CreatorGraphNodeKind.ProjectStart),
                Node("delay", CreatorGraphNodeKind.Delay, ("seconds", "2")),
                Node("condition", CreatorGraphNodeKind.StateCondition, ("value", "true")),
                Node("yes", CreatorGraphNodeKind.ShowToast, ("text", "yes")),
                Node("no", CreatorGraphNodeKind.ShowToast, ("text", "no"))
            };
            var edges = new[]
            {
                Edge("start", "fired", "delay"),
                Edge("delay", "done", "condition"),
                Edge("condition", "true", "yes"),
                Edge("condition", "false", "no")
            };
            using var runner = new CreatorEventGraphRunner(Project(nodes, edges), runtime);
            Assert(runner.Start().Succeeded && runtime.Executed.Count == 0, "delay should not execute downstream actions immediately");
            runner.Update(1f);
            Assert(runtime.Executed.Count == 0, "delay should use accumulated scaled frame time");
            runner.Update(1f);
            Assert(runtime.Executed.Count == 2
                && runtime.Executed[0] == "condition"
                && runtime.Executed[1] == "yes", "condition should emit only its typed true branch");
        }

        private static void TestRepeatAndFrameBudget()
        {
            var runtime = new FakeRuntime();
            var nodes = new[]
            {
                Node("start", CreatorGraphNodeKind.ProjectStart),
                Node("repeat", CreatorGraphNodeKind.Repeat, ("value", "100")),
                Node("each", CreatorGraphNodeKind.ShowToast, ("text", "each")),
                Node("done", CreatorGraphNodeKind.ShowToast, ("text", "done"))
            };
            var edges = new[]
            {
                Edge("start", "fired", "repeat"),
                Edge("repeat", "each", "each"),
                Edge("repeat", "done", "done")
            };
            using var runner = new CreatorEventGraphRunner(Project(nodes, edges), runtime);
            runner.Start();
            Assert(runtime.Executed.Count == 63, "one repeat plus 63 actions should consume the first 64-step frame budget");
            runner.Update(0f);
            Assert(runtime.Executed.Count == 101, "repeat should emit exactly 100 each branches and one done branch");
            runner.Stop();
            Assert(runner.Start().Succeeded && runtime.Executed.Count == 164,
                "a stopped runner should receive a fresh 64-step frame budget when restarted");
        }

        private static void TestDelayCompletionOrder()
        {
            var runtime = new FakeRuntime();
            var nodes = new[]
            {
                Node("start", CreatorGraphNodeKind.ProjectStart),
                Node("a-delay", CreatorGraphNodeKind.Delay, ("seconds", "1")),
                Node("z-delay", CreatorGraphNodeKind.Delay, ("seconds", "1")),
                Node("a-action", CreatorGraphNodeKind.ShowToast, ("text", "a")),
                Node("z-action", CreatorGraphNodeKind.ShowToast, ("text", "z"))
            };
            var edges = new[]
            {
                Edge("start", "fired", "z-delay"),
                Edge("start", "fired", "a-delay"),
                Edge("a-delay", "done", "a-action"),
                Edge("z-delay", "done", "z-action")
            };
            using var runner = new CreatorEventGraphRunner(Project(nodes, edges), runtime);
            runner.Start();
            runner.Update(1f);
            Assert(runtime.Executed.Count == 2
                && runtime.Executed[0] == "a-action"
                && runtime.Executed[1] == "z-action", "simultaneous delays should complete in deterministic insertion order");
        }

        private static void TestNestedNativeTriggerUsesBudgetAndTarget()
        {
            var runtime = new ReentrantRuntime();
            var nodes = new[]
            {
                Node("start", CreatorGraphNodeKind.ProjectStart),
                Node("removed-a", CreatorGraphNodeKind.EntityRemoved, ("nativeBindingId", "native-a")),
                Node("removed-b", CreatorGraphNodeKind.EntityRemoved, ("nativeBindingId", "native-b")),
                Node("spawn", CreatorGraphNodeKind.SpawnContent, ("entityId", "entity-a")),
                Node("despawn", CreatorGraphNodeKind.DespawnContent, ("entityId", "entity-a")),
                Node("wrong", CreatorGraphNodeKind.ShowToast, ("text", "wrong"))
            };
            var edges = new[]
            {
                Edge("start", "fired", "despawn"),
                Edge("removed-a", "fired", "spawn"),
                Edge("removed-b", "fired", "wrong"),
                Edge("spawn", "success", "despawn")
            };
            using var runner = new CreatorEventGraphRunner(Project(nodes, edges), runtime);
            runtime.Runner = runner;
            runner.Start();
            Assert(runner.TotalSteps == 64, "nested trigger dispatch should remain inside the first frame budget");
            Assert(!runtime.Executed.Contains("wrong"), "native trigger matching should use the exact native binding id");
            runner.Update(0f);
            Assert(runner.TotalSteps == 128, "queued implicit trigger work should resume on the next frame without recursion");
        }

        private static void TestGlobalCreatorMultiplayerPolicy()
        {
            var localId = new ParticipantId("local");
            var local = new MultiplayerParticipant(localId, "Local", isLocal: true, isConnected: true);
            var loopback = new MultiplayerSessionSnapshot(
                new MultiplayerSessionId("loopback"),
                MultiplayerSessionState.Ready,
                MultiplayerProcessKind.Interactive,
                MultiplayerExecutionSide.Client | MultiplayerExecutionSide.Server,
                localId,
                new[] { local },
                new NetworkTick(1),
                new SessionSeed(1));
            Assert(CreatorToolsMultiplayerPolicy.Allows(loopback), "single-local loopback should remain eligible");

            var remote = new MultiplayerParticipant(new ParticipantId("remote"), "Remote", isLocal: false, isConnected: true);
            var networked = new MultiplayerSessionSnapshot(
                new MultiplayerSessionId("networked"),
                MultiplayerSessionState.Ready,
                MultiplayerProcessKind.Interactive,
                MultiplayerExecutionSide.Client,
                localId,
                new[] { local, remote },
                new NetworkTick(1),
                new SessionSeed(1));
            Assert(!CreatorToolsMultiplayerPolicy.Allows(networked), "a connected remote participant should fail closed");

            var headless = new MultiplayerSessionSnapshot(
                new MultiplayerSessionId("headless"),
                MultiplayerSessionState.Ready,
                MultiplayerProcessKind.Headless,
                MultiplayerExecutionSide.Server,
                null,
                Array.Empty<MultiplayerParticipant>(),
                new NetworkTick(1),
                new SessionSeed(1));
            Assert(!CreatorToolsMultiplayerPolicy.Allows(headless), "headless processes should fail closed");
        }

        private static void TestTriggerTargetsAreCaseSensitive()
        {
            var runtime = new FakeRuntime();
            var nodes = new[]
            {
                Node("lower-trigger", CreatorGraphNodeKind.EntityRemoved, ("entityId", "crate")),
                Node("upper-trigger", CreatorGraphNodeKind.EntityRemoved, ("entityId", "Crate")),
                Node("lower-action", CreatorGraphNodeKind.ShowToast, ("text", "lower")),
                Node("upper-action", CreatorGraphNodeKind.ShowToast, ("text", "upper"))
            };
            var edges = new[]
            {
                Edge("lower-trigger", "fired", "lower-action"),
                Edge("upper-trigger", "fired", "upper-action")
            };
            using var runner = new CreatorEventGraphRunner(Project(nodes, edges), runtime);
            runner.Start();
            runner.Fire(CreatorGraphNodeKind.EntityRemoved, "crate");
            Assert(runtime.Executed.Count == 1 && runtime.Executed[0] == "lower-action",
                "trigger target ids should use exact persisted casing");
        }

        private static void TestRuntimeExceptionFailsRunner()
        {
            var runtime = new ThrowingRuntime();
            var nodes = new[]
            {
                Node("start", CreatorGraphNodeKind.ProjectStart),
                Node("unsafe", CreatorGraphNodeKind.ShowToast, ("text", "unsafe")),
                Node("after", CreatorGraphNodeKind.ShowToast, ("text", "must not run"))
            };
            var edges = new[]
            {
                Edge("start", "fired", "unsafe"),
                Edge("unsafe", "success", "after")
            };
            using var runner = new CreatorEventGraphRunner(Project(nodes, edges), runtime);
            var escaped = false;
            try
            {
                runner.Start();
            }
            catch (InvalidOperationException)
            {
                escaped = true;
            }
            Assert(!escaped, "runtime Execute exceptions must not escape the bounded runner");
            Assert(!runner.IsRunning && runtime.Calls == 1 && !string.IsNullOrWhiteSpace(runner.LastProblem),
                "a runtime Execute exception should stop the run once and surface a stable problem");
            runner.Update(0f);
            Assert(runtime.Calls == 1, "a failed runner must not execute queued continuation nodes");
        }

        private static CreatorGraphNode Node(
            string id,
            CreatorGraphNodeKind kind,
            params (string Key, string Value)[] parameters)
        {
            var values = new Dictionary<string, string>();
            foreach (var parameter in parameters) values[parameter.Key] = parameter.Value;
            return new CreatorGraphNode(id, kind, Vec2.Zero, values);
        }

        private static CreatorGraphEdge Edge(string from, string port, string to) =>
            new CreatorGraphEdge(from, port, to, "in");

        private static CreatorEventProject Project(
            IReadOnlyList<CreatorGraphNode> nodes,
            IReadOnlyList<CreatorGraphEdge> edges) =>
            new CreatorEventProject(
                1,
                "runner-test",
                "Runner test",
                string.Empty,
                CreatorProjectScope.Sandbox,
                string.Empty,
                string.Empty,
                DateTimeOffset.UtcNow,
                nodes: nodes,
                edges: edges);

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Creator event runner: " + message);
        }

        private sealed class FakeRuntime : ICreatorEventRuntime
        {
            public List<string> Executed { get; } = new List<string>();

            public OperationResult<bool> Execute(CreatorGraphNode node)
            {
                Executed.Add(node.Id);
                if (node.Kind == CreatorGraphNodeKind.StateCondition)
                {
                    return OperationResult<bool>.Success(string.Equals(
                        CreatorEventGraphRunner.Parameter(node, CreatorGraphParameters.Value),
                        "true",
                        StringComparison.OrdinalIgnoreCase));
                }
                return OperationResult<bool>.Success(true);
            }
        }

        private sealed class ReentrantRuntime : ICreatorEventRuntime
        {
            public CreatorEventGraphRunner? Runner { get; set; }
            public List<string> Executed { get; } = new List<string>();

            public OperationResult<bool> Execute(CreatorGraphNode node)
            {
                Executed.Add(node.Id);
                if (node.Kind == CreatorGraphNodeKind.DespawnContent)
                {
                    Runner!.Fire(CreatorGraphNodeKind.EntityRemoved, "native-a");
                }
                return OperationResult<bool>.Success(true);
            }
        }

        private sealed class ThrowingRuntime : ICreatorEventRuntime
        {
            public int Calls { get; private set; }

            public OperationResult<bool> Execute(CreatorGraphNode node)
            {
                Calls++;
                throw new InvalidOperationException("Synthetic creator runtime failure.");
            }
        }
    }
}
