using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Inspectable fake UI surface.</summary>
    public sealed partial class FakeUiSurface : IUiSurface, IUiSurfaceDismissalSource
    {
        private readonly FakeUiService owner;
        private readonly FakeUiWireTarget? wireTarget;
        private Action<FakeUiSurface>? release;
        private IDisposable? lifetimeLease;
        private readonly Dictionary<string, bool> toggleValues = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> sliderValues = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> textValues = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> dropdownValues = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, (string? Selection, float Offset)> listViewports =
            new Dictionary<string, (string? Selection, float Offset)>(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> graphSelections = new Dictionary<string, string?>(StringComparer.Ordinal);
        private readonly Dictionary<string, UiGraphViewport> graphViewports = new Dictionary<string, UiGraphViewport>(StringComparer.Ordinal);
        private readonly List<string> callbackErrors = new List<string>();
        private readonly IReadOnlyList<string> callbackErrorView;
        private readonly Func<bool>? gameplay;
        private ModErrorCode nextContentErrorCode;
        private string nextContentErrorMessage = string.Empty;
        private bool shown = true;
        private ulong revision = 1;

        internal FakeUiSurface(UiSurfaceRequest request, FakeUiService owner, Action<FakeUiSurface> release,
            Func<bool>? gameplay, FakeUiWireTarget? wireTarget)
        {
            this.owner = owner;
            this.wireTarget = wireTarget;
            callbackErrorView = callbackErrors.AsReadOnly();
            Request = request;
            if (request.Kind == UiSurfaceKind.Hud)
                HudOverflowText = UiHudOverflowNotice.Text(owner.HudPackageDisplayName, request.HudAnchor, request.HudVisibility);
            Body = request.Body;
            Content = request.Content;
            CaptureState(Content);
            this.release = release;
            this.gameplay = gameplay;
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

        /// <summary>
        /// Gets whether the surface shows, as in the game: the mod shows it, and a gameplay HUD only while the fake scenes
        /// are in gameplay (a loaded gameplay scene is active and no load is pending). At the fake's initial main-menu
        /// scene a gameplay HUD is hidden until a gameplay scene loads.
        /// </summary>
        public bool IsVisible => IsShown && (!FollowsGameplay || gameplay == null || gameplay());

        /// <summary>Gets whether the mod shows the surface, whether or not TopiaForge keeps a gameplay HUD hidden now.</summary>
        public bool IsShown => shown && release != null;

        private bool FollowsGameplay =>
            Request.Kind == UiSurfaceKind.Hud && Request.HudVisibility == UiHudVisibility.Gameplay;

        /// <summary>Gets the current body text.</summary>
        public string Body { get; private set; }

        /// <summary>Gets the currently captured immutable composition tree.</summary>
        public UiNode? Content { get; private set; }

        /// <summary>Gets callback failures captured without interrupting later callback subscribers.</summary>
        public IReadOnlyList<string> CallbackErrors => callbackErrorView;

        /// <inheritdoc/>
        public event Action? Dismissed;

        /// <inheritdoc/>
        public void Show()
        {
            EnsureActive();
            if (shown) return;
            shown = true;
            if (HudOverflowText != null && IsVisible) owner.RefreshHudLayoutNotices();
        }

        /// <inheritdoc/>
        public void Hide()
        {
            EnsureActive();
            var wasVisible = IsVisible;
            shown = false;
            CloseGraphConnections();
            if (wasVisible && HudOverflowText != null) owner.RefreshHudLayoutNotices();
            if (wasVisible) InvokeDismissed();
        }

        /// <summary>
        /// Simulates the player closing this surface. A remote player's dismissal carries guest origin;
        /// <see cref="Hide"/> remains a server-initiated operation.
        /// </summary>
        [RequiresTopiaForge("0.2.0")]
        public void DismissFromPlayer() => FakeGuestOrigin.Run(WireBounded, Hide);

        /// <inheritdoc/>
        public void SetBody(string body)
        {
            EnsureActive();
            if (WireBounded && (body == null || body.Length > 4096))
                throw new ArgumentException("Remote UI text is limited to 4096 characters.", nameof(body));
            var next = body ?? string.Empty;
            if (string.Equals(Body, next, StringComparison.Ordinal)) return;
            Body = next;
            if (HudOverflowText != null && IsVisible) owner.RefreshHudLayoutNotices();
        }

        /// <summary>Finds a captured node by stable control id.</summary>
        public bool TryFindNode(string id, out UiNode? node)
        {
            node = null;
            return release != null && UiLayoutTree.TryFind(Content, id, out node, out _);
        }

        /// <summary>Invokes one enabled button by id while isolating all callback subscribers.</summary>
        public OperationResult<bool> ActivateButton(string id)
        {
            if (!TryFindNode(id, out var node) || !(node is UiButton button)) return NotFound(id, "button");
            if (!CanInteract(id)) return Disabled(id);
            return Invoke(button.Activated, "button '" + id + "'");
        }

        /// <summary>Changes one enabled toggle and invokes its callback.</summary>
        public OperationResult<bool> ChangeToggle(string id, bool value)
        {
            if (!TryFindNode(id, out var node) || !(node is UiToggle toggle)) return NotFound(id, "toggle");
            if (!CanInteract(id)) return Disabled(id);
            toggleValues[id] = value;
            return Invoke(toggle.Changed, value, "toggle '" + id + "'");
        }

        /// <summary>Changes one enabled slider to an in-range finite value and invokes its callback.</summary>
        public OperationResult<bool> ChangeSlider(string id, float value)
        {
            if (!TryFindNode(id, out var node) || !(node is UiSlider slider)) return NotFound(id, "slider");
            if (!CanInteract(id)) return Disabled(id);
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
            if (!CanInteract(id)) return Disabled(id);
            var bounded = UiTextInput.Truncate(value ?? string.Empty, input.MaximumLength);
            textValues[id] = bounded;
            return Invoke(input.Changed, bounded, "text input '" + id + "'");
        }

        /// <summary>Selects one enabled dropdown choice by stable value and invokes its callback.</summary>
        public OperationResult<bool> ChangeDropdown(string id, string value)
        {
            if (!TryFindNode(id, out var node) || !(node is UiDropdown dropdown)) return NotFound(id, "dropdown");
            if (!CanInteract(id)) return Disabled(id);
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
            if (!CanInteract(id)) return Disabled(id);
            var index = ListIndex(list, itemId);
            if (index < 0) return OperationResult<bool>.Failure(ModErrorCode.NotFound, "The fake list item was not found.");
            RememberListViewport(list, itemId, index);
            return Invoke(list.Selected, itemId, "virtual list '" + id + "'");
        }

        /// <summary>Selects or clears selection on one enabled graph canvas.</summary>
        public OperationResult<bool> SelectGraphNode(string id, string? nodeId)
        {
            if (!TryFindNode(id, out var node) || !(node is UiGraphCanvas graph)) return NotFound(id, "graph canvas");
            if (!CanInteract(id)) return Disabled(id);
            if (nodeId != null && FindGraphNode(graph, nodeId) == null)
            {
                return OperationResult<bool>.Failure(ModErrorCode.NotFound, "The fake graph node was not found.");
            }

            focusedConnectionGraphId = id;
            graphSelections[id] = nodeId;
            return Invoke(graph.SelectionChanged, nodeId, "graph canvas '" + id + "' selection");
        }

        /// <summary>Moves one enabled graph node and invokes the optional edit callback.</summary>
        public OperationResult<bool> MoveGraphNode(string id, string nodeId, Vec2 position)
        {
            if (!TryFindNode(id, out var node) || !(node is UiGraphCanvas graph)) return NotFound(id, "graph canvas");
            if (!CanInteract(id)) return Disabled(id);
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
            if (!CanInteract(id)) return Disabled(id);
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
            if (!CanInteract(id)) return Disabled(id);
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
            if (!CanInteract(id)) return Disabled(id);
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
        public bool TryGetSelectedListItem(string id, out string? itemId)
        {
            var found = listViewports.TryGetValue(id, out var viewport);
            itemId = viewport.Selection;
            return found;
        }

        internal (string? Selection, float Offset) GetListViewport(string id) => listViewports.TryGetValue(id, out var viewport)
            ? viewport : default;

        /// <summary>Tries to read the fake's current graph selection.</summary>
        public bool TryGetSelectedGraphNode(string id, out string? nodeId) => graphSelections.TryGetValue(id, out nodeId);

        /// <summary>Tries to read the fake's current graph viewport.</summary>
        public bool TryGetGraphViewport(string id, out UiGraphViewport viewport) => graphViewports.TryGetValue(id, out viewport);

        /// <inheritdoc/>
        public void Dispose()
        {
            CloseGraphConnections();
            shown = false;
            Dismissed = null;
            var callback = release;
            release = null;
            callback?.Invoke(this);
            System.Threading.Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
        }

    }
}
