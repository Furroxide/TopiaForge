using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.CreatorContent
{
    internal sealed partial class CreatorProjectValidator
    {
        private static void ValidateEdges(List<CreatorProjectValidationIssue> issues, CreatorEventProject project)
        {
            var nodes = project.Nodes
                .GroupBy(node => node.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            foreach (var edge in project.Edges)
            {
                if (!nodes.TryGetValue(edge.FromNodeId, out var source))
                {
                    Error(issues, "edge.source", "The edge source node does not exist.", edge.FromNodeId);
                }
                else if (!AllowedOutput(source.Kind, edge.FromPort))
                {
                    Error(issues, "edge.output-port", "Output port '" + edge.FromPort + "' is invalid for " + source.Kind + ".", edge.FromNodeId);
                }
                if (!nodes.TryGetValue(edge.ToNodeId, out var target))
                {
                    Error(issues, "edge.target", "The edge target node does not exist.", edge.ToNodeId);
                }
                else if (IsTrigger(target.Kind))
                {
                    Error(issues, "edge.trigger-target", "Trigger nodes cannot receive graph edges.", edge.ToNodeId);
                }
                if (!string.Equals(edge.ToPort, "in", StringComparison.Ordinal)) Error(issues, "edge.input-port", "Every target port must be 'in'.", edge.ToNodeId);
            }
        }

        private static void ValidateAcyclic(List<CreatorProjectValidationIssue> issues, CreatorEventProject project)
        {
            var adjacency = project.Nodes
                .GroupBy(node => node.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, _ => new List<string>(), StringComparer.Ordinal);
            foreach (var edge in project.Edges)
            {
                if (adjacency.TryGetValue(edge.FromNodeId, out var targets) && adjacency.ContainsKey(edge.ToNodeId))
                {
                    targets.Add(edge.ToNodeId);
                }
            }
            var states = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var node in adjacency.Keys)
            {
                if (HasCycle(node, adjacency, states))
                {
                    Error(issues, "graph.cycle", "Event graph cycles are not supported in schema version 1.", node);
                    return;
                }
            }
        }

        private static bool HasCycle(string node, IReadOnlyDictionary<string, List<string>> adjacency, IDictionary<string, int> states)
        {
            if (states.TryGetValue(node, out var state)) return state == 1;
            states[node] = 1;
            foreach (var target in adjacency[node])
            {
                if (HasCycle(target, adjacency, states)) return true;
            }
            states[node] = 2;
            return false;
        }

        private static bool RequiresEntityOnly(CreatorGraphNodeKind kind) =>
            kind == CreatorGraphNodeKind.SpawnContent;

        private static bool IsTrigger(CreatorGraphNodeKind kind) =>
            kind >= CreatorGraphNodeKind.ProjectStart && kind <= CreatorGraphNodeKind.ConversationDecision;

        private static bool SupportsEntityOrNativeBinding(CreatorGraphNodeKind kind) =>
            kind == CreatorGraphNodeKind.InteractionTrigger
            || kind == CreatorGraphNodeKind.PlayerEnteredRadius
            || kind == CreatorGraphNodeKind.EntityRemoved
            || kind == CreatorGraphNodeKind.RobotObjectiveState
            || kind == CreatorGraphNodeKind.DespawnContent
            || kind == CreatorGraphNodeKind.SetTransform
            || kind == CreatorGraphNodeKind.ConfigureRobot
            || kind == CreatorGraphNodeKind.SetRobotPersonality
            || kind == CreatorGraphNodeKind.SetRobotObjective
            || kind == CreatorGraphNodeKind.SetRobotEmote
            || kind == CreatorGraphNodeKind.BeginConversation;

        private static void RequireEntity(
            List<CreatorProjectValidationIssue> issues,
            CreatorGraphNode node,
            ISet<string> entityIds)
        {
            if (!node.Parameters.TryGetValue("entityId", out var entityId) || !entityIds.Contains(entityId))
            {
                Error(issues, "node.entity", "This node requires an existing 'entityId'.", node.Id);
            }
        }

        private static void ValidateExclusiveTarget(
            List<CreatorProjectValidationIssue> issues,
            CreatorEventProject project,
            CreatorGraphNode node,
            ISet<string> entityIds,
            ISet<string> bindingIds)
        {
            var hasEntity = HasValue(node, "entityId");
            var hasBinding = HasValue(node, "nativeBindingId");
            if (hasEntity == hasBinding)
            {
                Error(issues, "node.target", "This node requires exactly one 'entityId' or 'nativeBindingId'.", node.Id);
                return;
            }
            if (hasEntity && !entityIds.Contains(node.Parameters["entityId"]))
            {
                Error(issues, "node.entity", "The node's 'entityId' does not exist.", node.Id);
            }
            if (hasBinding && !bindingIds.Contains(node.Parameters["nativeBindingId"]))
            {
                Error(issues, "node.native-binding", "The node's 'nativeBindingId' does not exist.", node.Id);
            }
            if (hasBinding && bindingIds.Contains(node.Parameters["nativeBindingId"]))
            {
                var binding = project.NativeBindings.First(item =>
                    string.Equals(item.Id, node.Parameters["nativeBindingId"], StringComparison.Ordinal));
                if (string.Equals(binding.AdapterId, "robotkit.native-robot", StringComparison.Ordinal)
                    && (node.Kind == CreatorGraphNodeKind.InteractionTrigger
                        || node.Kind == CreatorGraphNodeKind.DespawnContent))
                {
                    Error(
                        issues,
                        "node.native-binding-capability",
                        "RobotKit native bindings do not expose an interaction entity or reversible visibility lease for this node.",
                        node.Id);
                }
            }
        }

        private static void ValidateOptionalTarget(
            List<CreatorProjectValidationIssue> issues,
            CreatorEventProject project,
            CreatorGraphNode node,
            ISet<string> entityIds,
            ISet<string> bindingIds)
        {
            var hasEntity = HasValue(node, "entityId");
            var hasBinding = HasValue(node, "nativeBindingId");
            if (hasEntity && hasBinding)
            {
                Error(issues, "node.target", "A wildcard trigger may specify at most one 'entityId' or 'nativeBindingId'.", node.Id);
                return;
            }
            if (hasEntity && !entityIds.Contains(node.Parameters["entityId"]))
            {
                Error(issues, "node.entity", "The trigger's 'entityId' does not exist.", node.Id);
            }
            if (hasBinding && !bindingIds.Contains(node.Parameters["nativeBindingId"]))
            {
                Error(issues, "node.native-binding", "The trigger's 'nativeBindingId' does not exist.", node.Id);
            }
        }

        private static bool HasValue(CreatorGraphNode node, string key) =>
            node.Parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);

        private static void ValidateRobotConfiguration(
            List<CreatorProjectValidationIssue> issues,
            CreatorGraphNode node)
        {
            var hasName = HasValue(node, "name");
            var hasTint = HasValue(node, "tint");
            var hasScale = HasValue(node, "scale");
            var hasBrain = HasValue(node, "brain");
            var hasLegacyBrain = !hasBrain && HasValue(node, "value");
            if (!hasName && !hasTint && !hasScale && !hasBrain && !hasLegacyBrain)
            {
                Error(issues, "node.robot-configuration", "Configure-robot nodes require at least one of 'name', 'tint', 'scale', or 'brain'.", node.Id);
                return;
            }

            if (hasName && node.Parameters["name"].Trim().Length > 64)
            {
                Error(issues, "node.robot-name", "Robot names cannot exceed 64 characters.", node.Id);
            }
            if (hasTint && !IsRobotTint(node.Parameters["tint"]))
            {
                Error(issues, "node.robot-tint", "Robot tint must be White, Red, Orange, Yellow, Green, Cyan, Blue, Purple, or Pink.", node.Id);
            }
            if (hasScale
                && (!float.TryParse(node.Parameters["scale"], NumberStyles.Float, CultureInfo.InvariantCulture, out var scale)
                    || float.IsNaN(scale) || float.IsInfinity(scale) || scale < 0.25f || scale > 4f))
            {
                Error(issues, "node.robot-scale", "Robot scale must be finite and between 0.25 and 4.", node.Id);
            }

            var brain = hasBrain ? node.Parameters["brain"] : hasLegacyBrain ? node.Parameters["value"] : string.Empty;
            if (brain.Length > 0
                && !string.Equals(brain.Trim(), "Dormant", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(brain.Trim(), "Autonomous", StringComparison.OrdinalIgnoreCase))
            {
                Error(issues, "node.brain-mode", "Robot brain must be Dormant or Autonomous.", node.Id);
            }
        }

        private static bool IsRobotTint(string value)
        {
            switch ((value ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "WHITE":
                case "RED":
                case "ORANGE":
                case "YELLOW":
                case "GREEN":
                case "CYAN":
                case "BLUE":
                case "PURPLE":
                case "PINK":
                    return true;
                default:
                    return false;
            }
        }

        private static bool ValidCompactTransform(CreatorGraphNode node)
        {
            if (!node.Parameters.TryGetValue("value", out var raw)) return false;
            var parts = raw.Split(',');
            if (parts.Length != 3 && parts.Length != 10) return false;
            var numbers = new float[parts.Length];
            for (var index = 0; index < parts.Length; index++)
            {
                if (!float.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[index])
                    || float.IsNaN(numbers[index]) || float.IsInfinity(numbers[index]))
                {
                    return false;
                }
            }
            if (parts.Length == 3) return true;
            try
            {
                _ = new TransformState(
                    new Vec3(numbers[0], numbers[1], numbers[2]),
                    new Quat(numbers[3], numbers[4], numbers[5], numbers[6]),
                    new Vec3(numbers[7], numbers[8], numbers[9]));
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static bool AllowedOutput(CreatorGraphNodeKind kind, string port)
        {
            if (kind >= CreatorGraphNodeKind.ProjectStart && kind <= CreatorGraphNodeKind.ConversationDecision)
            {
                return string.Equals(port, "fired", StringComparison.Ordinal);
            }
            if (kind == CreatorGraphNodeKind.Delay)
            {
                return string.Equals(port, "done", StringComparison.Ordinal);
            }
            if (kind == CreatorGraphNodeKind.StateCondition)
            {
                return string.Equals(port, "true", StringComparison.Ordinal)
                    || string.Equals(port, "false", StringComparison.Ordinal);
            }
            if (kind == CreatorGraphNodeKind.Repeat)
            {
                return string.Equals(port, "each", StringComparison.Ordinal)
                    || string.Equals(port, "done", StringComparison.Ordinal);
            }
            return (kind >= CreatorGraphNodeKind.SpawnContent && kind <= CreatorGraphNodeKind.PlayAudio)
                && (string.Equals(port, "success", StringComparison.Ordinal)
                    || string.Equals(port, "failure", StringComparison.Ordinal));
        }

        private static void ValidateObjective(
            List<CreatorProjectValidationIssue> issues,
            CreatorGraphNode node)
        {
            if (!node.Parameters.TryGetValue("objective", out var raw) || string.IsNullOrWhiteSpace(raw))
            {
                Error(issues, "node.objective", "Set-robot-objective nodes require a bounded 'objective'.", node.Id);
                return;
            }
            var parts = raw.Split(new[] { ':' }, 2);
            var kind = parts[0].Trim().ToUpperInvariant();
            var target = parts.Length == 2 ? parts[1].Trim() : string.Empty;
            var known = kind == "IDLE" || kind == "WANDER" || kind == "FOLLOW"
                || kind == "GO_TO" || kind == "PATROL" || kind == "FLEE";
            var targetRequired = kind == "FOLLOW" || kind == "GO_TO" || kind == "PATROL" || kind == "FLEE";
            if (!known || (targetRequired && target.Length == 0) || target.Length > 128
                || (kind == "IDLE" && target.Length > 0))
            {
                Error(issues, "node.objective", "Objective must be IDLE, WANDER[:target], FOLLOW:target, GO_TO:target, PATROL:target, or FLEE:target with a target no longer than 128 characters.", node.Id);
            }
        }

        private static void ValidateStateCondition(
            List<CreatorProjectValidationIssue> issues,
            CreatorEventProject project,
            CreatorGraphNode node,
            string condition,
            ISet<string> entityIds,
            ISet<string> bindingIds)
        {
            var value = condition.Trim();
            if (bool.TryParse(value, out _)) return;
            var targeted = string.Equals(value, "entity.alive", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "robot.autonomous", StringComparison.OrdinalIgnoreCase);
            const string objectivePrefix = "objective:";
            if (value.StartsWith(objectivePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var state = value.Substring(objectivePrefix.Length);
                targeted = RobotObjectiveStates.Contains(state);
                if (!targeted)
                {
                    Error(issues, "node.condition", "Objective conditions must name a RobotObjectiveState.", node.Id);
                    return;
                }
            }
            else if (!targeted)
            {
                Error(issues, "node.condition", "State condition must be true, false, entity.alive, robot.autonomous, or objective:<RobotObjectiveState>.", node.Id);
                return;
            }
            ValidateExclusiveTarget(issues, project, node, entityIds, bindingIds);
        }

        private static bool TryPositiveNumber(CreatorGraphNode node, string key, float maximum) =>
            node.Parameters.TryGetValue(key, out var raw)
            && float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && value > 0f && value <= maximum && !float.IsNaN(value) && !float.IsInfinity(value);

        private static void Limits(List<CreatorProjectValidationIssue> issues, CreatorEventProject project)
        {
            if (project.Entities.Count > 256) Error(issues, "project.entities-limit", "A project may contain at most 256 entities.", project.Id);
            if (project.NativeBindings.Count > 64) Error(issues, "project.native-bindings-limit", "A project may contain at most 64 native bindings.", project.Id);
            if (project.Personas.Count > 64) Error(issues, "project.personas-limit", "A project may contain at most 64 personas.", project.Id);
            if (project.Nodes.Count > 512) Error(issues, "project.nodes-limit", "A project may contain at most 512 nodes.", project.Id);
            if (project.Edges.Count > 1024) Error(issues, "project.edges-limit", "A project may contain at most 1024 edges.", project.Id);
        }

        private static void Unique(List<CreatorProjectValidationIssue> issues, IEnumerable<string> ids, string code)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                if (!seen.Add(id)) Error(issues, code + ".duplicate", "Ids must be unique within their collection.", id);
            }
        }

        private static void ValidateId(List<CreatorProjectValidationIssue> issues, string value, string code, int maximum)
        {
            if (!CreatorIds.IsLocalId(value, maximum)) Error(issues, code, "Ids may contain only letters, digits, dots, underscores, or hyphens.", value);
        }

        private static void Bounded(List<CreatorProjectValidationIssue> issues, string value, int maximum, string code, string subject)
        {
            if (value.Length > maximum) Error(issues, code, "Text exceeds " + maximum.ToString(CultureInfo.InvariantCulture) + " characters.", subject);
        }

        private static void Error(List<CreatorProjectValidationIssue> issues, string code, string message, string subject) =>
            issues.Add(new CreatorProjectValidationIssue(code, message, CreatorProjectValidationSeverity.Error, subject));

        private static void Warning(List<CreatorProjectValidationIssue> issues, string code, string message, string subject) =>
            issues.Add(new CreatorProjectValidationIssue(code, message, CreatorProjectValidationSeverity.Warning, subject));
    }
}
