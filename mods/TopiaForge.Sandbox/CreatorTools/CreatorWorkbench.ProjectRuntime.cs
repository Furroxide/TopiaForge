using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.CreatorTools.Shared
{
    internal sealed partial class CreatorWorkbench
    {
        private readonly List<IAudioPlayback> graphAudio = new List<IAudioPlayback>();

        OperationResult<bool> ICreatorEventRuntime.Execute(CreatorGraphNode node)
        {
            var allowed = EnsureMutationAllowed();
            if (!allowed.Succeeded) return allowed;
            if (node.Kind == CreatorGraphNodeKind.StateCondition) return EvaluateStateCondition(node);
            switch (node.Kind)
            {
                case CreatorGraphNodeKind.SpawnContent:
                    return ExecuteSpawnNode(node);
                case CreatorGraphNodeKind.DespawnContent:
                    return ExecuteDespawnNode(node);
                case CreatorGraphNodeKind.SetTransform:
                    return ExecuteTransformNode(node);
                case CreatorGraphNodeKind.ConfigureRobot:
                    return ExecuteConfigureRobotNode(node);
                case CreatorGraphNodeKind.SetRobotPersonality:
                    return ExecutePersonalityNode(node);
                case CreatorGraphNodeKind.SetRobotObjective:
                    return ExecuteObjectiveNode(node);
                case CreatorGraphNodeKind.SetRobotEmote:
                    return ExecuteEmoteNode(node);
                case CreatorGraphNodeKind.BeginConversation:
                    return ExecuteConversationNode(node);
                case CreatorGraphNodeKind.ShowToast:
                    return context.Ui.ShowToast(Param(node, CreatorGraphParameters.Text), UiTone.Neutral);
                case CreatorGraphNodeKind.PlayAudio:
                    var cueId = Param(node, CreatorGraphParameters.CueId);
                    if (string.IsNullOrWhiteSpace(cueId))
                    {
                        return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Audio cue id is required.");
                    }
                    for (var index = graphAudio.Count - 1; index >= 0; index--)
                    {
                        if (graphAudio[index].IsPlaying) continue;
                        graphAudio[index].Dispose();
                        graphAudio.RemoveAt(index);
                    }
                    if (graphAudio.Count >= 256)
                    {
                        return OperationResult<bool>.Failure(ModErrorCode.RateLimited, "The event audio playback limit was reached.");
                    }
                    var audio = context.Audio.Play(new AudioPlayRequest(cueId));
                    if (!audio.TryGetValue(out var playback))
                    {
                        return OperationResult<bool>.Failure(audio.ErrorCode, audio.ErrorMessage);
                    }
                    graphAudio.Add(playback);
                    return OperationResult<bool>.Success(true);
                default:
                    return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Unsupported event action " + node.Kind + ".");
            }
        }

        private OperationResult<bool> ExecuteSpawnNode(CreatorGraphNode node)
        {
            var definition = ProjectEntity(node);
            if (definition == null) return MissingProjectEntity(node);
            var result = SpawnProjectEntity(definition);
            return result.Succeeded
                ? OperationResult<bool>.Success(true)
                : OperationResult<bool>.Failure(result.ErrorCode, result.ErrorMessage);
        }

        private OperationResult<bool> ExecuteDespawnNode(CreatorGraphNode node)
        {
            var id = Param(node, CreatorGraphParameters.EntityId);
            if (string.IsNullOrEmpty(id))
            {
                var bindingId = Param(node, CreatorGraphParameters.NativeBindingId);
                if (!projectBindings.TryGetValue(bindingId, out var borrowedRosterId)
                    || FindRoster(borrowedRosterId) is not { } borrowed)
                {
                    return OperationResult<bool>.Success(false);
                }
                if (borrowed.NativeTarget == null
                    || (borrowed.NativeTarget.Capabilities & CreatorSceneTargetCapabilities.TemporaryVisibility) == 0)
                {
                    return OperationResult<bool>.Failure(ModErrorCode.Conflict, "This borrowed native target cannot be soft-hidden safely.");
                }
                var lease = EnsureNativeEdit(borrowed);
                if (!lease.TryGetValue(out var edit)) return OperationResult<bool>.Failure(lease.ErrorCode, lease.ErrorMessage);
                var hidden = edit.SetTemporarilyHidden(true);
                if (hidden.Succeeded) borrowed.NativeHidden = true;
                if (hidden.Succeeded) runner?.Fire(CreatorGraphNodeKind.EntityRemoved, bindingId);
                return hidden;
            }
            if (!projectEntities.TryGetValue(id, out var rosterId) || FindRoster(rosterId) is not { } entry)
            {
                return OperationResult<bool>.Success(false);
            }
            Despawn(entry);
            DisposeProjectInteractions(id);
            entry.Dispose();
            roster.Remove(entry);
            projectEntities.Remove(id);
            runner?.Fire(CreatorGraphNodeKind.EntityRemoved, id);
            return OperationResult<bool>.Success(true);
        }

        private OperationResult<bool> ExecuteTransformNode(CreatorGraphNode node)
        {
            var entry = ProjectRoster(node);
            if (entry == null) return MissingProjectEntity(node);
            if (!TryGetTransform(entry, out var current)
                || !TryParseGraphTransform(Param(node, CreatorGraphParameters.Value), current, out var transform))
            {
                return OperationResult<bool>.Failure(
                    ModErrorCode.InvalidArgument,
                    "Transforms require a project-relative value 'x,y,z' or 'x,y,z,qx,qy,qz,qw,sx,sy,sz'.");
            }
            transform = new TransformState(transform.Position + projectRunOrigin, transform.Rotation, transform.Scale);
            var result = SetTransform(entry, transform, recordHistory: false);
            return result.Succeeded
                ? OperationResult<bool>.Success(true)
                : OperationResult<bool>.Failure(result.ErrorCode, result.ErrorMessage);
        }

        private OperationResult<bool> ExecuteConfigureRobotNode(CreatorGraphNode node)
        {
            var entry = ProjectRoster(node);
            if (entry?.RobotTarget != null && !entry.Owned)
            {
                var borrowedName = Param(node, CreatorGraphParameters.Name).Trim();
                var borrowedTint = Param(node, CreatorGraphParameters.Tint).Trim();
                var borrowedScale = Param(node, CreatorGraphParameters.Scale).Trim();
                var borrowedBrain = Param(node, CreatorGraphParameters.Brain).Trim();
                if (borrowedBrain.Length == 0) borrowedBrain = Param(node, CreatorGraphParameters.Value).Trim();
                if (borrowedName.Length > 0 || borrowedTint.Length > 0 || borrowedScale.Length > 0)
                {
                    return OperationResult<bool>.Failure(ModErrorCode.Conflict, "Borrowed native robots only permit lease-backed brain configuration.");
                }
                if (!string.Equals(borrowedBrain, "Dormant", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(borrowedBrain, "Autonomous", StringComparison.OrdinalIgnoreCase))
                {
                    return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Robot brain must be Dormant or Autonomous.");
                }
                var borrowedLease = EnsureRobotEdit(entry);
                if (!borrowedLease.TryGetValue(out var edit)) return OperationResult<bool>.Failure(borrowedLease.ErrorCode, borrowedLease.ErrorMessage);
                return edit.PreviewBrainMode(string.Equals(borrowedBrain, "Autonomous", StringComparison.OrdinalIgnoreCase)
                    ? RobotBrainMode.Autonomous
                    : RobotBrainMode.Dormant);
            }
            if (entry?.Robot == null || !entry.Owned)
            {
                return OperationResult<bool>.Failure(ModErrorCode.Conflict, "Only project-owned RobotKit robots permit name, tint, scale, and brain configuration.");
            }
            var name = Param(node, CreatorGraphParameters.Name).Trim();
            var tint = Param(node, CreatorGraphParameters.Tint).Trim();
            var scaleText = Param(node, CreatorGraphParameters.Scale).Trim();
            var brain = Param(node, CreatorGraphParameters.Brain).Trim();
            if (brain.Length == 0) brain = Param(node, CreatorGraphParameters.Value).Trim();
            if (name.Length > 64)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Robot name cannot exceed 64 characters.");
            }
            if (tint.Length > 0 && !TryRobotColor(tint, out _))
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Robot tint is not in the supported closed color set.");
            }
            if (scaleText.Length > 0 && (!TryFloat(scaleText, out var parsedScale) || parsedScale < 0.25f || parsedScale > 4f))
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Robot scale must be between 0.25 and 4.");
            }
            if (brain.Length > 0
                && !string.Equals(brain, "Dormant", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(brain, "Autonomous", StringComparison.OrdinalIgnoreCase))
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Robot brain must be Dormant or Autonomous.");
            }
            if (name.Length > 0)
            {
                var named = entry.Robot.SetName(name);
                if (!named.Succeeded) return named;
                entry.DisplayName = name;
            }
            if (tint.Length > 0)
            {
                TryRobotColor(tint, out var color);
                var tinted = entry.Robot.SetTint(color);
                if (!tinted.Succeeded) return tinted;
            }
            if (scaleText.Length > 0)
            {
                TryFloat(scaleText, out var scale);
                var scaled = entry.Robot.SetScale(scale);
                if (!scaled.Succeeded) return scaled;
            }
            if (brain.Length > 0)
            {
                var mode = string.Equals(brain, "Autonomous", StringComparison.OrdinalIgnoreCase)
                    ? RobotBrainMode.Autonomous
                    : RobotBrainMode.Dormant;
                var configured = entry.Robot.SetBrainMode(mode);
                if (!configured.Succeeded) return configured;
            }
            return OperationResult<bool>.Success(true);
        }

        private OperationResult<bool> ExecutePersonalityNode(CreatorGraphNode node)
        {
            var entry = ProjectRoster(node);
            var personaId = Param(node, CreatorGraphParameters.PersonaId);
            var persona = activeProject?.Personas.FirstOrDefault(item => string.Equals(item.Id, personaId, StringComparison.Ordinal));
            if (entry == null || persona == null) return OperationResult<bool>.Failure(ModErrorCode.NotFound, "Project personality target is missing.");
            if (string.IsNullOrWhiteSpace(persona.SystemFrame))
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Robot personality previews require a non-empty system frame.");
            }
            var lease = EnsureRobotEdit(entry);
            if (!lease.TryGetValue(out var edit))
            {
                return OperationResult<bool>.Failure(lease.ErrorCode, lease.ErrorMessage);
            }
            try
            {
                return edit.PreviewPersonality(new RobotPersonalityDraft(
                    persona.DisplayName,
                    persona.SystemFrame,
                    options.ChatTemperature));
            }
            catch (ArgumentException exception)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, exception.Message);
            }
        }

        private OperationResult<bool> ExecuteObjectiveNode(CreatorGraphNode node)
        {
            var entry = ProjectRoster(node);
            if (entry?.Robot == null || objectives == null) return OperationResult<bool>.Failure(ModErrorCode.Unavailable, "Project robot objectives are unavailable.");
            var objective = ParseObjective(Param(node, "objective"));
            if (objective == null) return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Project objective is invalid.");
            var result = objectives.SetObjective(entry.Robot, objective);
            return result.Succeeded
                ? OperationResult<bool>.Success(true)
                : OperationResult<bool>.Failure(result.ErrorCode, result.ErrorMessage);
        }

        private OperationResult<bool> ExecuteEmoteNode(CreatorGraphNode node)
        {
            var entry = ProjectRoster(node);
            return entry?.Robot == null
                ? OperationResult<bool>.Failure(ModErrorCode.Unavailable, "Project target does not expose emotes.")
                : entry.Robot.SetEmote(Param(node, CreatorGraphParameters.Value));
        }

        private OperationResult<bool> ExecuteConversationNode(CreatorGraphNode node)
        {
            var personaId = Param(node, CreatorGraphParameters.PersonaId);
            var persona = activeProject?.Personas.FirstOrDefault(item => string.Equals(item.Id, personaId, StringComparison.Ordinal));
            if (persona == null)
            {
                return OperationResult<bool>.Failure(ModErrorCode.NotFound, "Project conversation persona is missing.");
            }
            var result = BeginConversation(ProjectRoster(node), persona);
            return result.Succeeded
                ? OperationResult<bool>.Success(true)
                : OperationResult<bool>.Failure(result.ErrorCode, result.ErrorMessage);
        }

        private CreatorProjectEntity? ProjectEntity(CreatorGraphNode node)
        {
            var id = Param(node, CreatorGraphParameters.EntityId);
            return activeProject?.Entities.FirstOrDefault(entity => string.Equals(entity.Id, id, StringComparison.Ordinal));
        }

        private CreatorRosterEntry? ProjectRoster(CreatorGraphNode node)
        {
            var id = Param(node, CreatorGraphParameters.EntityId);
            if (projectEntities.TryGetValue(id, out var rosterId)) return FindRoster(rosterId);
            var bindingId = Param(node, CreatorGraphParameters.NativeBindingId);
            return projectBindings.TryGetValue(bindingId, out rosterId) ? FindRoster(rosterId) : null;
        }

        private static bool TryParseGraphTransform(string value, TransformState fallback, out TransformState transform)
        {
            var parts = (value ?? string.Empty).Split(',');
            if (parts.Length != 3 && parts.Length != 10)
            {
                transform = fallback;
                return false;
            }
            var numbers = new float[parts.Length];
            for (var index = 0; index < parts.Length; index++)
            {
                if (!TryFloat(parts[index], out numbers[index]))
                {
                    transform = fallback;
                    return false;
                }
            }
            try
            {
                transform = parts.Length == 3
                    ? new TransformState(new Vec3(numbers[0], numbers[1], numbers[2]), fallback.Rotation, fallback.Scale)
                    : new TransformState(
                        new Vec3(numbers[0], numbers[1], numbers[2]),
                        new Quat(numbers[3], numbers[4], numbers[5], numbers[6]),
                        new Vec3(numbers[7], numbers[8], numbers[9]));
                return true;
            }
            catch (ArgumentException)
            {
                transform = fallback;
                return false;
            }
        }

        private void DisposeGraphAudio()
        {
            for (var index = graphAudio.Count - 1; index >= 0; index--)
            {
                graphAudio[index].Stop();
                graphAudio[index].Dispose();
            }
            graphAudio.Clear();
        }
    }
}
