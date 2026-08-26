using System;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.CreatorTools.Shared
{
    internal sealed partial class CreatorWorkbench
    {
        private const string RobotKitProjectContentPrefix = "io.github.furroxide.topiaforge.robotkit:";
        private const string RobotKitProjectSourceVersion = "0.1.0-rc.1";
        private const string RobotKitNativeAdapterId = "robotkit.native-robot";

        private string ProjectContentProblem(CreatorProjectEntity entity)
        {
            if (TryRobotKitProjectContent(entity.ContentId, out var robotTypeId))
            {
                if (!robots.IsAvailable) return "RobotKit is unavailable in this scene";
                if (!string.IsNullOrEmpty(entity.ExpectedSourceVersion)
                    && !string.Equals(entity.ExpectedSourceVersion, CurrentRobotKitSourceVersion(), StringComparison.OrdinalIgnoreCase))
                {
                    return "RobotKit version differs from the authored recipe";
                }
                if (!string.Equals(robotTypeId, "default", StringComparison.OrdinalIgnoreCase)
                    && !robots.RobotTypes.Any(type => string.Equals(type.Id, robotTypeId, StringComparison.OrdinalIgnoreCase)))
                {
                    return "RobotKit type '" + robotTypeId + "' is unavailable";
                }
                return string.Empty;
            }

            var descriptor = content.Catalog.Entries.FirstOrDefault(item =>
                string.Equals(item.ContentId, entity.ContentId, StringComparison.OrdinalIgnoreCase));
            if (descriptor == null) return "content source is not installed or enabled";
            if (!string.IsNullOrEmpty(entity.ExpectedSourceVersion)
                && !string.Equals(entity.ExpectedSourceVersion, descriptor.SourceVersion, StringComparison.OrdinalIgnoreCase))
            {
                return "content source version differs from the authored recipe";
            }
            return string.Empty;
        }

        private string GraphNodeContentProblem(CreatorGraphNode node)
        {
            var id = Value(node, CreatorGraphParameters.EntityId);
            if (string.IsNullOrEmpty(id)) return string.Empty;
            var entity = activeProject?.Entities.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            return entity == null ? "project entity '" + id + "' is unresolved" : ProjectContentProblem(entity);
        }

        private string GraphTargetSupportProblem(CreatorGraphNode node)
        {
            var bindingId = Value(node, CreatorGraphParameters.NativeBindingId);
            if (string.IsNullOrEmpty(bindingId)) return string.Empty;
            var binding = activeProject?.NativeBindings.FirstOrDefault(item => item.Id == bindingId);
            if (binding == null) return "native binding '" + bindingId + "' is unresolved";
            if (node.Kind == CreatorGraphNodeKind.RobotObjectiveState
                || node.Kind == CreatorGraphNodeKind.SetRobotObjective
                || node.Kind == CreatorGraphNodeKind.SetRobotEmote
                || node.Kind == CreatorGraphNodeKind.BeginConversation
                || node.Kind == CreatorGraphNodeKind.ConversationDecision)
            {
                return "borrowed native targets do not expose RobotKit objective, emote, or conversation handles";
            }
            var robotKit = string.Equals(binding.AdapterId, RobotKitNativeAdapterId, StringComparison.Ordinal);
            if (robotKit && (node.Kind == CreatorGraphNodeKind.InteractionTrigger
                || node.Kind == CreatorGraphNodeKind.DespawnContent))
            {
                return "RobotKit native targets do not expose safe interaction or visibility operations";
            }
            if (!robotKit && (node.Kind == CreatorGraphNodeKind.ConfigureRobot
                || node.Kind == CreatorGraphNodeKind.SetRobotPersonality))
            {
                return "this native adapter does not expose reversible robot editing";
            }
            if (robotKit && node.Kind == CreatorGraphNodeKind.ConfigureRobot
                && (!string.IsNullOrEmpty(Value(node, CreatorGraphParameters.Name))
                    || !string.IsNullOrEmpty(Value(node, CreatorGraphParameters.Tint))
                    || !string.IsNullOrEmpty(Value(node, CreatorGraphParameters.Scale))))
            {
                return "borrowed native robots only permit lease-backed brain configuration";
            }
            if (node.Kind == CreatorGraphNodeKind.StateCondition)
            {
                var condition = Value(node, CreatorGraphParameters.Value);
                if (!bool.TryParse(condition, out _)
                    && !string.Equals(condition, "entity.alive", StringComparison.OrdinalIgnoreCase))
                {
                    return "borrowed native state conditions support only booleans and entity.alive";
                }
            }
            return string.Empty;
        }

        private OperationResult<string> SpawnProjectRobot(
            CreatorProjectEntity definition,
            TransformState transform,
            string robotTypeId)
        {
            var spawned = robots.Spawn(new RobotAgentSpawnRequest(
                transform.Position,
                brainMode: RobotBrainMode.Dormant,
                name: definition.DisplayName,
                robotTypeId: string.Equals(robotTypeId, "default", StringComparison.OrdinalIgnoreCase) ? null : robotTypeId));
            if (!spawned.TryGetValue(out var agent))
            {
                return OperationResult<string>.Failure(spawned.ErrorCode, spawned.ErrorMessage);
            }
            var transformed = context.Entities.SetTransform(agent, transform);
            if (!transformed.Succeeded)
            {
                agent.Despawn();
                agent.Dispose();
                return OperationResult<string>.Failure(transformed.ErrorCode, transformed.ErrorMessage);
            }
            var entry = new CreatorRosterEntry(
                "project:" + definition.Id,
                definition.DisplayName,
                CreatorContentKind.Robot,
                owned: true,
                cleanup: agent)
            {
                Robot = agent,
                SourceId = robotTypeId,
                TargetName = "PROJECT " + definition.Id.ToUpperInvariant()
            };
            if (robotEditor != null && robotEditor.TryResolve(agent, out var robotTarget)) entry.RobotTarget = robotTarget;
            if (objectives != null)
            {
                var registered = objectives.RegisterTarget(
                    entry.TargetName,
                    RobotTargetKind.Robot,
                    () => agent.IsAlive ? new RobotTargetSnapshot(agent.Position, agent) : (RobotTargetSnapshot?)null);
                registered.TryGetValue(out var registration);
                entry.TargetRegistration = registration;
            }
            roster.Add(entry);
            projectEntities[definition.Id] = entry.Id;
            var interactions = runner == null
                ? OperationResult<bool>.Success(false)
                : RegisterProjectInteractionsFor(definition.Id);
            if (!interactions.Succeeded)
            {
                Despawn(entry);
                entry.Dispose();
                roster.Remove(entry);
                projectEntities.Remove(definition.Id);
                return OperationResult<string>.Failure(interactions.ErrorCode, interactions.ErrorMessage);
            }
            return OperationResult<string>.Success(definition.DisplayName + " spawned.");
        }

        private static bool TryRobotKitProjectContent(string contentId, out string robotTypeId)
        {
            var normalized = contentId ?? string.Empty;
            if (normalized.StartsWith(RobotKitProjectContentPrefix, StringComparison.OrdinalIgnoreCase))
            {
                robotTypeId = normalized.Substring(RobotKitProjectContentPrefix.Length);
                return !string.IsNullOrWhiteSpace(robotTypeId);
            }
            robotTypeId = string.Empty;
            return false;
        }

        private string CurrentRobotKitSourceVersion() =>
            context.Runtime.ProviderVersions.TryGetValue("io.github.furroxide.topiaforge.robotkit", out var version)
                ? version.ToString()
                : RobotKitProjectSourceVersion;
    }
}
