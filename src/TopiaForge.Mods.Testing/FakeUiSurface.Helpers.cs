using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    public sealed partial class FakeUiSurface
    {
        private void CaptureState(UiNode? node)
        {
            if (node == null) return;
            if (node is UiToggle toggle) toggleValues[toggle.Id!] = toggle.Value;
            else if (node is UiSlider slider) sliderValues[slider.Id!] = slider.Value;
            else if (node is UiTextInput input) textValues[input.Id!] = input.Value;
            else if (node is UiDropdown dropdown) dropdownValues[dropdown.Id!] = dropdown.SelectedValue;
            else if (node is UiVirtualList list) listSelections[list.Id!] = list.SelectedItemId;
            else if (node is UiGraphCanvas graph)
            {
                graphSelections[graph.Id!] = graph.SelectedNodeId;
                graphViewports[graph.Id!] = graph.Viewport;
            }

            if (node is UiLayoutNode layout)
            {
                foreach (var child in layout.Children) CaptureState(child);
            }
            else if (node is UiScroll scroll)
            {
                CaptureState(scroll.Content);
            }
            else if (node is UiSplitPane split)
            {
                CaptureState(split.Primary);
                CaptureState(split.Secondary);
            }
        }

        private static bool TryFind(UiNode? current, string id, out UiNode? node)
        {
            node = null;
            if (current == null || string.IsNullOrWhiteSpace(id)) return false;
            if (string.Equals(current.Id, id, StringComparison.Ordinal))
            {
                node = current;
                return true;
            }

            if (current is UiLayoutNode layout)
            {
                foreach (var child in layout.Children)
                {
                    if (TryFind(child, id, out node)) return true;
                }
            }
            else if (current is UiScroll scroll && TryFind(scroll.Content, id, out node))
            {
                return true;
            }
            else if (current is UiSplitPane split &&
                     (TryFind(split.Primary, id, out node) || TryFind(split.Secondary, id, out node)))
            {
                return true;
            }

            return false;
        }

        private OperationResult<bool> Invoke(Action callback, string description)
        {
            var failures = 0;
            foreach (var subscriber in callback.GetInvocationList())
            {
                try { ((Action)subscriber)(); }
                catch (Exception exception)
                {
                    failures++;
                    callbackErrors.Add(description + " callback failed: " + exception.Message);
                }
            }

            return CallbackResult(failures);
        }

        private OperationResult<bool> Invoke<T>(Action<T> callback, T value, string description)
        {
            var failures = 0;
            foreach (var subscriber in callback.GetInvocationList())
            {
                try { ((Action<T>)subscriber)(value); }
                catch (Exception exception)
                {
                    failures++;
                    callbackErrors.Add(description + " callback failed: " + exception.Message);
                }
            }

            return CallbackResult(failures);
        }

        private static OperationResult<bool> CallbackResult(int failures) => failures == 0
            ? OperationResult<bool>.Success(true)
            : OperationResult<bool>.Failure(ModErrorCode.External, failures + " fake UI callback subscriber(s) failed.");

        private static OperationResult<bool> NotFound(string id, string kind) =>
            OperationResult<bool>.Failure(ModErrorCode.NotFound, "No " + kind + " uses id '" + id + "'.");

        private static OperationResult<bool> Disabled(string id) =>
            OperationResult<bool>.Failure(ModErrorCode.InvalidState, "UI control '" + id + "' is disabled.");

        private static UiGraphNode? FindGraphNode(UiGraphCanvas graph, string id)
        {
            foreach (var node in graph.Nodes)
            {
                if (string.Equals(node.Id, id, StringComparison.Ordinal)) return node;
            }

            return null;
        }

        private static UiGraphPort? FindGraphPort(UiGraphNode node, string id)
        {
            foreach (var port in node.Ports)
            {
                if (string.Equals(port.Id, id, StringComparison.Ordinal)) return port;
            }

            return null;
        }

        private void InvokeDismissed()
        {
            var handlers = Dismissed;
            if (handlers == null) return;
            Invoke(handlers, "surface '" + Id + "' dismissal");
        }

        private void EnsureActive()
        {
            if (release == null)
            {
                throw new ObjectDisposedException(nameof(FakeUiSurface));
            }
        }
    }
}
