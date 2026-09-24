using System;
using System.Globalization;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.CreatorTools.Shared
{
    internal sealed partial class CreatorWorkbench
    {
        private void PollProjectTriggers()
        {
            if (runner?.IsRunning != true || activeProject == null) return;
            foreach (var node in activeProject.Nodes)
            {
                if (node.Kind == CreatorGraphNodeKind.PlayerEnteredRadius)
                {
                    PollRadiusNode(node);
                }
                else if (node.Kind == CreatorGraphNodeKind.RobotObjectiveState)
                {
                    PollObjectiveNode(node);
                }
            }
        }

        private void PollRadiusNode(CreatorGraphNode node)
        {
            var entry = ProjectRoster(node);
            if (entry == null || !context.LocalPlayer.TryGetSnapshot(out var player) || player == null) return;
            Vec3 targetPosition;
            if (entry.Entity != null) targetPosition = entry.Entity.Position;
            else if (!TryGetTransform(entry, out var targetTransform)) return;
            else targetPosition = targetTransform.Position;
            var radius = float.TryParse(
                Param(node, "radius"),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed)
                ? Math.Max(0f, parsed)
                : 0f;
            var inside = Vec3.Distance(player.Position, targetPosition) <= radius;
            if (inside && enteredRadiusNodes.Add(node.Id))
            {
                runner?.Fire(CreatorGraphNodeKind.PlayerEnteredRadius, CreatorEventGraphRunner.TargetParameter(node));
            }
            else if (!inside)
            {
                enteredRadiusNodes.Remove(node.Id);
            }
        }

        private void PollObjectiveNode(CreatorGraphNode node)
        {
            var entry = ProjectRoster(node);
            if (entry?.Robot == null || objectives?.TryGetObjective(entry.Robot, out var handle) != true || handle == null) return;
            if (objectiveStates.TryGetValue(entry.Id, out var previous) && previous == handle.State) return;
            objectiveStates[entry.Id] = handle.State;
            runner?.Fire(
                CreatorGraphNodeKind.RobotObjectiveState,
                CreatorEventGraphRunner.TargetParameter(node),
                handle.State.ToString());
        }

        private string ProjectIdForRoster(string rosterId)
        {
            foreach (var pair in projectEntities)
            {
                if (string.Equals(pair.Value, rosterId, StringComparison.Ordinal)) return pair.Key;
            }
            return string.Empty;
        }

        private string ProjectTargetIdForRoster(string rosterId)
        {
            var entityId = ProjectIdForRoster(rosterId);
            if (!string.IsNullOrEmpty(entityId)) return entityId;
            foreach (var pair in projectBindings)
            {
                if (string.Equals(pair.Value, rosterId, StringComparison.Ordinal)) return pair.Key;
            }
            return string.Empty;
        }
    }
}
