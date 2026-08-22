using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.CreatorTools.Shared
{
    internal static class CreatorGraphParameters
    {
        public const string EntityId = "entityId";
        public const string NativeBindingId = "nativeBindingId";
        public const string PersonaId = "personaId";
        public const string Value = "value";
        public const string Text = "text";
        public const string Seconds = "seconds";
        public const string Radius = "radius";
        public const string CueId = "cueId";
        public const string Objective = "objective";
        public const string Prompt = "prompt";
        public const string Name = "name";
        public const string Tint = "tint";
        public const string Scale = "scale";
        public const string Brain = "brain";
        public const string MaxActivations = "maxActivations";
    }

    internal interface ICreatorEventRuntime
    {
        OperationResult<bool> Execute(CreatorGraphNode node);
    }

    /// <summary>Executes validated project flow without unbounded work or per-frame timers.</summary>
    internal sealed class CreatorEventGraphRunner : IDisposable
    {
        private const int MaximumStepsPerUpdate = 64;
        private const int MaximumSessionSteps = 10000;
        private const int MaximumPendingDelays = 1024;

        private sealed class PendingDelay
        {
            public PendingDelay(string nodeId, float seconds)
            {
                NodeId = nodeId;
                Remaining = seconds;
            }

            public string NodeId { get; }
            public float Remaining { get; set; }
        }

        private readonly ICreatorEventRuntime runtime;
        private readonly IReadOnlyList<CreatorGraphNode> orderedNodes;
        private readonly Dictionary<string, CreatorGraphNode> nodes;
        private readonly Dictionary<string, List<CreatorGraphEdge>> outgoing;
        private readonly Dictionary<string, int> activations = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Queue<string> ready = new Queue<string>();
        private readonly List<PendingDelay> pending = new List<PendingDelay>();
        private readonly List<string> completedDelays = new List<string>();
        private int totalSteps;
        private int remainingFrameSteps = MaximumStepsPerUpdate;
        private bool running;
        private bool pumping;
        private bool disposed;

        public CreatorEventGraphRunner(CreatorEventProject project, ICreatorEventRuntime runtime)
        {
            Project = project ?? throw new ArgumentNullException(nameof(project));
            this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            orderedNodes = project.Nodes.OrderBy(node => node.Id, StringComparer.Ordinal).ToArray();
            nodes = orderedNodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
            outgoing = project.Edges
                .GroupBy(edge => edge.FromNodeId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .OrderBy(edge => edge.FromPort, StringComparer.Ordinal)
                        .ThenBy(edge => edge.ToNodeId, StringComparer.Ordinal)
                        .ThenBy(edge => edge.ToPort, StringComparer.Ordinal)
                        .ToList(),
                    StringComparer.Ordinal);
        }

        public CreatorEventProject Project { get; }
        public bool IsRunning => running && !disposed;
        public int TotalSteps => totalSteps;
        public string LastProblem { get; private set; } = string.Empty;

        public OperationResult<bool> Start()
        {
            if (disposed)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The event runner is disposed.");
            }
            if (running) return OperationResult<bool>.Success(false);

            LastProblem = string.Empty;
            ready.Clear();
            pending.Clear();
            completedDelays.Clear();
            activations.Clear();
            totalSteps = 0;
            remainingFrameSteps = MaximumStepsPerUpdate;
            running = true;
            var fired = Fire(CreatorGraphNodeKind.ProjectStart);
            return fired.Succeeded
                ? OperationResult<bool>.Success(true)
                : OperationResult<bool>.Failure(fired.ErrorCode, fired.ErrorMessage);
        }

        public OperationResult<bool> Fire(
            CreatorGraphNodeKind triggerKind,
            string entityId = "",
            string value = "")
        {
            if (!IsRunning)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "Start the event runner first.");
            }
            if (!IsTrigger(triggerKind))
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Only trigger nodes can be fired.");
            }

            var matched = false;
            foreach (var node in orderedNodes)
            {
                if (node.Kind != triggerKind
                    || !Matches(TargetParameter(node), entityId)
                    || !Matches(Parameter(node, CreatorGraphParameters.Value), value))
                {
                    continue;
                }

                matched = true;
                EnqueueOutputs(node.Id, "fired");
            }
            Pump();
            return PumpResult(matched);
        }

        public OperationResult<bool> FireManual(string nodeId)
        {
            if (!IsRunning)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "Start the event runner first.");
            }
            if (!nodes.TryGetValue(nodeId ?? string.Empty, out var node)
                || node.Kind != CreatorGraphNodeKind.ManualTrigger)
            {
                return OperationResult<bool>.Failure(ModErrorCode.NotFound, "The manual trigger was not found.");
            }

            EnqueueOutputs(node.Id, "fired");
            Pump();
            return PumpResult(true);
        }

        public void Update(float scaledDeltaTime)
        {
            if (!IsRunning || scaledDeltaTime < 0f || float.IsNaN(scaledDeltaTime)) return;
            remainingFrameSteps = MaximumStepsPerUpdate;

            completedDelays.Clear();
            for (var index = 0; index < pending.Count; index++)
            {
                pending[index].Remaining -= scaledDeltaTime;
                if (pending[index].Remaining > 0f) continue;
                completedDelays.Add(pending[index].NodeId);
            }
            for (var index = pending.Count - 1; index >= 0; index--)
            {
                if (pending[index].Remaining <= 0f) pending.RemoveAt(index);
            }
            foreach (var nodeId in completedDelays) EnqueueOutputs(nodeId, "done");
            Pump();
        }

        public void Stop()
        {
            running = false;
            ready.Clear();
            pending.Clear();
            completedDelays.Clear();
            activations.Clear();
            totalSteps = 0;
            remainingFrameSteps = MaximumStepsPerUpdate;
        }

        public void Dispose()
        {
            if (disposed) return;
            Stop();
            disposed = true;
        }

        public static string Parameter(CreatorGraphNode node, string key) =>
            node.Parameters.TryGetValue(key, out var value) ? value : string.Empty;

        public static string TargetParameter(CreatorGraphNode node)
        {
            var entityId = Parameter(node, CreatorGraphParameters.EntityId);
            return string.IsNullOrEmpty(entityId)
                ? Parameter(node, CreatorGraphParameters.NativeBindingId)
                : entityId;
        }

        private void Pump()
        {
            if (pumping) return;
            pumping = true;
            try
            {
                while (IsRunning && ready.Count > 0 && remainingFrameSteps > 0)
                {
                    var nodeId = ready.Dequeue();
                    if (!nodes.TryGetValue(nodeId, out var node) || !CanActivate(node)) continue;
                    remainingFrameSteps--;
                    if (++totalSteps > MaximumSessionSteps)
                    {
                        Fail("Event run stopped at its 10,000-step safety limit.");
                        return;
                    }
                    if (node.Kind == CreatorGraphNodeKind.Delay)
                    {
                        var seconds = ParseFloat(Parameter(node, CreatorGraphParameters.Seconds));
                        if (seconds <= 0f)
                        {
                            EnqueueOutputs(node.Id, "done");
                        }
                        else if (pending.Count >= MaximumPendingDelays)
                        {
                            Fail("Event run stopped at its pending-delay safety limit.");
                        }
                        else
                        {
                            pending.Add(new PendingDelay(node.Id, seconds));
                        }
                        continue;
                    }

                    if (node.Kind == CreatorGraphNodeKind.Repeat)
                    {
                        var count = Math.Min(100, ParseInt(Parameter(node, CreatorGraphParameters.Value)));
                        for (var index = 0; index < count; index++) EnqueueOutputs(node.Id, "each");
                        EnqueueOutputs(node.Id, "done");
                        continue;
                    }

                    OperationResult<bool> result;
                    try
                    {
                        result = runtime.Execute(node);
                    }
                    catch (Exception exception)
                    {
                        Fail("Event node '" + node.Id + "' failed unexpectedly: " + exception.Message);
                        return;
                    }
                    if (node.Kind == CreatorGraphNodeKind.StateCondition)
                    {
                        if (!result.Succeeded) LastProblem = result.ErrorMessage;
                        EnqueueOutputs(node.Id, result.Succeeded && result.Value ? "true" : "false");
                        continue;
                    }
                    if (!result.Succeeded)
                    {
                        LastProblem = result.ErrorMessage;
                        EnqueueOutputs(node.Id, "failure");
                    }
                    else
                    {
                        EnqueueOutputs(node.Id, result.Value ? "success" : "failure");
                    }
                }
            }
            finally
            {
                pumping = false;
            }
        }

        private bool CanActivate(CreatorGraphNode node)
        {
            activations.TryGetValue(node.Id, out var count);
            var limit = ParseInt(Parameter(node, CreatorGraphParameters.MaxActivations));
            if (limit > 0 && count >= limit) return false;
            activations[node.Id] = count + 1;
            return true;
        }

        private void EnqueueOutputs(string nodeId, string port)
        {
            if (!outgoing.TryGetValue(nodeId, out var edges)) return;
            foreach (var edge in edges)
            {
                if (string.Equals(edge.FromPort, port, StringComparison.Ordinal)) ready.Enqueue(edge.ToNodeId);
            }
        }

        private void Fail(string problem)
        {
            LastProblem = problem;
            running = false;
            ready.Clear();
            pending.Clear();
        }

        private OperationResult<bool> PumpResult(bool value) =>
            !running && !string.IsNullOrEmpty(LastProblem)
                ? OperationResult<bool>.Failure(ModErrorCode.External, LastProblem)
                : OperationResult<bool>.Success(value);

        private static bool IsTrigger(CreatorGraphNodeKind kind) =>
            kind >= CreatorGraphNodeKind.ProjectStart && kind <= CreatorGraphNodeKind.ConversationDecision;

        private static bool Matches(string filter, string value) =>
            string.IsNullOrEmpty(filter) || string.Equals(filter, value ?? string.Empty, StringComparison.Ordinal);

        private static float ParseFloat(string value) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                && parsed >= 0f && !float.IsInfinity(parsed)
                ? parsed
                : 0f;

        private static int ParseInt(string value) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? Math.Min(parsed, 1000)
                : 0;
    }
}
