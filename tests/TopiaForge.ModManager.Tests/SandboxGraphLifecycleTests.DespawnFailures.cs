using System;
using System.Linq;
using TopiaForge.CreatorContent;
using TopiaForge.CreatorTools.Shared;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class SandboxGraphLifecycleTests
    {
        private static void TestProductionSourceDespawnFailuresAreReported()
        {
            foreach (var stopProject in new[] { true, false })
            {
                using var context = new FakeModContext();
                context.Scenes.Load("RobotopiaCity");
                context.LocalPlayer.Snapshot = new PlayerSnapshot(Vec3.Zero, new Ray(Vec3.Zero, new Vec3(0f, 0f, 1f)));
                // The production handle converts a source exception to a failed result and retires
                // the source. A later Dispose cannot recover or report that discarded first failure.
                using var content = new CreatorContentService("test.creator", context.Runtime, context.Logger);
                var factory = new Factory("a");
                var unrelated = new Factory("b");
                using var source = content.Register(new CreatorContentRegistrationRequest("a", "a", "Throwing source fixture.",
                    CreatorContentKind.Prop, CreatorTransformCapabilities.All, factory)).Value!;
                using var otherSource = content.Register(new CreatorContentRegistrationRequest("b", "b", "Control source fixture.",
                    CreatorContentKind.Prop, CreatorTransformCapabilities.All, unrelated)).Value!;
                using var unusedSource = content.Register(new CreatorContentRegistrationRequest("c", "c", "Catalog-only fixture.",
                    CreatorContentKind.Prop, CreatorTransformCapabilities.All, new Factory("c"))).Value!;
                var project = Project(new[]
                {
                    Node("start", CreatorGraphNodeKind.ProjectStart),
                    Node("removed", CreatorGraphNodeKind.EntityRemoved, ("entityId", "a")),
                    Node("observed", CreatorGraphNodeKind.ShowToast, ("text", "removed-successfully"))
                }, new[] { Edge("removed", "fired", "observed") }, speaker: false);
                var library = new FakeCreatorProjectLibrary();
                Assert(library.SaveAsync(project).GetAwaiter().GetResult().Succeeded, "save production source fixture");
                using var libraryRegistration = context.Extensions.Register<ICreatorProjectLibrary>(library).Value!;
                var robots = new FakeRobotKit(context.Lifetime);
                using var workbench = new CreatorWorkbench(context,
                    new CreatorWorkbenchOptions("despawn-failure", "DESPAWN FAILURE", CreatorProjectScope.Sandbox, 16, true, true, 4, 0f),
                    content, robots.Agents, () => { }, () => { });
                Assert(workbench.Open().Succeeded && workbench.SpawnRobot().Succeeded, "open with unrelated manual robot");
                var manualRobot = robots.Agents.ActiveAgents.Single();
                context.AdvanceFrame(TimeSpan.Zero);
                var window = context.Ui.Surfaces.Single(surface => surface.Id == "despawn-failure-window");
                Assert(window.SelectListItem("project-list", project.Id).Succeeded, "select production source fixture");
                Click("load-project");
                context.AdvanceFrame(TimeSpan.Zero);
                Click("run-project");
                Assert(factory.ActiveCount == 1 && unrelated.ActiveCount == 1, "both production source instances are live");
                var instance = factory.Instances.Single();
                instance.OnDispose = () => throw new InvalidOperationException("Seeded production source disposal failure.");
                const string failure = "The content source failed while despawning its instance.";
                if (stopProject)
                {
                    Click("stop-project");
                    Assert(context.Ui.Toasts.Any(toast => toast.Tone == UiTone.Danger
                        && toast.Message.Contains(failure, StringComparison.Ordinal)),
                        "Stop must report the production despawn failure instead of successful cleanup");
                    Assert(unrelated.ActiveCount == 0 && unrelated.Instances.Single().DisposeCalls == 1,
                        "Stop must continue independent source cleanup after a failed despawn");
                }
                else
                {
                    var result = ((ICreatorEventRuntime)workbench).Execute(
                        Node("remove", CreatorGraphNodeKind.DespawnContent, ("entityId", "a")));
                    Assert(!result.Succeeded && result.ErrorCode == ModErrorCode.External && result.ErrorMessage == failure,
                        "graph Despawn must return the production failure to its caller");
                    Assert(!context.Ui.Toasts.Any(toast => toast.Message == "removed-successfully"),
                        "a failed graph despawn cannot emit a successful removal event");
                    Assert(unrelated.ActiveCount == 1 && unrelated.Instances.Single().DisposeCalls == 0,
                        "a failed graph despawn preserves the unrelated graph source");
                }
                Assert(instance.DisposeCalls == 1 && manualRobot.IsAlive,
                    "failed source cleanup is attempted once and preserves the unrelated manual robot");

                void Click(string id)
                {
                    Assert(window.ActivateButton(id).Succeeded && window.CallbackErrors.Count == 0,
                        "production source fixture callback " + id);
                }
            }
        }
    }
}
