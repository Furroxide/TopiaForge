using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.CreatorTools.Shared
{
    internal sealed partial class CreatorWorkbench
    {
        private string graphNodeKind = CreatorGraphNodeKind.ManualTrigger.ToString();
        private string graphEntityId = string.Empty;
        private string graphNativeBindingId = string.Empty;
        private string graphPersonaId = string.Empty;
        private string graphValue = string.Empty;
        private string graphText = string.Empty;
        private string graphSeconds = string.Empty;
        private string graphRadius = string.Empty;
        private string graphCueId = string.Empty;
        private string graphObjective = string.Empty;
        private string graphRobotName = string.Empty;
        private string graphRobotTint = string.Empty;
        private string graphRobotScale = string.Empty;
        private string graphRobotBrain = string.Empty;
        private string personaId = "creator-persona";
        private string personaReplyGuidance = "Reply briefly and stay in character.";
        private string nativeSearchRadius = "3";
        private bool projectEntitySpawnOnStart = true;

        private UiNode BuildProjectAuthoringControls()
        {
            var kinds = Enum.GetValues(typeof(CreatorGraphNodeKind))
                .Cast<CreatorGraphNodeKind>()
                .Where(kind => kind != CreatorGraphNodeKind.ProjectStart)
                .Select(kind => new UiChoice(kind.ToString(), GraphTitle(kind)))
                .ToArray();
            var node = SelectedGraphNode();
            var contentProblems = activeProject!.Entities
                .Select(entity => new { Entity = entity, Problem = ProjectContentProblem(entity) })
                .Where(item => !string.IsNullOrEmpty(item.Problem))
                .Select(item => item.Entity.DisplayName + ": " + item.Problem)
                .ToArray();
            return new UiColumn(
                new UiText("PROJECT CONTENT", UiTextStyle.Heading),
                new UiText(
                    activeProject!.Entities.Count + " entities  •  "
                        + activeProject.Personas.Count + " personas  •  "
                        + activeProject.NativeBindings.Count + " native recipes",
                    UiTextStyle.Caption),
                new UiText(
                    contentProblems.Length == 0 ? "All declared project content currently resolves." : string.Join("\n", contentProblems),
                    UiTextStyle.Caption,
                    contentProblems.Length == 0 ? UiTone.Success : UiTone.Danger),
                new UiToggle("project-spawn-on-start", "Spawn selected content when the event starts", projectEntitySpawnOnStart, value => projectEntitySpawnOnStart = value),
                new UiRow(
                    new UiButton("project-add-content", "Add catalog selection", () => Execute(AddSelectedCatalogToProject), UiButtonStyle.Secondary, FindCatalog(selectedCatalogId) != null),
                    new UiTextInput("project-persona-id", "Persona id", personaId, value => personaId = value, maximumLength: 64),
                    new UiButton("project-save-persona", "Save personality draft", () => Execute(SavePersonaToProject), UiButtonStyle.Secondary)),
                new UiRow(
                    new UiTextInput("project-native-radius", "Native radius", nativeSearchRadius, value => nativeSearchRadius = value, maximumLength: 16),
                    new UiButton("project-capture-native", "Capture native recipe", () => Execute(CaptureSelectedNativeBinding), UiButtonStyle.Secondary,
                        SelectedRoster()?.NativeTarget != null || SelectedRoster()?.RobotTarget?.IsNativeSceneObject == true)),
                new UiTextInput("project-persona-reply", "Persona reply guidance", personaReplyGuidance, value => personaReplyGuidance = value, maximumLength: 2000),
                new UiText("GRAPH NODE PALETTE", UiTextStyle.Heading),
                new UiRow(
                    new UiDropdown("graph-kind", "Node kind", kinds, graphNodeKind, value => graphNodeKind = value),
                    new UiButton("graph-add-kind", "Add node", () => Execute(AddSelectedNodeKind), UiButtonStyle.Secondary)),
                new UiRow(
                    new UiButton("graph-fire-manual", "Fire selected", () => Execute(FireSelectedManual), enabled: node?.Kind == CreatorGraphNodeKind.ManualTrigger),
                    new UiButton("graph-remove-selected", "Remove node", () => Execute(RemoveSelectedNode), UiButtonStyle.Danger, node != null)),
                new UiText(node == null ? "Select a node to edit its bounded parameters." : "SELECTED: " + GraphTitle(node.Kind), UiTextStyle.Caption),
                new UiRow(
                    new UiTextInput("graph-entity", "Entity id", graphEntityId, value => graphEntityId = value, maximumLength: 64, enabled: node != null),
                    new UiTextInput("graph-native", "Native binding id", graphNativeBindingId, value => graphNativeBindingId = value, maximumLength: 64, enabled: node != null),
                    new UiTextInput("graph-persona", "Persona id", graphPersonaId, value => graphPersonaId = value, maximumLength: 64, enabled: node != null)),
                new UiRow(
                    new UiTextInput("graph-value", "Value / emote", graphValue, value => graphValue = value, maximumLength: 1024, enabled: node != null),
                    new UiTextInput("graph-text", "Text / prompt", graphText, value => graphText = value, maximumLength: 1024, enabled: node != null)),
                new UiRow(
                    new UiTextInput("graph-seconds", "Seconds", graphSeconds, value => graphSeconds = value, maximumLength: 16, enabled: node != null),
                    new UiTextInput("graph-radius", "Radius", graphRadius, value => graphRadius = value, maximumLength: 16, enabled: node != null)),
                new UiRow(
                    new UiTextInput("graph-objective", "Objective", graphObjective, value => graphObjective = value, maximumLength: 128, enabled: node != null),
                    new UiTextInput("graph-cue", "Audio cue id", graphCueId, value => graphCueId = value, maximumLength: 128, enabled: node != null)),
                new UiRow(
                    new UiTextInput("graph-robot-name", "Robot name", graphRobotName, value => graphRobotName = value, maximumLength: 64, enabled: node != null),
                    new UiTextInput("graph-robot-tint", "Robot tint", graphRobotTint, value => graphRobotTint = value, maximumLength: 16, enabled: node != null)),
                new UiRow(
                    new UiTextInput("graph-robot-scale", "Robot scale", graphRobotScale, value => graphRobotScale = value, maximumLength: 16, enabled: node != null),
                    new UiTextInput("graph-robot-brain", "Robot brain", graphRobotBrain, value => graphRobotBrain = value, maximumLength: 16, enabled: node != null),
                    new UiButton("graph-apply-parameters", "Apply parameters", () => Execute(ApplySelectedNodeParameters), enabled: node != null)));
        }

        private void SelectGraphNode(string? id)
        {
            selectedGraphNodeId = id ?? string.Empty;
            LoadGraphNodeParameters(SelectedGraphNode());
            RefreshUi();
        }

        private void LoadGraphNodeParameters(CreatorGraphNode? node)
        {
            graphEntityId = Value(node, CreatorGraphParameters.EntityId);
            graphNativeBindingId = Value(node, CreatorGraphParameters.NativeBindingId);
            graphPersonaId = Value(node, CreatorGraphParameters.PersonaId);
            graphValue = Value(node, CreatorGraphParameters.Value);
            graphText = Value(node, CreatorGraphParameters.Text);
            if (string.IsNullOrEmpty(graphText)) graphText = Value(node, CreatorGraphParameters.Prompt);
            graphSeconds = Value(node, CreatorGraphParameters.Seconds);
            graphRadius = Value(node, CreatorGraphParameters.Radius);
            graphCueId = Value(node, CreatorGraphParameters.CueId);
            graphObjective = Value(node, CreatorGraphParameters.Objective);
            graphRobotName = Value(node, CreatorGraphParameters.Name);
            graphRobotTint = Value(node, CreatorGraphParameters.Tint);
            graphRobotScale = Value(node, CreatorGraphParameters.Scale);
            graphRobotBrain = Value(node, CreatorGraphParameters.Brain);
        }

        private OperationResult<string> AddSelectedNodeKind()
        {
            return Enum.TryParse(graphNodeKind, out CreatorGraphNodeKind kind)
                ? AddNode(kind)
                : OperationResult<string>.Failure(ModErrorCode.InvalidArgument, "Choose a supported graph node kind.");
        }

        private OperationResult<string> ApplySelectedNodeParameters()
        {
            var node = SelectedGraphNode();
            if (activeProject == null || node == null) return OperationResult<string>.Failure(ModErrorCode.NotFound, "Choose a graph node first.");
            var values = new Dictionary<string, string>(node.Parameters, StringComparer.Ordinal);
            Put(values, CreatorGraphParameters.EntityId, graphEntityId);
            Put(values, CreatorGraphParameters.NativeBindingId, graphNativeBindingId);
            Put(values, CreatorGraphParameters.PersonaId, graphPersonaId);
            Put(values, CreatorGraphParameters.Value, graphValue);
            Put(values, CreatorGraphParameters.Text, graphText);
            Put(values, CreatorGraphParameters.Prompt, graphText);
            Put(values, CreatorGraphParameters.Seconds, graphSeconds);
            Put(values, CreatorGraphParameters.Radius, graphRadius);
            Put(values, CreatorGraphParameters.CueId, graphCueId);
            Put(values, CreatorGraphParameters.Objective, graphObjective);
            Put(values, CreatorGraphParameters.Name, graphRobotName);
            Put(values, CreatorGraphParameters.Tint, graphRobotTint);
            Put(values, CreatorGraphParameters.Scale, graphRobotScale);
            Put(values, CreatorGraphParameters.Brain, graphRobotBrain);
            var replacement = new CreatorGraphNode(node.Id, node.Kind, node.EditorPosition, values);
            activeProject = RebuildProject(activeProject.Nodes.Select(item => item.Id == node.Id ? replacement : item), activeProject.Edges);
            return OperationResult<string>.Success("Updated " + GraphTitle(node.Kind) + " parameters.");
        }

        private OperationResult<string> AddSelectedCatalogToProject()
        {
            if (activeProject == null) return OperationResult<string>.Failure(ModErrorCode.NotFound, "Create or load a project first.");
            var selected = FindCatalog(selectedCatalogId);
            if (selected == null)
            {
                return OperationResult<string>.Failure(ModErrorCode.InvalidArgument, "Choose a catalog entry first.");
            }
            var aimed = AimTransform();
            if (!aimed.TryGetValue(out var transform)) return OperationResult<string>.Failure(aimed.ErrorCode, aimed.ErrorMessage);
            if (activeProject.Origin == CreatorProjectOrigin.PlayerAtRun
                && context.LocalPlayer.TryGetSnapshot(out var player) && player != null)
            {
                transform = new TransformState(transform.Position - player.Position, transform.Rotation, transform.Scale);
            }
            var number = 1;
            while (activeProject.Entities.Any(item => item.Id == "entity-" + number)) number++;
            var descriptor = selected.IsRobotKit
                ? null
                : content.Catalog.Entries.FirstOrDefault(item => string.Equals(item.ContentId, selected.SourceId, StringComparison.OrdinalIgnoreCase));
            if (!selected.IsRobotKit && descriptor == null)
            {
                return OperationResult<string>.Failure(ModErrorCode.NotFound, "The selected content source is no longer available.");
            }
            var entity = new CreatorProjectEntity(
                "entity-" + number,
                selected.DisplayName,
                selected.IsRobotKit ? RobotKitProjectContentPrefix + selected.SourceId : descriptor!.ContentId,
                selected.IsRobotKit ? CurrentRobotKitSourceVersion() : descriptor!.SourceVersion,
                transform,
                projectEntitySpawnOnStart);
            activeProject = RebuildProject(activeProject.Nodes, activeProject.Edges, entities: activeProject.Entities.Concat(new[] { entity }));
            graphEntityId = entity.Id;
            return OperationResult<string>.Success("Added project entity " + entity.DisplayName + ".");
        }

        private OperationResult<string> SavePersonaToProject()
        {
            if (activeProject == null) return OperationResult<string>.Failure(ModErrorCode.NotFound, "Create or load a project first.");
            var id = personaId.Trim();
            if (!IsLocalId(id)) return OperationResult<string>.Failure(ModErrorCode.InvalidArgument, "Persona id may use letters, digits, dots, underscores, and hyphens only.");
            var persona = new CreatorPersona(
                id,
                string.IsNullOrWhiteSpace(personaName) ? "Creator persona" : personaName.Trim(),
                personaInstructions.Trim(),
                personaReplyGuidance.Trim());
            activeProject = RebuildProject(
                activeProject.Nodes,
                activeProject.Edges,
                personas: activeProject.Personas.Where(item => item.Id != id).Concat(new[] { persona }));
            graphPersonaId = id;
            return OperationResult<string>.Success("Saved project persona " + persona.DisplayName + ".");
        }

        private OperationResult<string> CaptureSelectedNativeBinding()
        {
            if (activeProject == null) return OperationResult<string>.Failure(ModErrorCode.NotFound, "Create or load a project first.");
            var selected = SelectedRoster();
            var nativeTarget = selected?.NativeTarget;
            var robotTarget = selected?.RobotTarget;
            if (nativeTarget == null && robotTarget?.IsNativeSceneObject != true)
            {
                return OperationResult<string>.Failure(ModErrorCode.Unavailable, "Choose a provider-approved native scene target.");
            }
            if (!TryFloat(nativeSearchRadius, out var radius) || radius <= 0f || radius > 50f)
            {
                return OperationResult<string>.Failure(ModErrorCode.InvalidArgument, "Native search radius must be greater than zero and no more than 50.");
            }
            TransformState transform;
            if (nativeTarget != null)
            {
                if (string.IsNullOrWhiteSpace(nativeTarget.AdapterId)
                    || !context.Entities.TryGetTransform(nativeTarget.Entity, out transform))
                {
                    return OperationResult<string>.Failure(ModErrorCode.Unavailable, "The native target transform or adapter identity cannot be read.");
                }
            }
            else if (robotTarget == null
                || !string.Equals(robotTarget.SceneName, ActiveSceneName(), StringComparison.Ordinal)
                || !robotTarget.TryGetTransform(out transform))
            {
                return OperationResult<string>.Failure(ModErrorCode.Conflict, "The native robot is not in the exact active scene.");
            }
            var number = 1;
            while (activeProject.NativeBindings.Any(item => item.Id == "native-" + number)) number++;
            var displayName = nativeTarget?.DisplayName ?? robotTarget!.DisplayName;
            var adapterId = nativeTarget?.AdapterId ?? RobotKitNativeAdapterId;
            var nameFragment = displayName.Length > 128 ? displayName.Substring(0, 128) : displayName;
            var binding = new CreatorNativeBinding(
                "native-" + number,
                displayName,
                ActiveSceneName(),
                nameFragment,
                transform.Position,
                radius,
                adapterId);
            activeProject = RebuildProject(
                activeProject.Nodes,
                activeProject.Edges,
                nativeBindings: activeProject.NativeBindings.Concat(new[] { binding }));
            graphNativeBindingId = binding.Id;
            ClearResolvedProjectBindings();
            return OperationResult<string>.Success("Captured native binding recipe " + binding.DisplayName + ".");
        }

        private static string Value(CreatorGraphNode? node, string key) =>
            node != null && node.Parameters.TryGetValue(key, out var value) ? value : string.Empty;

        private static void Put(IDictionary<string, string> values, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) values.Remove(key);
            else values[key] = value.Trim();
        }

        private static bool IsLocalId(string value)
        {
            if (value.Length < 1 || value.Length > 64) return false;
            return value.All(character => char.IsLetterOrDigit(character) || character == '.' || character == '_' || character == '-');
        }
    }
}
