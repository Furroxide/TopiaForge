using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.CreatorTools.Shared
{
    internal sealed partial class CreatorWorkbench
    {
        private readonly Dictionary<string, IInteractableRegistration> projectInteractions =
            new Dictionary<string, IInteractableRegistration>(StringComparer.Ordinal);

        private OperationResult<bool> RegisterProjectInteractions()
        {
            if (activeProject == null) return OperationResult<bool>.Success(false);
            var changed = false;
            foreach (var targetId in activeProject.Nodes
                .Where(node => node.Kind == CreatorGraphNodeKind.InteractionTrigger)
                .Select(CreatorEventGraphRunner.TargetParameter)
                .Where(target => !string.IsNullOrEmpty(target))
                .Distinct(StringComparer.Ordinal))
            {
                var result = RegisterProjectInteractionsFor(targetId);
                if (!result.Succeeded) return result;
                changed |= result.Value;
            }
            return OperationResult<bool>.Success(changed);
        }

        private OperationResult<bool> RegisterProjectInteractionsFor(string targetId)
        {
            if (activeProject == null)
            {
                return OperationResult<bool>.Success(false);
            }
            string? rosterId = null;
            if (!projectEntities.TryGetValue(targetId, out rosterId)) projectBindings.TryGetValue(targetId, out rosterId);
            if (string.IsNullOrEmpty(rosterId)) return OperationResult<bool>.Success(false);
            if (FindRoster(rosterId)?.Entity is not { } entity)
            {
                return OperationResult<bool>.Failure(ModErrorCode.Unavailable, "The selected native binding does not expose a safe interaction entity.");
            }
            var changed = false;
            foreach (var node in activeProject.Nodes)
            {
                if (node.Kind != CreatorGraphNodeKind.InteractionTrigger
                    || !string.Equals(CreatorEventGraphRunner.TargetParameter(node), targetId, StringComparison.Ordinal)
                    || projectInteractions.ContainsKey(node.Id)) continue;
                var prompt = Param(node, CreatorGraphParameters.Prompt);
                if (string.IsNullOrWhiteSpace(prompt)) prompt = "INTERACT";
                var distance = float.TryParse(
                    Param(node, CreatorGraphParameters.Radius),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var parsed)
                    ? parsed
                    : 3f;
                if (distance <= 0f || distance > 10f || float.IsNaN(distance) || float.IsInfinity(distance))
                {
                    return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Interaction distance must be greater than zero and no more than 10.");
                }
                var registered = context.Interactions.Register(
                    entity,
                    new InteractableDefinition(prompt, distance),
                    interaction => runner?.Fire(CreatorGraphNodeKind.InteractionTrigger, targetId));
                if (!registered.TryGetValue(out var registration))
                {
                    return OperationResult<bool>.Failure(registered.ErrorCode, registered.ErrorMessage);
                }
                projectInteractions[node.Id] = registration;
                changed = true;
            }
            return OperationResult<bool>.Success(changed);
        }

        private void DisposeProjectInteractions(string targetId = "")
        {
            foreach (var pair in new List<KeyValuePair<string, IInteractableRegistration>>(projectInteractions))
            {
                var node = activeProject?.Nodes.FirstOrDefault(item => item.Id == pair.Key);
                if (!string.IsNullOrEmpty(targetId)
                    && !string.Equals(node == null ? string.Empty : CreatorEventGraphRunner.TargetParameter(node), targetId, StringComparison.Ordinal)) continue;
                pair.Value.Dispose();
                projectInteractions.Remove(pair.Key);
            }
        }
    }
}
