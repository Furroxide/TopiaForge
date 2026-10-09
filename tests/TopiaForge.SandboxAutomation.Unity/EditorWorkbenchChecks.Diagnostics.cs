using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TopiaForge.CreatorTools.Shared;
using TopiaForge.ModManager;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.Mods.UnityUi;
using UnityEngine;
using UnityEngine.EventSystems;

namespace TopiaForge.SandboxAutomation.Unity
{
    public static partial class EditorWorkbenchChecks
    {
        private const string LeftPane = "$scroll-0";
        private const string BorrowedRow = "roster-list/robot-native:borrowed-editor";
        private const float ToastWaitSeconds = 2f;

        // Measured-field and reserved-tag oracles of the stage-5 contract (section 1), driven through the same
        // synthetic EventSystem input as the rest of the fixture. Left-pane controls sit below the pane's 680px
        // viewport, so every interaction there first wheels the tagged "$scroll-0" container into place.
        private static IEnumerator ObserveDiagnostics(CreatorWorkbench workbench, FakeCreatorContentService content, OwnerUiService ui)
        {
            Canvas.ForceUpdateCanvases();
            Require(Node("$close").Kind == "button", "close-chrome-tagged");
            AssertUsable("$close");
            for (var index = 0; index < 3; index++)
            {
                var scroll = Node(TopiaForgeUiDiagnosticFormat.ScrollTag(index));
                Require(scroll.Kind == TopiaForgeUiDiagnosticFormat.ScrollKind && scroll.Visible, "scroll-container-tagged:" + index);
            }
            Require(!Exists(TopiaForgeUiDiagnosticFormat.ScrollTag(3)), "scroll-containers-count-in-render-order");
            Require(Node("catalog-kind").Value == "All content", "dropdown-reports-caption-value");
            var hide = Node("hide-workbench");
            Require(hide.Value.Length == 0 && !hide.Selected, "non-value-widgets-report-empty");
            Require(Capture().Widgets.All(widget => TopiaForgeUiDiagnosticFormat.IsColorText(widget.Foreground)
                && TopiaForgeUiDiagnosticFormat.IsColorText(widget.Background)), "colour-text-well-formed");
            Require(IsHex(hide.Foreground), "button-foreground-measured");
            var search = Node("catalog-search");
            Require(IsHex(search.Foreground) && IsHex(search.Background), "input-colours-measured");

            // Discover native targets, then select the borrowed row through the rendered list.
            foreach (var step in Steps(ScrollIntoView("refresh-native"))) yield return null;
            Click("refresh-native");
            for (var frame = 0; frame < 3; frame++) yield return null;
            foreach (var step in Steps(ScrollIntoView(BorrowedRow))) yield return null;
            Require(!Node(BorrowedRow).Selected, "row-unselected-before-click");
            Click(BorrowedRow);
            for (var frame = 0; frame < 3; frame++) yield return null;
            foreach (var step in Steps(ScrollIntoView(BorrowedRow))) yield return null;
            var row = Node(BorrowedRow);
            Require(row.Selected && row.Kind == "list-item", "rendered-row-selected");
            Require(IsHex(row.Foreground) && IsHex(row.Background), "row-colours-measured");
            Require(Node("position-x").Value == "0" && Node("scale-x").Value == "1", "input-reports-text-value");
            Require(!Node("undo-last").Enabled, "undo-last-disabled-without-history");

            // Register a duplicable prop (a fake robot has no readable transform off-game), refresh the rendered
            // catalog, select its row, then spawn through the rendered action: product toast, newly selected row.
            using var prop = content.Register(new CreatorContentRegistrationRequest("editor-crate", "Editor crate",
                "Editor diagnostics fixture.", CreatorContentKind.Prop, CreatorTransformCapabilities.All, new EditorPropFactory())).Value!;
            Click("refresh-content");
            for (var frame = 0; frame < 3; frame++) yield return null;
            var propRow = "catalog-list/content:" + prop.Descriptor.ContentId;
            foreach (var step in Steps(ScrollIntoView(propRow))) yield return null;
            Click(propRow);
            for (var frame = 0; frame < 3; frame++) yield return null;
            foreach (var step in Steps(ScrollIntoView(propRow))) yield return null;
            Require(Node(propRow).Selected, "catalog-row-selected");
            foreach (var step in Steps(ScrollIntoView("spawn-selected"))) yield return null;
            Click("spawn-selected");
            for (var frame = 0; frame < 3; frame++) yield return null;
            Require(content.ActiveSpawnCount == 1, "spawn-creates-owned-prop");
            var rows = RosterRows();
            Require(rows.Count(value => value.Selected) == 1 && rows.Single(value => value.Selected).NodeId.StartsWith("roster-list/owned-content:", StringComparison.Ordinal)
                && !Node(BorrowedRow).Selected, "spawned-row-selected");
            TopiaForgeUiDiagnosticWidget? toast = null;
            for (var deadline = Time.unscaledTime + ToastWaitSeconds; toast == null && Time.unscaledTime < deadline;)
            {
                // The slide-in starts 40px off the right edge, so wait for the resting, unclipped presentation.
                toast = ToastNodes().FirstOrDefault(value => value.Visible && !value.Clipped && value.Style == "Success" && value.Text.Contains("spawned"));
                if (toast == null) yield return null;
            }
            Require(toast != null && toast.Kind == TopiaForgeUiDiagnosticFormat.ToastKind && IsHex(toast.Foreground)
                && toast.NodeId.StartsWith(TopiaForgeUiDiagnosticFormat.ToastNodePrefix, StringComparison.Ordinal), "toast-observed");
            var sequence = long.Parse(toast!.NodeId.Substring(TopiaForgeUiDiagnosticFormat.ToastNodePrefix.Length), CultureInfo.InvariantCulture);
            TopiaForgeToasts.Tick(60f);
            var hidden = false;
            for (var deadline = Time.unscaledTime + ToastWaitSeconds; !hidden && Time.unscaledTime < deadline;)
            {
                var pooled = ToastNodes().SingleOrDefault(value => value.NodeId == toast.NodeId);
                hidden = pooled != null && !pooled.Visible && pooled.Text == toast.Text;
                if (!hidden) yield return null;
            }
            Require(hidden, "pooled-toast-reports-hidden");

            // Duplicate, then undo through the rendered control until the history is empty again.
            foreach (var step in Steps(ScrollIntoView("undo-last"))) yield return null;
            AssertUsable("undo-last");
            foreach (var step in Steps(ScrollIntoView("duplicate-selected"))) yield return null;
            Click("duplicate-selected");
            for (var frame = 0; frame < 3; frame++) yield return null;
            Require(content.ActiveSpawnCount == 2, "duplicate-creates-second-prop");
            // Duplicate reports through the status caption only; a second toast through the owner service proves the
            // process-monotonic sequence and the tone-name style of a re-presented pooled view.
            Require(ui.ShowToast("Editor diagnostics toast probe", UiTone.Warning).Succeeded, "toast-probe-shown");
            TopiaForgeUiDiagnosticWidget? later = null;
            for (var deadline = Time.unscaledTime + ToastWaitSeconds; later == null && Time.unscaledTime < deadline;)
            {
                later = ToastNodes().FirstOrDefault(value => value.Visible && value.Style == "Warning" && ToastSequence(value) > sequence);
                if (later == null) yield return null;
            }
            Require(later != null && later!.Text == "Editor diagnostics toast probe", "toast-sequence-monotonic");
            foreach (var step in Steps(ScrollIntoView("undo-last"))) yield return null;
            Click("undo-last");
            for (var frame = 0; frame < 3; frame++) yield return null;
            Require(content.ActiveSpawnCount == 1, "undo-last-after-duplicate");
            foreach (var step in Steps(ScrollIntoView("undo-last"))) yield return null;
            Click("undo-last");
            for (var frame = 0; frame < 3; frame++) yield return null;
            Require(content.ActiveSpawnCount == 0 && workbench.IsSessionActive, "undo-last-restores-spawn");
            foreach (var step in Steps(ScrollIntoView("undo-last"))) yield return null;
            var undo = Node("undo-last");
            Require(undo.Visible && !undo.Enabled && !undo.Clipped && undo.Kind == "button" && undo.Text == "Undo", "undo-last-disabled-when-history-empty");
        }

        // Wheels the left pane at a measured point that the container's own ScrollRect handles (nested list
        // viewports own their own scrolling) until the target reports clipped: false; mirrors the broker atom.
        private static IEnumerator ScrollIntoView(string nodeId, string containerId = LeftPane)
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                Canvas.ForceUpdateCanvases();
                var target = Node(nodeId);
                if (target.Visible && !target.Clipped) yield break;
                var container = Node(containerId);
                Require(container.Kind == TopiaForgeUiDiagnosticFormat.ScrollKind && container.Visible && !container.Clipped, "scroll-container-usable:" + containerId);
                GameObject? handler = null;
                PointerEventData? data = null;
                foreach (var fraction in new[] { 0.5f, 0.95f, 0.05f, 0.75f, 0.25f })
                {
                    var probe = new PointerEventData(EventSystem.current)
                    {
                        position = new Vector2(container.X + (container.Width * 0.4f), container.Y + (container.Height * fraction)),
                        scrollDelta = new Vector2(0f, -3f)
                    };
                    var hits = new List<RaycastResult>();
                    EventSystem.current.RaycastAll(probe, hits);
                    if (hits.Count == 0) continue;
                    var candidate = ExecuteEvents.GetEventHandler<IScrollHandler>(hits[0].gameObject);
                    if (candidate == null || !TopiaForgeUiDiagnostics.MatchesHit(Owner, Surface, containerId, candidate)) continue;
                    // A nested list viewport also lies inside the container: only the outermost matching handler is the pane.
                    var parent = candidate.transform.parent;
                    var outer = parent == null ? null : ExecuteEvents.GetEventHandler<IScrollHandler>(parent.gameObject);
                    if (outer != null && TopiaForgeUiDiagnostics.MatchesHit(Owner, Surface, containerId, outer)) continue;
                    probe.pointerCurrentRaycast = hits[0];
                    handler = candidate;
                    data = probe;
                    break;
                }
                Require(handler != null && data != null, "scroll-wheel-target:" + containerId);
                ExecuteEvents.Execute(handler, data, ExecuteEvents.scrollHandler);
                yield return null;
            }
            Require(false, "scroll-into-view:" + nodeId);
        }

        private static IEnumerable<object?> Steps(IEnumerator steps)
        {
            while (steps.MoveNext()) yield return steps.Current;
        }

        private static bool Exists(string id, string surface = Surface) =>
            Capture().Widgets.Any(value => value.SurfaceId == surface && value.NodeId == id);

        private static bool IsHex(string value) => value.Length == 7 && TopiaForgeUiDiagnosticFormat.IsColorText(value);

        private static TopiaForgeUiDiagnosticWidget[] RosterRows() => Capture().Widgets
            .Where(value => value.SurfaceId == Surface && value.NodeId.StartsWith("roster-list/", StringComparison.Ordinal)).ToArray();

        private static List<TopiaForgeUiDiagnosticWidget> ToastNodes() => TopiaForgeUiDiagnostics.Capture(TopiaForgeToasts.DiagnosticsOwnerId).Widgets
            .Where(value => value.SurfaceId == TopiaForgeUiDiagnosticFormat.ToastSurface).ToList();

        private static long ToastSequence(TopiaForgeUiDiagnosticWidget toast) =>
            long.TryParse(toast.NodeId.Substring(TopiaForgeUiDiagnosticFormat.ToastNodePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : -1;

        // Deterministic prop content whose spawn handles read their transform back, so Duplicate has an offset source.
        private sealed class EditorPropFactory : ICreatorContentFactory
        {
            private int nextId;

            public OperationResult<ICreatorSourceInstance> Spawn(TransformState transform) =>
                OperationResult<ICreatorSourceInstance>.Success(new EditorPropInstance("editor-crate-" + (++nextId), transform));
        }

        private sealed class EditorPropInstance : ICreatorSourceInstance
        {
            private readonly FakeEntity entity;
            private TransformState transform;
            private bool disposed;

            public EditorPropInstance(string id, TransformState transform)
            {
                this.transform = transform;
                entity = new FakeEntity(id, "Editor crate", transform.Position) { Rotation = transform.Rotation, Scale = transform.Scale };
            }

            public IEntity Entity => entity;
            public bool IsAlive => !disposed && entity.IsAlive;

            public bool TryGetTransform(out TransformState value)
            {
                value = transform;
                return IsAlive;
            }

            public OperationResult<TransformState> SetTransform(TransformState value)
            {
                if (!IsAlive) return OperationResult<TransformState>.Failure(ModErrorCode.InvalidState, "The prop is disposed.");
                transform = value;
                entity.Position = value.Position;
                entity.Rotation = value.Rotation;
                entity.Scale = value.Scale;
                return OperationResult<TransformState>.Success(value);
            }

            public void Dispose()
            {
                disposed = true;
                entity.Destroy();
            }
        }
    }
}
