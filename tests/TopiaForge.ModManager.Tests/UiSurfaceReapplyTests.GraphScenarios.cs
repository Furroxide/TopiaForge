namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiSurfaceReapplyTests
    {
        private const string GraphScenarios = """
            using System;
            using System.Linq;
            using TopiaForge.Mods;
            using TopiaForge.Mods.Testing;
            using TopiaForge.Mods.UnityUi;
            using TopiaForge.TestSupport;
            using UnityEngine;
            using UnityEngine.EventSystems;
            using UnityEngine.UI;

            namespace TopiaForge.ModManager
            {
                internal sealed partial class OwnerUiService
                {
                    private static void ProbeGraphAdmission()
                    {
                        var host = new UiHost();
                        var window = new TopiaForgeWindow(host);
                        var parent = new TopiaForgeContainer(host);
                        using var lifetime = new FakeModLifetime();
                        var logger = new BoundaryLogger();
                        using var surface = UnityUiSurface.ForWindow("graph-probe", window, new TopiaForgeLabel(),
                            parent, lifetime, logger, _ => { });
                        surface.SetClock(() => 10d, () => "graph-world");
                        surface.Present();
                        var previousCalls = 0;
                        var replacementCalls = 0;
                        var initial = Graph(160, "source", UiGraphViewport.Default, _ => previousCalls++);
                        Require(surface.SetContent(initial).Succeeded, "The graph was admitted through the native layout.");
                        var admittedRoot = parent.Children.Single();
                        var widget = host.Graphs.Single();
                        var previousParent = widget.Go.transform.parent;
                        var previousBinding = widget.Graph;
                        var previousBindCount = widget.BindCount;
                        var previousMoves = widget.Go.ParentChanges;
                        var previousViewport = new UiGraphViewport(new Vec2(40, -15), 1.2f);
                        var drag = new Vector2(14, -33);
                        widget.SetLiveState(previousViewport, "target", "send", drag);

                        var replacementViewport = new UiGraphViewport(new Vec2(-60, -5), 1.5f);
                        var replacement = Graph(220, "source", replacementViewport, _ => replacementCalls++);
                        var events = new EventSystem();
                        EventSystem.current = events;
                        foreach (var target in new[] { widget.FirstHeader, widget.FirstPort })
                        {
                            events.SetSelectedGameObject(target);
                            var selectionChanges = events.SelectionChanges;
                            var disabled = target.DisableCalls;
                            var unboundTimer = new UiCountdown(25, "Candidate");
                            var rejected = surface.SetContent(new UiColumn(new UiSizedBox(replacement, height: 100), unboundTimer));
                            Require(rejected.ErrorCode == ModErrorCode.InvalidArgument && rejected.ErrorMessage.Contains("cannot fit") &&
                                unboundTimer.ClockEpoch == null, "The staged graph viewport fails real layout before binding candidate clocks.");
                            Require(events.currentSelectedGameObject == target && target.GetComponent<Selectable>()!.Focused &&
                                events.SelectionChanges == selectionChanges && target.DisableCalls == disabled,
                                "A rejected replacement never disables, deselects, or refocuses the admitted header or port.");
                            Require(parent.Children.Single() == admittedRoot && widget.Go.transform.parent == previousParent &&
                                widget.BindCount == previousBindCount && widget.Go.ParentChanges == previousMoves &&
                                host.Graphs.Count == 1 && widget.Graph == previousBinding && widget.Viewport == previousViewport &&
                                widget.SelectedNodeId == "target" && widget.PendingPort == "send" && widget.FirstPosition == drag,
                                "Rejected sizing leaves the live graph parent, bindings, viewport, selection and drag untouched.");
                        }

                        widget.Graph.SelectionChanged("target");
                        Require(previousCalls == 1 && replacementCalls == 0, "Rejected content preserves the admitted graph callback.");

                        host.FailNextGraphBind = true;
                        var nativeFailure = surface.SetContent(new UiColumn(replacement));
                        Require(nativeFailure.ErrorCode == ModErrorCode.External && nativeFailure.ErrorMessage.Contains("binding error fixture") &&
                            parent.Children.Single() == admittedRoot && widget.Go.transform.parent == previousParent &&
                            widget.Graph == previousBinding && widget.Viewport == previousViewport && widget.SelectedNodeId == "target" &&
                            widget.PendingPort == "send" && widget.FirstPosition == drag && widget.Enabled,
                            "An exceptional transfer restores the previous native graph and runtime binding.");
                        Require(events.currentSelectedGameObject == widget.FirstPort && widget.FirstPort.GetComponent<Selectable>()!.Focused,
                            "Even an exceptional native rebind restores the admitted port focus ring.");

                        var priorDisableCalls = widget.FirstPort.DisableCalls;
                        var priorSelections = events.SelectionChanges;
                        var accepted = new UiColumn(new UiNode[] { replacement, new UiText("Below the graph") },
                            new UiFlexLayout(spacing: UiSpace.None));
                        Require(surface.SetContent(accepted).Succeeded && parent.Children.Single() != admittedRoot &&
                            host.Graphs.Count == 1 && widget.Go.transform.parent != previousParent &&
                            widget.Go.transform.parent!.parent is RectTransform graphRect && graphRect.rect.height == 220f &&
                            widget.Rect.anchorMax == new Vector2(1, 1) && widget.Viewport == replacementViewport,
                            "An admitted replacement transfers the same canvas into the measured destination.");
                        Require(events.currentSelectedGameObject == widget.FirstPort && widget.FirstPort.GetComponent<Selectable>()!.Focused &&
                            widget.FirstPort.DisableCalls == priorDisableCalls && events.SelectionChanges == priorSelections,
                            "Transfer between visible hosts preserves a surviving focused port.");
                        widget.Graph.SelectionChanged("source");
                        Require(previousCalls == 1 && replacementCalls == 1, "Only the committed graph sends its new callbacks.");

                        var moved = widget.Go.ParentChanges;
                        var rebound = widget.BindCount;
                        var bytes = AllocationProbe.Measure("settled retained graph", () =>
                            Require(surface.SetContent(accepted).Succeeded, "The same graph tree stays admitted."), 2048, 64);
                        Require(bytes == 0 && widget.Go.ParentChanges == moved && widget.BindCount == rebound,
                            "A settled graph tree allocates nothing and never moves or rebinds its canvas.");
                        EventSystem.current = null;
                    }

                    private static UiGraphCanvas Graph(float height, string selected, UiGraphViewport viewport, Action<string?> changed) =>
                        new UiGraphCanvas("graph", new[]
                        {
                            new UiGraphNode("source", "event", "Source", new Vec2(-115, 0), new[]
                            {
                                new UiGraphPort("send", "Send", UiGraphPortDirection.Output),
                            }),
                            new UiGraphNode("target", "task", "Target", new Vec2(115, 0), new[]
                            {
                                new UiGraphPort("receive", "Run", UiGraphPortDirection.Input),
                            }),
                        }, Array.Empty<UiGraphEdge>(), changed, selected, viewport, height);
                }
            }
            """;
    }
}
