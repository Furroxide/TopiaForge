using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService
    {
        private sealed class UiGraphRetentionTransaction
        {
            private readonly KeyedRetentionMap<TopiaForgeGraphCanvas> retention;
            private readonly List<RetainedGraphMove> moves = new List<RetainedGraphMove>();
            private bool completed;

            public UiGraphRetentionTransaction(
                IReadOnlyDictionary<string, TopiaForgeGraphCanvas> previous)
            {
                retention = new KeyedRetentionMap<TopiaForgeGraphCanvas>(previous);
            }

            public IReadOnlyDictionary<string, TopiaForgeGraphCanvas> Next => retention.Next;

            public void Render(
                UiGraphCanvas graph,
                TopiaForgeContainer parent,
                UiCallbackGate callbacks)
            {
                if (graph == null) throw new ArgumentNullException(nameof(graph));
                if (parent == null) throw new ArgumentNullException(nameof(parent));
                if (callbacks == null) throw new ArgumentNullException(nameof(callbacks));

                var id = graph.Id!;
                var gated = GateGraphCallbacks(graph, callbacks);
                if (!retention.TryClaimPrevious(id, out var retained))
                {
                    var created = parent.GraphCanvas(gated);
                    retention.SetClaimed(id, created);
                    return;
                }

                if (retained == null || retained.Go == null)
                {
                    throw new InvalidOperationException(
                        "Retained graph canvas '" + id + "' is no longer available.");
                }

                var transform = retained.Go.transform;
                var previousParent = transform.parent;
                if (previousParent == null)
                {
                    throw new InvalidOperationException(
                        "Retained graph canvas '" + id + "' has no parent.");
                }

                var move = new RetainedGraphMove(
                    retained,
                    previousParent,
                    transform.GetSiblingIndex(),
                    retained.CaptureBindingState());
                moves.Add(move);
                transform.SetParent(parent.Go.transform, false);
                retained.SetGraph(gated);
                retention.SetClaimed(id, retained);
            }

            public IReadOnlyDictionary<string, TopiaForgeGraphCanvas> Commit()
            {
                completed = true;
                return retention.Next;
            }

            public Exception? Rollback(TopiaForgeContainer fallbackParent)
            {
                if (completed) return null;
                Exception? firstFailure = null;
                for (var index = moves.Count - 1; index >= 0; index--)
                {
                    var move = moves[index];
                    try
                    {
                        move.RestoreParent();
                    }
                    catch (Exception exception)
                    {
                        firstFailure ??= exception;
                        try
                        {
                            move.DetachTo(fallbackParent.Go.transform);
                        }
                        catch (Exception fallbackException)
                        {
                            firstFailure ??= fallbackException;
                        }
                    }

                    try
                    {
                        move.RestoreBinding();
                    }
                    catch (Exception exception)
                    {
                        firstFailure ??= exception;
                    }
                }

                return firstFailure;
            }

            private static UiGraphCanvas GateGraphCallbacks(
                UiGraphCanvas graph,
                UiCallbackGate callbacks)
            {
                var id = graph.Id!;
                return new UiGraphCanvas(
                    id,
                    graph.Nodes,
                    graph.Edges,
                    callbacks.Wrap(graph.SelectionChanged, "graph canvas '" + id + "' selection"),
                    graph.SelectedNodeId,
                    graph.Viewport,
                    graph.Height,
                    graph.Enabled,
                    graph.NodeMoved == null
                        ? null
                        : callbacks.Wrap(graph.NodeMoved, "graph canvas '" + id + "' node move"),
                    graph.ConnectionRequested == null
                        ? null
                        : callbacks.Wrap(graph.ConnectionRequested, "graph canvas '" + id + "' connection"),
                    graph.ConnectionRemoved == null
                        ? null
                        : callbacks.Wrap(graph.ConnectionRemoved, "graph canvas '" + id + "' connection removal"),
                    graph.ViewportChanged == null
                        ? null
                        : callbacks.Wrap(graph.ViewportChanged, "graph canvas '" + id + "' viewport"));
            }

            private sealed class RetainedGraphMove
            {
                private readonly TopiaForgeGraphCanvas graph;
                private readonly Transform previousParent;
                private readonly int previousSiblingIndex;
                private readonly TopiaForgeGraphCanvasBindingState previousBinding;

                public RetainedGraphMove(
                    TopiaForgeGraphCanvas graph,
                    Transform previousParent,
                    int previousSiblingIndex,
                    TopiaForgeGraphCanvasBindingState previousBinding)
                {
                    this.graph = graph;
                    this.previousParent = previousParent;
                    this.previousSiblingIndex = previousSiblingIndex;
                    this.previousBinding = previousBinding;
                }

                public void RestoreParent()
                {
                    graph.Go.transform.SetParent(previousParent, false);
                    graph.Go.transform.SetSiblingIndex(previousSiblingIndex);
                }

                public void DetachTo(Transform fallbackParent)
                {
                    graph.Go.transform.SetParent(fallbackParent, false);
                }

                public void RestoreBinding()
                {
                    graph.RestoreBindingState(previousBinding);
                }
            }
        }
    }
}
