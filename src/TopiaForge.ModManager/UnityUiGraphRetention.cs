using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService
    {
        private sealed class UiGraphRetentionTransaction
        {
            private readonly KeyedRetentionMap<TopiaForgeGraphCanvas> retention;
            private readonly List<RetainedGraphMove> moves = new List<RetainedGraphMove>();
            private int startedMoves;
            private bool completed;

            public UiGraphRetentionTransaction(
                IReadOnlyDictionary<string, TopiaForgeGraphCanvas> previous)
            {
                retention = new KeyedRetentionMap<TopiaForgeGraphCanvas>(previous);
            }

            public void Render(
                UiGraphCanvas graph,
                TopiaForgeContainer parent,
                UiCallbackGate callbacks,
                bool inheritedDisabled)
            {
                if (graph == null) throw new ArgumentNullException(nameof(graph));
                if (parent == null) throw new ArgumentNullException(nameof(parent));
                if (callbacks == null) throw new ArgumentNullException(nameof(callbacks));

                var id = graph.Id!;
                var gated = GateGraphCallbacks(graph, callbacks, inheritedDisabled);
                if (!retention.TryClaimPrevious(id, out var retained))
                {
                    var created = parent.GraphCanvas(gated);
                    TopiaForgeAnchors.Stretch(created.Rect);
                    retention.SetClaimed(id, created);
                    return;
                }

                if (retained == null || retained.Go == null)
                {
                    throw new InvalidOperationException(
                        "Retained graph canvas '" + id + "' is no longer available.");
                }

                if (retained.Go.transform.parent == null)
                {
                    throw new InvalidOperationException(
                        "Retained graph canvas '" + id + "' has no parent.");
                }

                moves.Add(new RetainedGraphMove(retained, parent, gated));
                retention.SetClaimed(id, retained);
            }

            public void Transfer(TopiaForgeContainer stage)
            {
                if (moves.Count == 0) return;
                foreach (var move in moves) move.Capture();
                // Layout has accepted the destination. Moving between visible hosts keeps an
                // existing control active while its composition wrapper is replaced.
                stage.SetShown(true);
                foreach (var move in moves)
                {
                    startedMoves++;
                    move.Apply();
                }
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
                for (var index = startedMoves - 1; index >= 0; index--)
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

                    try
                    {
                        move.RestoreFocus();
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
                UiCallbackGate callbacks,
                bool inheritedDisabled)
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
                    graph.Enabled && !inheritedDisabled,
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
                private readonly TopiaForgeContainer destination;
                private readonly UiGraphCanvas replacement;
                private Transform previousParent = null!;
                private int previousSiblingIndex;
                private TopiaForgeGraphCanvasBindingState previousBinding;
                private Vector2 anchorMin;
                private Vector2 anchorMax;
                private Vector2 pivot;
                private Vector2 offsetMin;
                private Vector2 offsetMax;
                private EventSystem? previousEventSystem;
                private GameObject? previousFocus;

                public RetainedGraphMove(TopiaForgeGraphCanvas graph, TopiaForgeContainer destination, UiGraphCanvas replacement)
                {
                    this.graph = graph;
                    this.destination = destination;
                    this.replacement = replacement;
                }

                public void Capture()
                {
                    var transform = graph.Go.transform;
                    var parent = transform.parent;
                    if (parent == null) throw new InvalidOperationException("The retained graph canvas lost its parent.");
                    previousParent = parent;
                    previousSiblingIndex = transform.GetSiblingIndex();
                    previousBinding = graph.CaptureBindingState();
                    anchorMin = graph.Rect.anchorMin;
                    anchorMax = graph.Rect.anchorMax;
                    pivot = graph.Rect.pivot;
                    offsetMin = graph.Rect.offsetMin;
                    offsetMax = graph.Rect.offsetMax;
                    var events = EventSystem.current;
                    var selected = events != null ? events.currentSelectedGameObject : null;
                    if (selected != null && selected.transform.IsChildOf(transform))
                    {
                        previousEventSystem = events;
                        previousFocus = selected;
                    }
                }

                public void Apply()
                {
                    graph.Go.transform.SetParent(destination.Go.transform, false);
                    TopiaForgeAnchors.Stretch(graph.Rect);
                    graph.SetGraph(replacement);
                }

                public void RestoreParent()
                {
                    graph.Go.transform.SetParent(previousParent, false);
                    graph.Go.transform.SetSiblingIndex(previousSiblingIndex);
                    graph.Rect.anchorMin = anchorMin;
                    graph.Rect.anchorMax = anchorMax;
                    graph.Rect.pivot = pivot;
                    graph.Rect.offsetMin = offsetMin;
                    graph.Rect.offsetMax = offsetMax;
                }

                public void DetachTo(Transform fallbackParent)
                {
                    graph.Go.transform.SetParent(fallbackParent, false);
                }

                public void RestoreBinding()
                {
                    graph.RestoreBindingState(previousBinding);
                }

                public void RestoreFocus()
                {
                    var target = previousFocus;
                    var events = previousEventSystem;
                    if (target == null || events == null || !ReferenceEquals(events, EventSystem.current) ||
                        !target.activeInHierarchy || !target.TryGetComponent<Selectable>(out var control) || !control.IsInteractable()) return;
                    // A native disable can hide the ring while EventSystem still points to the same control.
                    if (events.currentSelectedGameObject == target) events.SetSelectedGameObject(null);
                    events.SetSelectedGameObject(target);
                }
            }
        }
    }
}
