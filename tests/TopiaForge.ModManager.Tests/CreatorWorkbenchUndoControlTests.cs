using System;
using System.Linq;
using TopiaForge.CreatorTools;
using TopiaForge.CreatorTools.Shared;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The rendered "undo-last" control of the Sandbox workbench: ghost "Undo" in the roster action row, enabled
    /// exactly while the workbench history is non-empty, invoking the existing Undo() with no new semantics.
    /// </summary>
    internal static class CreatorWorkbenchUndoControlTests
    {
        public static void Run()
        {
            UndoControlFollowsHistoryDepth();
            Console.WriteLine("CreatorWorkbenchUndoControlTests passed.");
        }

        private static void UndoControlFollowsHistoryDepth()
        {
            using var context = new FakeModContext();
            context.Scenes.Load("RobotopiaCity");
            context.LocalPlayer.Snapshot = new PlayerSnapshot(Vec3.Zero, new Ray(Vec3.Zero, new Vec3(0f, 0f, 1f)));
            using var content = new FakeCreatorContentService(context.Lifetime);
            var robots = new FakeRobotKit(context.Lifetime);
            // A duplicable prop: the fake spawn handle reads its transform back, which is what Duplicate offsets from.
            var props = new PropFactory();
            var registration = content.Register(new CreatorContentRegistrationRequest(
                "undo-crate", "Undo crate", "Undo control fixture.", CreatorContentKind.Prop, CreatorTransformCapabilities.All, props)).Value!;
            CreatorWorkbench? target = null;
            var workbench = new CreatorWorkbench(
                context,
                new CreatorWorkbenchOptions("undo-control", "UNDO CONTROL TEST", CreatorProjectScope.Sandbox, 16, true, false, 4, 0f),
                content,
                robots.Agents,
                () => { },
                () => (target ?? throw new InvalidOperationException("workbench callback before construction")).EndSession());
            target = workbench;
            Assert(workbench.Open().Succeeded, "workbench opens");
            var window = context.Ui.Surfaces.Single(surface => surface.Id == "undo-control-window");

            var undo = UndoButton(window);
            Assert(undo.Label == "Undo" && undo.Style == UiButtonStyle.Ghost, "undo-last renders as the ghost 'Undo' action");
            Assert(!undo.Enabled, "undo-last is disabled while the history is empty");
            Assert(SiblingIds(window, "undo-last").SequenceEqual(new[] { "duplicate-selected", "remove-selected", "undo-last" }),
                "undo-last sits in the roster action row after duplicate and remove");
            var refused = window.ActivateButton("undo-last");
            Assert(!refused.Succeeded && refused.ErrorCode == ModErrorCode.InvalidState, "the rendered control refuses activation while disabled");
            var direct = workbench.Undo();
            Assert(!direct.Succeeded && direct.ErrorCode == ModErrorCode.NotFound, "the existing Undo() keeps reporting an empty history");

            Assert(window.SelectListItem("catalog-list", "content:" + registration.Descriptor.ContentId).Succeeded
                && window.ActivateButton("spawn-selected").Succeeded && props.ActiveCount == 1,
                "spawning through the rendered catalog action creates one owned prop");
            Assert(UndoButton(window).Enabled, "one recorded operation enables undo-last");
            Assert(window.ActivateButton("duplicate-selected").Succeeded && props.ActiveCount == 2 && window.CallbackErrors.Count == 0,
                "duplicate creates a second owned prop");
            Assert(UndoButton(window).Enabled, "undo-last stays enabled with two recorded operations");

            Assert(window.ActivateButton("undo-last").Succeeded && window.CallbackErrors.Count == 0, "undo-last activates the existing undo");
            Assert(props.ActiveCount == 1, "undoing the duplicate removes exactly the duplicate");
            Assert(UndoButton(window).Enabled, "the original spawn is still undoable");
            Assert(window.ActivateButton("undo-last").Succeeded && props.ActiveCount == 0, "undoing the spawn removes the original prop");
            Assert(!UndoButton(window).Enabled, "undo-last is disabled again once the history is empty");
            Assert(!window.ActivateButton("undo-last").Succeeded && props.ActiveCount == 0, "an empty history cannot be undone through the control");

            Assert(workbench.EndSession().Succeeded && !workbench.IsSessionActive, "session ends cleanly");
            workbench.Dispose();
            registration.Dispose();
            content.Dispose();
            context.Dispose();
            context.AssertNoLeaks();
        }

        private static UiButton UndoButton(FakeUiSurface window)
        {
            Assert(window.TryFindNode("undo-last", out var node) && node is UiButton, "undo-last is rendered in the workbench composition");
            return (UiButton)node!;
        }

        private static string[] SiblingIds(FakeUiSurface window, string id)
        {
            var row = FindRow(window.Content, id);
            Assert(row != null, id + " is inside a row");
            return row!.Children.Select(child => child.Id ?? string.Empty).ToArray();
        }

        private static UiRow? FindRow(UiNode? node, string id)
        {
            switch (node)
            {
                case UiRow row when row.Children.Any(child => string.Equals(child.Id, id, StringComparison.Ordinal)):
                    return row;
                case UiLayoutNode layout:
                    return layout.Children.Select(child => FindRow(child, id)).FirstOrDefault(found => found != null);
                case UiScroll scroll:
                    return FindRow(scroll.Content, id);
                case UiSplitPane split:
                    return FindRow(split.Primary, id) ?? FindRow(split.Secondary, id);
                default:
                    return null;
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Creator workbench undo control: " + message);
        }

        private sealed class PropFactory : ICreatorContentFactory
        {
            private int nextId;
            public int ActiveCount { get; private set; }

            public OperationResult<ICreatorSourceInstance> Spawn(TransformState transform)
            {
                ActiveCount++;
                return OperationResult<ICreatorSourceInstance>.Success(new PropInstance("undo-crate-" + (++nextId), transform, () => ActiveCount--));
            }
        }

        private sealed class PropInstance : ICreatorSourceInstance
        {
            private readonly FakeEntity entity;
            private Action? release;
            private TransformState transform;

            public PropInstance(string id, TransformState transform, Action release)
            {
                this.transform = transform;
                this.release = release;
                entity = new FakeEntity(id, "Undo crate", transform.Position) { Rotation = transform.Rotation, Scale = transform.Scale };
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
                if (!IsAlive) return OperationResult<TransformState>.Failure(ModErrorCode.InvalidState, "The prop is disposed.");
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
    }
}
