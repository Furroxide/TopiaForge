using System;
using System.Linq;
using TopiaForge.Mods;
using TopiaForge.CreatorTools.Shared;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class SandboxGraphLifecycleTests
    {
        public static void Run()
        {
            TestStopRejectsRestartDuringCleanup();
            TestRemovalCallbackCannotRemoveSameTargetTwice();
            TestTenGraphResourceCyclesRejectRetiredCallbacks();
            TestRespawnRejectsRetiredInteraction();
            TestGraphFaultRollsBackEveryOwnedCategory();
            TestThrowingDisposersAttemptAllCleanup();
            TestProductionSourceDespawnFailuresAreReported();
            TestCancelledAndFaultedConversationCleanup();
            TestReentrantSourceRemovalPreservesUnrelatedContent();
            TestCompletedAudioIsPrunedWithoutAffectingOtherOwners();
            Console.WriteLine("Sandbox graph lifecycle offline tests passed (11 cases; 10 complete resource cycles).");
        }

        private static void TestStopRejectsRestartDuringCleanup()
        {
            foreach (var category in new[] { "audio", "conversation", "interaction" })
            {
                using var fixture = new Fixture(ResourceProject());
                fixture.Start();
                Assert(fixture.Window.TryFindNode("run-project", out var runNode) && runNode is UiButton,
                    "capture original Run callback for disposer reentrancy");
                Assert(fixture.Window.TryFindNode("stop-project", out var stopNode) && stopNode is UiButton,
                    "capture original Stop callback for idempotence");
                var attempts = 0;
                Action restart = () => { attempts++; ((UiButton)runNode!).Activated(); };
                if (category == "audio") fixture.Audio.Handles[0].OnDispose = restart;
                else if (category == "conversation") fixture.Conversations.Handles[0].OnDispose = restart;
                else fixture.Interactions.Handles[0].OnDispose = restart;
                fixture.Click("stop-project");
                Assert(attempts == 1 && fixture.Audio.Handles.Count == 2 && fixture.Conversations.Handles.Count == 1,
                    "a " + category + " disposer must not start a replacement graph during Stop");
                fixture.AssertGraphReleased(category + " reentrant restart");
                Assert(fixture.Context.Ui.Toasts.Any(toast => toast.Tone == UiTone.Danger
                    && toast.Message.Contains("cleanup is in progress", StringComparison.Ordinal)),
                    "reentrant Run receives an explicit conflict rather than a silently lost start");
                ((UiButton)stopNode!).Activated();
                fixture.AssertGraphReleased("idempotent idle Stop");
                fixture.Start();
                fixture.Click("stop-project");
                fixture.AssertGraphReleased("explicit restart after completed Stop");
            }
        }

        private static void TestRemovalCallbackCannotRemoveSameTargetTwice()
        {
            using var fixture = new Fixture(Project(new[]
            {
                Node("start", CreatorGraphNodeKind.ProjectStart),
                Node("removed", CreatorGraphNodeKind.EntityRemoved, ("entityId", "a")),
                Node("observed", CreatorGraphNodeKind.ShowToast, ("text", "removed-once"))
            }, new[] { Edge("removed", "fired", "observed") }, speaker: false));
            fixture.Start();
            var instance = fixture.Factories["a"].Instances.Single();
            var runtime = (ICreatorEventRuntime)fixture.Workbench;
            var remove = Node("remove", CreatorGraphNodeKind.DespawnContent, ("entityId", "a"));
            OperationResult<bool>? reentered = null;
            instance.OnDispose = () => reentered = runtime.Execute(remove);
            Assert(runtime.Execute(remove).Succeeded && reentered?.Succeeded == true && reentered?.Value == false,
                "source disposal callback sees the target already removed and cannot remove it twice");
            fixture.Sources["a"].Dispose();
            fixture.Frame();
            Assert(instance.DisposeCalls == 1
                && fixture.Context.Ui.Toasts.Count(toast => toast.Message == "removed-once") == 1,
                "reentrant removal and subsequent source unload dispose once and emit one removal event");
            Assert(fixture.ManualRobot.IsAlive && fixture.Content.ActiveSpawnCount == 1,
                "reentrant removal preserves both the manual robot and unrelated graph object");
        }

        private static void TestTenGraphResourceCyclesRejectRetiredCallbacks()
        {
            using var fixture = new Fixture(ResourceProject());
            SandboxGraphTestInteractions.Registration? retired = null;
            SandboxGraphTestConversations.Conversation? oldConversation = null;
            var baseline = fixture.Context.Lifetime.TrackedResourceCount;
            for (var cycle = 0; cycle < 10; cycle++)
            {
                fixture.Start();
                Assert(fixture.Audio.ActiveCount == 2 && fixture.Interactions.ActiveCount == 2
                    && fixture.Conversations.ActiveCount == 1 && fixture.Content.ActiveSpawnCount == 2
                    && fixture.Robots.Agents.ActiveAgents.Count == 2, "cycle " + cycle + " owns the complete graph fixture");
                var callbacksBefore = fixture.Context.Ui.Toasts.Count(toast => toast.Message == "graph-callback-observed");
                retired?.DeliverQueuedCallback();
                oldConversation?.Complete("AUTONOMOUS");
                fixture.Frame();
                Assert(fixture.Context.Ui.Toasts.Count(toast => toast.Message == "graph-callback-observed") == callbacksBefore,
                    "callbacks/completions from a retired run cannot execute against its replacement");
                var active = fixture.Interactions.Handles.Last();
                active.DeliverQueuedCallback();
                Assert(fixture.Context.Ui.Toasts.Count(toast => toast.Message == "graph-callback-observed") == callbacksBefore + 1,
                    "a current interaction must reach the graph, preventing a disabled-driver false positive");
                fixture.Submit();
                oldConversation = fixture.Conversations.Handles.Last();
                retired = active;
                Assert(fixture.Workbench.Hide().Succeeded && fixture.Workbench.Open().Succeeded
                    && fixture.Audio.ActiveCount == 2 && fixture.Conversations.ActiveCount == 1,
                    "hide/reopen retains this graph's session and resources");
                fixture.Click("stop-project");
                fixture.AssertGraphReleased("cycle " + cycle + " Stop");
                var released = fixture.Audio.Handles.Skip(cycle * 2).ToArray();
                Assert(released.All(playback => playback.StopCalls == 1 && playback.DisposeCalls == 1),
                    "Stop attempts each audio stop/disposal exactly once");
                foreach (var playback in released) playback.Complete();
                fixture.Frame();
                fixture.AssertGraphReleased("late audio completion");
                fixture.NextSession();
                Assert(fixture.Context.Lifetime.TrackedResourceCount == baseline,
                    "cycle " + cycle + " has zero retained tracked-resource delta without GC or retries");
            }
            oldConversation!.Complete();
            fixture.Frame();
            Assert(fixture.Conversations.ActiveCount == 0, "last late reply cannot create a fresh conversation");
        }

        private static void TestRespawnRejectsRetiredInteraction()
        {
            using var fixture = new Fixture(ResourceProject());
            fixture.Start();
            var retired = fixture.Interactions.Handles.First();
            var runtime = (ICreatorEventRuntime)fixture.Workbench;
            Assert(runtime.Execute(Node("remove", CreatorGraphNodeKind.DespawnContent, ("entityId", "a"))).Succeeded
                && runtime.Execute(Node("replace", CreatorGraphNodeKind.SpawnContent, ("entityId", "a"))).Succeeded,
                "replace a graph target during the same run");
            var before = fixture.Context.Ui.Toasts.Count(toast => toast.Message == "graph-callback-observed");
            retired.DeliverQueuedCallback();
            Assert(fixture.Context.Ui.Toasts.Count(toast => toast.Message == "graph-callback-observed") == before,
                "a retired target registration cannot address its replacement during the same graph run");
            fixture.Interactions.Handles.Last().DeliverQueuedCallback();
            Assert(fixture.Context.Ui.Toasts.Count(toast => toast.Message == "graph-callback-observed") == before + 1,
                "the replacement registration remains functional");
        }

        private static void TestGraphFaultRollsBackEveryOwnedCategory()
        {
            using var fixture = new Fixture(ResourceProject(delayedFault: true));
            fixture.Audio.FaultCue = "fault";
            fixture.Start();
            fixture.Submit();
            var pending = fixture.Conversations.Handles.Single();
            fixture.Context.AdvanceFrame(TimeSpan.FromSeconds(1));
            fixture.AssertGraphReleased("unhandled graph action fault");
            Assert(fixture.Context.Ui.Toasts.Any(toast => toast.Tone == UiTone.Danger
                && toast.Message.Contains("Seeded graph audio failure", StringComparison.Ordinal)),
                "the original graph fault is visible after cleanup");
            pending.Complete();
            fixture.Frame();
            Assert(!fixture.Context.Ui.Toasts.Any(toast => toast.Message == "graph-callback-observed"),
                "a late reply after a graph fault cannot execute a continuation");
        }

        private static void TestThrowingDisposersAttemptAllCleanup()
        {
            using var fixture = new Fixture(ResourceProject());
            fixture.Start();
            fixture.Submit();
            fixture.Audio.Handles[0].ThrowOnStop = true;
            fixture.Audio.Handles[0].ThrowOnDispose = true;
            fixture.Interactions.Handles[0].ThrowOnDispose = true;
            fixture.Conversations.Handles.Single().ThrowOnDispose = true;
            // Disposal re-enters the UI Stop action; ownership must already be detached.
            Assert(fixture.Window.TryFindNode("stop-project", out var stopNode) && stopNode is UiButton,
                "capture the original enabled Stop callback");
            fixture.Interactions.Handles[1].OnDispose = ((UiButton)stopNode!).Activated;
            fixture.Click("stop-project");
            fixture.AssertGraphReleased("throwing graph disposers");
            Assert(fixture.Audio.Handles.All(playback => playback.StopCalls == 1 && playback.DisposeCalls == 1)
                && fixture.Interactions.Handles.All(handle => handle.DisposeCalls == 1)
                && fixture.Conversations.Handles.Single().DisposeCalls == 1,
                "each failing or reentrant resource receives one cleanup attempt while independent cleanup continues");
            Assert(fixture.Context.Ui.Toasts.Any(toast => toast.Tone == UiTone.Danger
                && toast.Message.Contains("cleanup problems", StringComparison.Ordinal)),
                "cleanup faults must be reported as failures rather than successful restoration");
        }

        private static void TestCancelledAndFaultedConversationCleanup()
        {
            foreach (var cancelled in new[] { true, false })
            {
                using var fixture = new Fixture(ResourceProject());
                fixture.Start();
                fixture.Submit();
                var conversation = fixture.Conversations.Handles.Single();
                if (cancelled) conversation.Completion.SetCanceled();
                else conversation.Completion.SetException(new InvalidOperationException("Seeded asynchronous reply fault."));
                fixture.Frame();
                Assert(conversation.DisposeCalls == 1 && fixture.Conversations.ActiveCount == 0,
                    "cancelled/faulted reply tasks terminate their conversation handle");
                Assert(fixture.Workbench.DescribeStatus().Contains("event=running", StringComparison.Ordinal)
                    && !fixture.Context.Ui.Toasts.Any(toast => toast.Message == "graph-callback-observed"),
                    "failed asynchronous turns cannot emit successful graph decisions or disturb unrelated graph resources");
                fixture.Click("stop-project");
                fixture.AssertGraphReleased("Stop following asynchronous reply failure");
            }
        }

        private static void TestReentrantSourceRemovalPreservesUnrelatedContent()
        {
            var project = Project(new[]
            {
                Node("start", CreatorGraphNodeKind.ProjectStart),
                Node("removed", CreatorGraphNodeKind.EntityRemoved, ("entityId", "a")),
                Node("remove-b", CreatorGraphNodeKind.DespawnContent, ("entityId", "b")),
                Node("spawn-c", CreatorGraphNodeKind.SpawnContent, ("entityId", "c"))
            }, new[] { Edge("removed", "fired", "remove-b"), Edge("remove-b", "success", "spawn-c") }, speaker: false);
            using var fixture = new Fixture(project);
            fixture.Start();
            fixture.Sources["a"].Dispose();
            fixture.Frame();
            Assert(fixture.Factories["a"].ActiveCount == 0 && fixture.Factories["b"].ActiveCount == 0
                && fixture.Factories["c"].ActiveCount == 1 && fixture.Content.ActiveSpawnCount == 1,
                "source removal can re-enter graph removal/spawn while stale roster indices are never reused");
            Assert(fixture.ManualRobot.IsAlive && fixture.Workbench.DescribeStatus().Contains("roster=2", StringComparison.Ordinal)
                && fixture.Context.Logger.Count(CapturedLogLevel.Error) == 0,
                "reentrant removal preserves unrelated manual content without swallowed update exceptions");
            fixture.Click("stop-project");
            fixture.AssertGraphReleased("Stop after source-removal reentrancy");
        }

        private static void TestCompletedAudioIsPrunedWithoutAffectingOtherOwners()
        {
            using var fixture = new Fixture(ResourceProject());
            var unrelated = fixture.Audio.Play(new AudioPlayRequest("unrelated-manual-cue")).Value!;
            fixture.Start();
            var completed = fixture.Audio.Handles.Single(playback => playback.Cue == "graph-a");
            completed.Complete();
            var replayed = ((ICreatorEventRuntime)fixture.Workbench).Execute(
                Node("replay", CreatorGraphNodeKind.PlayAudio, ("cueId", "graph-c")));
            Assert(replayed.Succeeded && completed.DisposeCalls == 1 && fixture.Audio.ActiveCount == 3,
                "the next graph action prunes the completed handle while retaining playing and unrelated handles");
            fixture.Click("stop-project");
            Assert(fixture.Audio.ActiveCount == 1 && unrelated.IsPlaying && completed.DisposeCalls == 1,
                "graph Stop disposes completed handles and preserves unrelated audio ownership");
            unrelated.Dispose();
            fixture.AssertGraphReleased("completed audio cleanup");
        }
    }
}
