using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace TopiaForge.Mods
{
    /// <summary>Direction of a graph-node port.</summary>
    public enum UiGraphPortDirection
    {
        /// <summary>A port that accepts an incoming connection.</summary>
        Input = 0,

        /// <summary>A port that starts an outgoing connection.</summary>
        Output = 1
    }

    /// <summary>One immutable typed port on a graph node.</summary>
    public sealed class UiGraphPort
    {
        /// <summary>Creates a graph port.</summary>
        public UiGraphPort(
            string id,
            string label,
            UiGraphPortDirection direction,
            string dataType = "flow",
            bool required = false)
        {
            Id = GraphUiValidation.RequireId(id, nameof(id));
            Label = GraphUiValidation.RequireText(label, nameof(label), 256);
            if (!Enum.IsDefined(typeof(UiGraphPortDirection), direction))
            {
                throw new ArgumentOutOfRangeException(nameof(direction));
            }

            Direction = direction;
            DataType = GraphUiValidation.RequireText(dataType, nameof(dataType), 128);
            Required = required;
        }

        /// <summary>Gets the stable port id, unique inside its node.</summary>
        public string Id { get; }

        /// <summary>Gets the visible accessible port label.</summary>
        public string Label { get; }

        /// <summary>Gets whether the port accepts or produces connections.</summary>
        public UiGraphPortDirection Direction { get; }

        /// <summary>Gets the stable connection-compatibility type.</summary>
        public string DataType { get; }

        /// <summary>Gets whether the owning tool presents this port as required.</summary>
        public bool Required { get; }
    }

    /// <summary>One immutable node rendered in a bounded graph canvas.</summary>
    public sealed class UiGraphNode
    {
        /// <summary>Creates a graph node.</summary>
        public UiGraphNode(
            string id,
            string type,
            string title,
            Vec2 position,
            IEnumerable<UiGraphPort> ports,
            string subtitle = "",
            UiTone tone = UiTone.Neutral,
            bool enabled = true)
        {
            Id = GraphUiValidation.RequireId(id, nameof(id));
            Type = GraphUiValidation.RequireText(type, nameof(type), 128);
            Title = GraphUiValidation.RequireText(title, nameof(title), 256);
            GraphUiValidation.RequirePosition(position, nameof(position));
            if (ports == null) throw new ArgumentNullException(nameof(ports));
            if (!Enum.IsDefined(typeof(UiTone), tone)) throw new ArgumentOutOfRangeException(nameof(tone));

            var copy = new List<UiGraphPort>();
            var portIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var port in ports)
            {
                if (port == null) throw new ArgumentException("Graph ports cannot be null.", nameof(ports));
                if (!portIds.Add(port.Id))
                {
                    throw new ArgumentException("Graph port ids must be unique inside a node.", nameof(ports));
                }

                copy.Add(port);
                if (copy.Count > UiGraphCanvas.MaximumPortsPerNode)
                {
                    throw new ArgumentException(
                        "A graph node cannot contain more than " + UiGraphCanvas.MaximumPortsPerNode + " ports.",
                        nameof(ports));
                }
            }

            Position = position;
            Ports = new ReadOnlyCollection<UiGraphPort>(copy);
            Subtitle = GraphUiValidation.OptionalText(subtitle, nameof(subtitle), 512);
            Tone = tone;
            Enabled = enabled;
        }

        /// <summary>Gets the stable node id, unique inside its canvas.</summary>
        public string Id { get; }

        /// <summary>Gets the stable node type used by the owning tool.</summary>
        public string Type { get; }

        /// <summary>Gets the visible accessible node title.</summary>
        public string Title { get; }

        /// <summary>Gets the canvas-space position.</summary>
        public Vec2 Position { get; }

        /// <summary>Gets the immutable bounded port sequence.</summary>
        public IReadOnlyList<UiGraphPort> Ports { get; }

        /// <summary>Gets optional secondary node text.</summary>
        public string Subtitle { get; }

        /// <summary>Gets the semantic node tone.</summary>
        public UiTone Tone { get; }

        /// <summary>Gets whether the node accepts graph interaction.</summary>
        public bool Enabled { get; }
    }

    /// <summary>One immutable directed connection between graph ports.</summary>
    public sealed class UiGraphEdge
    {
        /// <summary>Creates a graph edge.</summary>
        public UiGraphEdge(
            string id,
            string sourceNodeId,
            string sourcePortId,
            string targetNodeId,
            string targetPortId,
            UiTone tone = UiTone.Neutral)
        {
            Id = GraphUiValidation.RequireId(id, nameof(id));
            SourceNodeId = GraphUiValidation.RequireId(sourceNodeId, nameof(sourceNodeId));
            SourcePortId = GraphUiValidation.RequireId(sourcePortId, nameof(sourcePortId));
            TargetNodeId = GraphUiValidation.RequireId(targetNodeId, nameof(targetNodeId));
            TargetPortId = GraphUiValidation.RequireId(targetPortId, nameof(targetPortId));
            if (!Enum.IsDefined(typeof(UiTone), tone)) throw new ArgumentOutOfRangeException(nameof(tone));
            Tone = tone;
        }

        /// <summary>Gets the stable edge id, unique inside its canvas.</summary>
        public string Id { get; }

        /// <summary>Gets the source node id.</summary>
        public string SourceNodeId { get; }

        /// <summary>Gets the source output-port id.</summary>
        public string SourcePortId { get; }

        /// <summary>Gets the target node id.</summary>
        public string TargetNodeId { get; }

        /// <summary>Gets the target input-port id.</summary>
        public string TargetPortId { get; }

        /// <summary>Gets the semantic edge tone.</summary>
        public UiTone Tone { get; }
    }

    /// <summary>Immutable graph viewport state.</summary>
    public readonly struct UiGraphViewport : IEquatable<UiGraphViewport>
    {
        /// <summary>Creates viewport state.</summary>
        public UiGraphViewport(Vec2 offset, float zoom = 1f)
        {
            GraphUiValidation.RequirePosition(offset, nameof(offset));
            if (float.IsNaN(zoom) || float.IsInfinity(zoom) || zoom < MinimumZoom || zoom > MaximumZoom)
            {
                throw new ArgumentOutOfRangeException(nameof(zoom));
            }

            Offset = offset;
            Zoom = zoom;
        }

        /// <summary>Gets the minimum supported zoom.</summary>
        public const float MinimumZoom = 0.25f;

        /// <summary>Gets the maximum supported zoom.</summary>
        public const float MaximumZoom = 2f;

        /// <summary>Gets the canvas-space pan offset.</summary>
        public Vec2 Offset { get; }

        /// <summary>Gets the bounded zoom factor.</summary>
        public float Zoom { get; }

        /// <summary>Gets the default viewport.</summary>
        public static UiGraphViewport Default => new UiGraphViewport(Vec2.Zero, 1f);

        /// <inheritdoc/>
        public bool Equals(UiGraphViewport other) => Offset.Equals(other.Offset) && Zoom.Equals(other.Zoom);

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is UiGraphViewport other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                return (Offset.GetHashCode() * 397) ^ Zoom.GetHashCode();
            }
        }

        /// <summary>Compares two viewport values.</summary>
        public static bool operator ==(UiGraphViewport left, UiGraphViewport right) => left.Equals(right);

        /// <summary>Compares two viewport values.</summary>
        public static bool operator !=(UiGraphViewport left, UiGraphViewport right) => !left.Equals(right);
    }

    /// <summary>Reports a requested graph-node move.</summary>
    public readonly struct UiGraphNodeMove
    {
        /// <summary>Creates a node-move request.</summary>
        public UiGraphNodeMove(string nodeId, Vec2 position)
        {
            NodeId = GraphUiValidation.RequireId(nodeId, nameof(nodeId));
            GraphUiValidation.RequirePosition(position, nameof(position));
            Position = position;
        }

        /// <summary>Gets the moved node id.</summary>
        public string NodeId { get; }

        /// <summary>Gets the requested canvas-space position.</summary>
        public Vec2 Position { get; }
    }

    /// <summary>Reports a requested directed connection between two graph ports.</summary>
    public readonly struct UiGraphConnectionRequest
    {
        /// <summary>Creates a connection request.</summary>
        public UiGraphConnectionRequest(
            string sourceNodeId,
            string sourcePortId,
            string targetNodeId,
            string targetPortId)
        {
            SourceNodeId = GraphUiValidation.RequireId(sourceNodeId, nameof(sourceNodeId));
            SourcePortId = GraphUiValidation.RequireId(sourcePortId, nameof(sourcePortId));
            TargetNodeId = GraphUiValidation.RequireId(targetNodeId, nameof(targetNodeId));
            TargetPortId = GraphUiValidation.RequireId(targetPortId, nameof(targetPortId));
        }

        /// <summary>Gets the source node id.</summary>
        public string SourceNodeId { get; }

        /// <summary>Gets the source output-port id.</summary>
        public string SourcePortId { get; }

        /// <summary>Gets the target node id.</summary>
        public string TargetNodeId { get; }

        /// <summary>Gets the target input-port id.</summary>
        public string TargetPortId { get; }
    }

    /// <summary>
    /// Immutable bounded node graph with isolated callbacks for selection, editing, and viewport changes.
    /// </summary>
    public sealed class UiGraphCanvas : UiNode
    {
        /// <summary>Maximum graph nodes accepted by one canvas.</summary>
        public const int MaximumNodes = 512;

        /// <summary>Maximum graph edges accepted by one canvas.</summary>
        public const int MaximumEdges = 1024;

        /// <summary>Maximum ports accepted by one graph node.</summary>
        public const int MaximumPortsPerNode = 32;

        /// <summary>Creates a bounded graph canvas.</summary>
        public UiGraphCanvas(
            string id,
            IEnumerable<UiGraphNode> nodes,
            IEnumerable<UiGraphEdge> edges,
            Action<string?> selectionChanged,
            string? selectedNodeId = null,
            UiGraphViewport? viewport = null,
            float height = 480f,
            bool enabled = true,
            Action<UiGraphNodeMove>? nodeMoved = null,
            Action<UiGraphConnectionRequest>? connectionRequested = null,
            Action<string>? connectionRemoved = null,
            Action<UiGraphViewport>? viewportChanged = null)
            : base(id)
        {
            if (nodes == null) throw new ArgumentNullException(nameof(nodes));
            if (edges == null) throw new ArgumentNullException(nameof(edges));
            if (selectionChanged == null) throw new ArgumentNullException(nameof(selectionChanged));
            if (float.IsNaN(height) || float.IsInfinity(height) || height < 160f || height > 1200f)
            {
                throw new ArgumentOutOfRangeException(nameof(height));
            }

            var nodeCopy = new List<UiGraphNode>();
            var nodesById = new Dictionary<string, UiGraphNode>(StringComparer.Ordinal);
            foreach (var node in nodes)
            {
                if (node == null) throw new ArgumentException("Graph nodes cannot be null.", nameof(nodes));
                if (!nodesById.TryAdd(node.Id, node))
                {
                    throw new ArgumentException("Graph node ids must be unique.", nameof(nodes));
                }

                nodeCopy.Add(node);
                if (nodeCopy.Count > MaximumNodes)
                {
                    throw new ArgumentException("A graph cannot contain more than " + MaximumNodes + " nodes.", nameof(nodes));
                }
            }

            var edgeCopy = new List<UiGraphEdge>();
            var edgeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in edges)
            {
                if (edge == null) throw new ArgumentException("Graph edges cannot be null.", nameof(edges));
                if (!edgeIds.Add(edge.Id)) throw new ArgumentException("Graph edge ids must be unique.", nameof(edges));
                ValidateEdge(edge, nodesById, nameof(edges));
                edgeCopy.Add(edge);
                if (edgeCopy.Count > MaximumEdges)
                {
                    throw new ArgumentException("A graph cannot contain more than " + MaximumEdges + " edges.", nameof(edges));
                }
            }

            if (selectedNodeId != null && !nodesById.ContainsKey(selectedNodeId))
            {
                throw new ArgumentException("The selected graph node was not found.", nameof(selectedNodeId));
            }

            Nodes = new ReadOnlyCollection<UiGraphNode>(nodeCopy);
            Edges = new ReadOnlyCollection<UiGraphEdge>(edgeCopy);
            SelectionChanged = selectionChanged;
            SelectedNodeId = selectedNodeId;
            Viewport = viewport ?? UiGraphViewport.Default;
            Height = height;
            Enabled = enabled;
            NodeMoved = nodeMoved;
            ConnectionRequested = connectionRequested;
            ConnectionRemoved = connectionRemoved;
            ViewportChanged = viewportChanged;
        }

        /// <summary>Gets the immutable bounded graph nodes.</summary>
        public IReadOnlyList<UiGraphNode> Nodes { get; }

        /// <summary>Gets the immutable bounded directed edges.</summary>
        public IReadOnlyList<UiGraphEdge> Edges { get; }

        /// <summary>Gets the initially selected node id, or <c>null</c>.</summary>
        public string? SelectedNodeId { get; }

        /// <summary>Gets the initial bounded viewport.</summary>
        public UiGraphViewport Viewport { get; }

        /// <summary>Gets the canvas height in scaled UI units.</summary>
        public float Height { get; }

        /// <summary>Gets whether the canvas initially accepts input.</summary>
        public bool Enabled { get; }

        /// <summary>Gets the selection callback. A null value represents cleared selection.</summary>
        public Action<string?> SelectionChanged { get; }

        /// <summary>Gets the optional node-move callback.</summary>
        public Action<UiGraphNodeMove>? NodeMoved { get; }

        /// <summary>Gets the optional connection-creation callback.</summary>
        public Action<UiGraphConnectionRequest>? ConnectionRequested { get; }

        /// <summary>Gets the optional connection-removal callback, receiving the edge id.</summary>
        public Action<string>? ConnectionRemoved { get; }

        /// <summary>Gets the optional viewport-change callback.</summary>
        public Action<UiGraphViewport>? ViewportChanged { get; }

        private static void ValidateEdge(
            UiGraphEdge edge,
            IReadOnlyDictionary<string, UiGraphNode> nodes,
            string parameterName)
        {
            if (!nodes.TryGetValue(edge.SourceNodeId, out var source) ||
                !nodes.TryGetValue(edge.TargetNodeId, out var target))
            {
                throw new ArgumentException("Every graph edge must reference existing nodes.", parameterName);
            }

            var sourcePort = FindPort(source, edge.SourcePortId);
            var targetPort = FindPort(target, edge.TargetPortId);
            if (sourcePort == null || targetPort == null ||
                sourcePort.Direction != UiGraphPortDirection.Output ||
                targetPort.Direction != UiGraphPortDirection.Input)
            {
                throw new ArgumentException("Graph edges must connect an output port to an existing input port.", parameterName);
            }

            if (!string.Equals(sourcePort.DataType, targetPort.DataType, StringComparison.Ordinal))
            {
                throw new ArgumentException("Connected graph ports must use the same data type.", parameterName);
            }
        }

        private static UiGraphPort? FindPort(UiGraphNode node, string id)
        {
            foreach (var port in node.Ports)
            {
                if (string.Equals(port.Id, id, StringComparison.Ordinal)) return port;
            }

            return null;
        }
    }

    internal static class GraphUiValidation
    {
        private const float MaximumCoordinate = 100000f;

        public static string RequireId(string value, string parameterName) =>
            RequireText(value, parameterName, 128);

        public static string RequireText(string value, string parameterName, int maximumLength)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
            {
                throw new ArgumentException(
                    "A value containing 1-" + maximumLength + " characters is required.",
                    parameterName);
            }

            return value;
        }

        public static string OptionalText(string? value, string parameterName, int maximumLength)
        {
            var normalized = value ?? string.Empty;
            if (normalized.Length > maximumLength)
            {
                throw new ArgumentException("The value cannot exceed " + maximumLength + " characters.", parameterName);
            }

            return normalized;
        }

        public static void RequirePosition(Vec2 value, string parameterName)
        {
            if (!value.IsFinite || Math.Abs(value.X) > MaximumCoordinate || Math.Abs(value.Y) > MaximumCoordinate)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    "Graph coordinates must be finite and within plus or minus " + MaximumCoordinate + ".");
            }
        }
    }
}
