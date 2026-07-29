using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Inspectable fake UI surface.</summary>
    public sealed class FakeUiSurface : IUiSurface, IUiSurfaceDismissalSource
    {
        private Action<FakeUiSurface>? release;
        private IDisposable? lifetimeLease;
        private readonly Dictionary<string, bool> toggleValues = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> sliderValues = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> textValues = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> dropdownValues = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> listSelections = new Dictionary<string, string?>(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> graphSelections = new Dictionary<string, string?>(StringComparer.Ordinal);
        private readonly Dictionary<string, UiGraphViewport> graphViewports = new Dictionary<string, UiGraphViewport>(StringComparer.Ordinal);
        private readonly List<string> callbackErrors = new List<string>();
        private ModErrorCode nextContentErrorCode;
        private string nextContentErrorMessage = string.Empty;

        internal FakeUiSurface(UiSurfaceRequest request, Action<FakeUiSurface> release)
        {
            Request = request;
            Body = request.Body;
            Content = request.Content;
            CaptureState(Content);
            this.release = release;
            IsVisible = true;
        }

        internal void AttachLifetimeLease(IDisposable lease)
        {
            lifetimeLease = lease ?? throw new ArgumentNullException(nameof(lease));
        }

        internal void FailNextContentUpdate(ModErrorCode errorCode, string message)
        {
            nextContentErrorCode = errorCode;
            nextContentErrorMessage = message ?? string.Empty;
        }

        /// <summary>Gets the captured surface request.</summary>
        public UiSurfaceRequest Request { get; }

        /// <inheritdoc/>
        public string Id => Request.Id;

        /// <inheritdoc/>
        public bool IsVisible { get; private set; }

        /// <summary>Gets the current body text.</summary>
        public string Body { get; private set; }

        /// <summary>Gets the currently captured immutable composition tree.</summary>
        public UiNode? Content { get; private set; }

        /// <summary>Gets callback failures captured without interrupting later callback subscribers.</summary>
        public IReadOnlyList<string> CallbackErrors => callbackErrors.AsReadOnly();

        /// <inheritdoc/>
        public event Action? Dismissed;

        /// <inheritdoc/>
        public void Show()
        {
            EnsureActive();
            IsVisible = true;
        }

        /// <inheritdoc/>
        public void Hide()
        {
            EnsureActive();
            if (!IsVisible) return;
            IsVisible = false;
            InvokeDismissed();
        }

        /// <inheritdoc/>
        public void SetBody(string body)
        {
            EnsureActive();
            Body = body ?? string.Empty;
        }

        /// <inheritdoc/>
        public OperationResult<bool> SetContent(UiNode content)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            if (release == null)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The fake UI surface is disposed.");
            }

            if (nextContentErrorCode != ModErrorCode.None)
            {
                var errorCode = nextContentErrorCode;
                var message = nextContentErrorMessage;
                nextContentErrorCode = ModErrorCode.None;
                nextContentErrorMessage = string.Empty;
                return OperationResult<bool>.Failure(errorCode, message);
            }

            try
            {
                UiComposition.Validate(content);
            }
            catch (ArgumentException exception)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, exception.Message);
            }

            Content = content;
            toggleValues.Clear();
            sliderValues.Clear();
            textValues.Clear();
            dropdownValues.Clear();
            listSelections.Clear();
            graphSelections.Clear();
            graphViewports.Clear();
            CaptureState(content);
            return OperationResult<bool>.Success(true);
        }

        /// <summary>Finds a captured node by stable control id.</summary>
        public bool TryFindNode(string id, out UiNode? node)
        {
            node = null;
            return release != null && TryFind(Content, id, out node);
        }

        /// <summary>Invokes one enabled button by id while isolating all callback subscribers.</summary>
        public OperationResult<bool> ActivateButton(string id)
        {
            if (!TryFindNode(id, out var node) || !(node is UiButton button)) return NotFound(id, "button");
            if (!button.Enabled) return Disabled(id);
            return Invoke(button.Activated, "button '" + id + "'");
        }

        /// <summary>Changes one enabled toggle and invokes its callback.</summary>
        public OperationResult<bool> ChangeToggle(string id, bool value)
        {
            if (!TryFindNode(id, out var node) || !(node is UiToggle toggle)) return NotFound(id, "toggle");
            if (!toggle.Enabled) return Disabled(id);
            toggleValues[id] = value;
            return Invoke(toggle.Changed, value, "toggle '" + id + "'");
        }

        /// <summary>Changes one enabled slider to an in-range finite value and invokes its callback.</summary>
        public OperationResult<bool> ChangeSlider(string id, float value)
        {
            if (!TryFindNode(id, out var node) || !(node is UiSlider slider)) return NotFound(id, "slider");
            if (!slider.Enabled) return Disabled(id);
            if (float.IsNaN(value) || float.IsInfinity(value) || value < slider.Minimum || value > slider.Maximum)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "The fake slider value is outside its range.");
            }

            sliderValues[id] = value;
            return Invoke(slider.Changed, value, "slider '" + id + "'");
        }

        /// <summary>Changes one enabled text input, applying its maximum length before callback delivery.</summary>
        public OperationResult<bool> ChangeText(string id, string value)
        {
            if (!TryFindNode(id, out var node) || !(node is UiTextInput input)) return NotFound(id, "text input");
            if (!input.Enabled) return Disabled(id);
            var bounded = UiTextInput.Truncate(value ?? string.Empty, input.MaximumLength);
            textValues[id] = bounded;
            return Invoke(input.Changed, bounded, "text input '" + id + "'");
        }

        /// <summary>Selects one enabled dropdown choice by stable value and invokes its callback.</summary>
        public OperationResult<bool> ChangeDropdown(string id, string value)
        {
            if (!TryFindNode(id, out var node) || !(node is UiDropdown dropdown)) return NotFound(id, "dropdown");
            if (!dropdown.Enabled) return Disabled(id);
            var found = false;
            foreach (var choice in dropdown.Choices)
            {
                if (string.Equals(choice.Value, value, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }

            if (!found) return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "The fake dropdown value is not a choice.");
            dropdownValues[id] = value;
            return Invoke(dropdown.Changed, value, "dropdown '" + id + "'");
        }

        /// <summary>Selects one enabled virtual-list item by stable id and invokes its callback.</summary>
        public OperationResult<bool> SelectListItem(string id, string itemId)
        {
            if (!TryFindNode(id, out var node) || !(node is UiVirtualList list)) return NotFound(id, "virtual list");
            if (!list.Enabled) return Disabled(id);
            var found = false;
            foreach (var item in list.Items)
            {
                if (string.Equals(item.Id, itemId, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }

            if (!found) return OperationResult<bool>.Failure(ModErrorCode.NotFound, "The fake list item was not found.");
            listSelections[id] = itemId;
            return Invoke(list.Selected, itemId, "virtual list '" + id + "'");
        }

        /// <summary>Selects or clears selection on one enabled graph canvas.</summary>
        public OperationResult<bool> SelectGraphNode(string id, string? nodeId)
        {
            if (!TryFindNode(id, out var node) || !(node is UiGraphCanvas graph)) return NotFound(id, "graph canvas");
            if (!graph.Enabled) return Disabled(id);
            if (nodeId != null && FindGraphNode(graph, nodeId) == null)
            {
                return OperationResult<bool>.Failure(ModErrorCode.NotFound, "The fake graph node was not found.");
            }

            graphSelections[id] = nodeId;
            return Invoke(graph.SelectionChanged, nodeId, "graph canvas '" + id + "' selection");
        }

        /// <summary>Moves one enabled graph node and invokes the optional edit callback.</summary>
        public OperationResult<bool> MoveGraphNode(string id, string nodeId, Vec2 position)
        {
            if (!TryFindNode(id, out var node) || !(node is UiGraphCanvas graph)) return NotFound(id, "graph canvas");
            if (!graph.Enabled) return Disabled(id);
            var graphNode = FindGraphNode(graph, nodeId);
            if (graphNode == null) return OperationResult<bool>.Failure(ModErrorCode.NotFound, "The fake graph node was not found.");
            if (!graphNode.Enabled) return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The fake graph node is disabled.");
            if (graph.NodeMoved == null)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The graph does not accept node moves.");
            }

            UiGraphNodeMove move;
            try { move = new UiGraphNodeMove(nodeId, position); }
            catch (ArgumentException exception)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, exception.Message);
            }

            return Invoke(graph.NodeMoved, move, "graph canvas '" + id + "' node move");
        }

        /// <summary>Requests one compatible connection on an enabled graph canvas.</summary>
        public OperationResult<bool> ConnectGraphPorts(
            string id,
            string sourceNodeId,
            string sourcePortId,
            string targetNodeId,
            string targetPortId)
        {
            if (!TryFindNode(id, out var node) || !(node is UiGraphCanvas graph)) return NotFound(id, "graph canvas");
            if (!graph.Enabled) return Disabled(id);
            if (graph.ConnectionRequested == null)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The graph does not accept new connections.");
            }

            var source = FindGraphNode(graph, sourceNodeId);
            var target = FindGraphNode(graph, targetNodeId);
            var sourcePort = source == null ? null : FindGraphPort(source, sourcePortId);
            var targetPort = target == null ? null : FindGraphPort(target, targetPortId);
            if (source == null || target == null || sourcePort == null || targetPort == null)
            {
                return OperationResult<bool>.Failure(ModErrorCode.NotFound, "A fake graph connection endpoint was not found.");
            }

            if (!source.Enabled || !target.Enabled ||
                sourcePort.Direction != UiGraphPortDirection.Output ||
                targetPort.Direction != UiGraphPortDirection.Input ||
                !string.Equals(sourcePort.DataType, targetPort.DataType, StringComparison.Ordinal))
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "The fake graph ports are not compatible.");
            }

            var request = new UiGraphConnectionRequest(sourceNodeId, sourcePortId, targetNodeId, targetPortId);
            return Invoke(graph.ConnectionRequested, request, "graph canvas '" + id + "' connection");
        }

        /// <summary>Requests removal of an existing edge on an enabled graph canvas.</summary>
        public OperationResult<bool> RemoveGraphConnection(string id, string edgeId)
        {
            if (!TryFindNode(id, out var node) || !(node is UiGraphCanvas graph)) return NotFound(id, "graph canvas");
            if (!graph.Enabled) return Disabled(id);
            if (graph.ConnectionRemoved == null)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The graph does not accept connection removal.");
            }

            var found = false;
            foreach (var edge in graph.Edges)
            {
                if (string.Equals(edge.Id, edgeId, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }

            if (!found) return OperationResult<bool>.Failure(ModErrorCode.NotFound, "The fake graph edge was not found.");
            return Invoke(graph.ConnectionRemoved, edgeId, "graph canvas '" + id + "' connection removal");
        }

        /// <summary>Changes the bounded viewport on an enabled graph canvas.</summary>
        public OperationResult<bool> ChangeGraphViewport(string id, UiGraphViewport viewport)
        {
            if (!TryFindNode(id, out var node) || !(node is UiGraphCanvas graph)) return NotFound(id, "graph canvas");
            if (!graph.Enabled) return Disabled(id);
            if (graph.ViewportChanged == null)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The graph does not accept viewport changes.");
            }

            graphViewports[id] = viewport;
            return Invoke(graph.ViewportChanged, viewport, "graph canvas '" + id + "' viewport");
        }

        /// <summary>Tries to read the fake's current toggle value.</summary>
        public bool TryGetToggleValue(string id, out bool value) => toggleValues.TryGetValue(id, out value);

        /// <summary>Tries to read the fake's current slider value.</summary>
        public bool TryGetSliderValue(string id, out float value) => sliderValues.TryGetValue(id, out value);

        /// <summary>Tries to read the fake's current text-input value.</summary>
        public bool TryGetTextValue(string id, out string? value) => textValues.TryGetValue(id, out value);

        /// <summary>Tries to read the fake's current dropdown value.</summary>
        public bool TryGetDropdownValue(string id, out string? value) => dropdownValues.TryGetValue(id, out value);

        /// <summary>Tries to read the fake's current virtual-list selection.</summary>
        public bool TryGetSelectedListItem(string id, out string? itemId) => listSelections.TryGetValue(id, out itemId);

        /// <summary>Tries to read the fake's current graph selection.</summary>
        public bool TryGetSelectedGraphNode(string id, out string? nodeId) => graphSelections.TryGetValue(id, out nodeId);

        /// <summary>Tries to read the fake's current graph viewport.</summary>
        public bool TryGetGraphViewport(string id, out UiGraphViewport viewport) => graphViewports.TryGetValue(id, out viewport);

        /// <inheritdoc/>
        public void Dispose()
        {
            IsVisible = false;
            Dismissed = null;
            var callback = release;
            release = null;
            callback?.Invoke(this);
            System.Threading.Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
        }

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
