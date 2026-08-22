using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.CreatorTools.Shared
{
    internal sealed partial class CreatorWorkbench
    {
        private UiGraphViewport graphViewport = UiGraphViewport.Default;

        private UiNode BuildGraphCanvas(CreatorEventProject project)
        {
            var graphNodes = project.Nodes.Select(node => new UiGraphNode(
                node.Id,
                node.Kind.ToString(),
                GraphTitle(node.Kind),
                node.EditorPosition,
                Ports(node.Kind),
                GraphSubtitle(node),
                GraphTone(node)));
            var index = 0;
            var graphEdges = project.Edges.Select(edge => new UiGraphEdge(
                "edge-" + (index++).ToString(CultureInfo.InvariantCulture),
                edge.FromNodeId,
                edge.FromPort,
                edge.ToNodeId,
                edge.ToPort));
            return new UiGraphCanvas(
                "event-graph",
                graphNodes,
                graphEdges,
                SelectGraphNode,
                string.IsNullOrEmpty(selectedGraphNodeId) ? null : selectedGraphNodeId,
                viewport: graphViewport,
                height: 420f,
                nodeMoved: move => { MoveGraphNode(move); RefreshUi(); },
                connectionRequested: request => { ConnectGraph(request); RefreshUi(); },
                connectionRemoved: id => { RemoveGraphEdge(id); RefreshUi(); },
                viewportChanged: viewport => graphViewport = viewport);
        }

        private OperationResult<string> CreateProject()
        {
            StopProject(removeProjectEntities: true, removeProjectBindings: true);
            var stamp = DateTimeOffset.UtcNow;
            var start = new CreatorGraphNode("start", CreatorGraphNodeKind.ProjectStart, new Vec2(80f, 100f));
            var toast = new CreatorGraphNode(
                "welcome",
                CreatorGraphNodeKind.ShowToast,
                new Vec2(380f, 100f),
                new Dictionary<string, string> { [CreatorGraphParameters.Text] = "Creator event started." });
            activeProject = new CreatorEventProject(
                1,
                "project-" + stamp.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)
                    + "-" + Guid.NewGuid().ToString("N"),
                "New creator event",
                "A local visual event project.",
                options.ProjectScope,
                options.WorldId,
                ActiveSceneName(),
                stamp,
                nodes: new[] { start, toast },
                edges: new[] { new CreatorGraphEdge("start", "fired", "welcome", "in") },
                origin: options.ProjectScope == CreatorProjectScope.Global
                    ? CreatorProjectOrigin.PlayerAtRun
                    : CreatorProjectOrigin.World);
            confirmedNativeProjectId = string.Empty;
            graphViewport = UiGraphViewport.Default;
            selectedGraphNodeId = start.Id;
            LoadGraphNodeParameters(start);
            selectedProjectId = activeProject.Id;
            return OperationResult<string>.Success("Created a new unsaved event project.");
        }

        private OperationResult<string> SaveProject()
        {
            if (projects == null) return OperationResult<string>.Failure(ModErrorCode.Unavailable, "The project library is unavailable.");
            if (activeProject == null) return OperationResult<string>.Failure(ModErrorCode.NotFound, "Create or load a project first.");
            if (projectSaveTask != null) return OperationResult<string>.Failure(ModErrorCode.Conflict, "A project save is already running.");
            activeProject = RebuildProject(activeProject.Nodes, activeProject.Edges, DateTimeOffset.UtcNow);
            var validation = projects.Validate(activeProject);
            var error = validation.Issues.FirstOrDefault(issue => issue.Severity == CreatorProjectValidationSeverity.Error);
            if (error != null) return OperationResult<string>.Failure(ModErrorCode.InvalidArgument, error.Message);
            projectSaveTask = projects.SaveAsync(activeProject);
            return OperationResult<string>.Success("Saving project…");
        }

        private OperationResult<string> AddNode(CreatorGraphNodeKind kind)
        {
            if (activeProject == null) return OperationResult<string>.Failure(ModErrorCode.NotFound, "Create or load a project first.");
            if (activeProject.Nodes.Count >= UiGraphCanvas.MaximumNodes)
            {
                return OperationResult<string>.Failure(ModErrorCode.RateLimited, "The visual graph reached its node limit.");
            }
            var prefix = kind.ToString().ToLowerInvariant();
            var number = 1;
            while (activeProject.Nodes.Any(node => node.Id == prefix + "-" + number)) number++;
            var parameters = new Dictionary<string, string>();
            var entityId = activeProject.Entities.FirstOrDefault()?.Id ?? graphEntityId;
            var personaId = activeProject.Personas.FirstOrDefault()?.Id ?? graphPersonaId;
            if (kind == CreatorGraphNodeKind.InteractionTrigger) parameters[CreatorGraphParameters.Prompt] = "INTERACT";
            if (kind == CreatorGraphNodeKind.InteractionTrigger || kind == CreatorGraphNodeKind.PlayerEnteredRadius)
            {
                if (!string.IsNullOrWhiteSpace(entityId)) parameters[CreatorGraphParameters.EntityId] = entityId;
                parameters[CreatorGraphParameters.Radius] = "3";
            }
            if (kind == CreatorGraphNodeKind.EntityRemoved || kind == CreatorGraphNodeKind.RobotObjectiveState
                || (kind >= CreatorGraphNodeKind.SpawnContent && kind <= CreatorGraphNodeKind.BeginConversation))
            {
                if (!string.IsNullOrWhiteSpace(entityId)) parameters[CreatorGraphParameters.EntityId] = entityId;
            }
            if (kind == CreatorGraphNodeKind.Delay) parameters[CreatorGraphParameters.Seconds] = "1";
            if (kind == CreatorGraphNodeKind.StateCondition) parameters[CreatorGraphParameters.Value] = "entity.alive";
            if (kind == CreatorGraphNodeKind.Repeat) parameters[CreatorGraphParameters.Value] = "2";
            if (kind == CreatorGraphNodeKind.ShowToast) parameters[CreatorGraphParameters.Text] = "Creator event";
            if (kind == CreatorGraphNodeKind.PlayAudio) parameters[CreatorGraphParameters.CueId] = "creator-event";
            if (kind == CreatorGraphNodeKind.SetTransform) parameters[CreatorGraphParameters.Value] = "0,0,0";
            if (kind == CreatorGraphNodeKind.ConfigureRobot) parameters[CreatorGraphParameters.Brain] = "Dormant";
            if (kind == CreatorGraphNodeKind.SetRobotObjective) parameters[CreatorGraphParameters.Objective] = "IDLE";
            if (kind == CreatorGraphNodeKind.SetRobotEmote) parameters[CreatorGraphParameters.Value] = ":wave:";
            if (kind == CreatorGraphNodeKind.SetRobotPersonality || kind == CreatorGraphNodeKind.BeginConversation)
            {
                if (!string.IsNullOrWhiteSpace(personaId)) parameters[CreatorGraphParameters.PersonaId] = personaId;
            }
            var node = new CreatorGraphNode(
                prefix + "-" + number,
                kind,
                new Vec2(140f + activeProject.Nodes.Count * 28f, 160f + activeProject.Nodes.Count * 18f),
                parameters);
            activeProject = RebuildProject(activeProject.Nodes.Concat(new[] { node }), activeProject.Edges);
            selectedGraphNodeId = node.Id;
            LoadGraphNodeParameters(node);
            return OperationResult<string>.Success("Added " + GraphTitle(kind) + ". Choose an output, then an input, to connect it.");
        }

        private OperationResult<string> RemoveSelectedNode()
        {
            if (activeProject == null || SelectedGraphNode() == null)
            {
                return OperationResult<string>.Failure(ModErrorCode.NotFound, "Choose a graph node first.");
            }
            var id = selectedGraphNodeId;
            activeProject = RebuildProject(
                activeProject.Nodes.Where(node => node.Id != id),
                activeProject.Edges.Where(edge => edge.FromNodeId != id && edge.ToNodeId != id));
            selectedGraphNodeId = activeProject.Nodes.FirstOrDefault()?.Id ?? string.Empty;
            LoadGraphNodeParameters(SelectedGraphNode());
            return OperationResult<string>.Success("Removed graph node " + id + ".");
        }

        private OperationResult<string> FireSelectedManual()
        {
            if (runner == null) return OperationResult<string>.Failure(ModErrorCode.InvalidState, "Run the project first.");
            var result = runner.FireManual(selectedGraphNodeId);
            return result.Succeeded
                ? OperationResult<string>.Success("Manual event fired.")
                : OperationResult<string>.Failure(result.ErrorCode, result.ErrorMessage);
        }

        private void MoveGraphNode(UiGraphNodeMove move)
        {
            if (activeProject == null) return;
            var nodes = activeProject.Nodes.Select(node => node.Id == move.NodeId
                ? new CreatorGraphNode(node.Id, node.Kind, move.Position, node.Parameters)
                : node);
            activeProject = RebuildProject(nodes, activeProject.Edges);
        }

        private void ConnectGraph(UiGraphConnectionRequest request)
        {
            if (activeProject == null || activeProject.Edges.Count >= UiGraphCanvas.MaximumEdges) return;
            if (activeProject.Edges.Any(edge =>
                edge.FromNodeId == request.SourceNodeId && edge.FromPort == request.SourcePortId
                && edge.ToNodeId == request.TargetNodeId && edge.ToPort == request.TargetPortId)) return;
            var edge = new CreatorGraphEdge(
                request.SourceNodeId,
                request.SourcePortId,
                request.TargetNodeId,
                request.TargetPortId);
            activeProject = RebuildProject(activeProject.Nodes, activeProject.Edges.Concat(new[] { edge }));
        }

        private void RemoveGraphEdge(string edgeId)
        {
            if (activeProject == null || !edgeId.StartsWith("edge-", StringComparison.Ordinal)
                || !int.TryParse(edgeId.Substring(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                || index < 0 || index >= activeProject.Edges.Count) return;
            activeProject = RebuildProject(activeProject.Nodes, activeProject.Edges.Where((edge, at) => at != index));
        }

        private CreatorEventProject RebuildProject(
            IEnumerable<CreatorGraphNode> nodes,
            IEnumerable<CreatorGraphEdge> edges,
            DateTimeOffset? modified = null,
            IEnumerable<CreatorProjectEntity>? entities = null,
            IEnumerable<CreatorNativeBinding>? nativeBindings = null,
            IEnumerable<CreatorPersona>? personas = null) =>
            new CreatorEventProject(
                activeProject!.SchemaVersion,
                activeProject.Id,
                activeProject.DisplayName,
                activeProject.Description,
                activeProject.Scope,
                activeProject.WorldId,
                activeProject.SceneName,
                modified ?? activeProject.ModifiedAtUtc,
                entities ?? activeProject.Entities,
                nativeBindings ?? activeProject.NativeBindings,
                personas ?? activeProject.Personas,
                nodes,
                edges,
                activeProject.Origin);

        private CreatorGraphNode? SelectedGraphNode() =>
            activeProject?.Nodes.FirstOrDefault(node => node.Id == selectedGraphNodeId);

        private string ActiveSceneName() =>
            context.Scenes.TryGetActive(out var scene) && scene != null ? scene.Name : string.Empty;

        private static IReadOnlyList<UiGraphPort> Ports(CreatorGraphNodeKind kind)
        {
            if (kind <= CreatorGraphNodeKind.ConversationDecision)
            {
                return new[] { new UiGraphPort("fired", "Fired", UiGraphPortDirection.Output) };
            }
            if (kind == CreatorGraphNodeKind.Delay)
            {
                return new[]
                {
                    new UiGraphPort("in", "In", UiGraphPortDirection.Input),
                    new UiGraphPort("done", "Done", UiGraphPortDirection.Output)
                };
            }
            if (kind == CreatorGraphNodeKind.StateCondition)
            {
                return new[]
                {
                    new UiGraphPort("in", "In", UiGraphPortDirection.Input),
                    new UiGraphPort("true", "True", UiGraphPortDirection.Output),
                    new UiGraphPort("false", "False", UiGraphPortDirection.Output)
                };
            }
            if (kind == CreatorGraphNodeKind.Repeat)
            {
                return new[]
                {
                    new UiGraphPort("in", "In", UiGraphPortDirection.Input),
                    new UiGraphPort("each", "Each", UiGraphPortDirection.Output),
                    new UiGraphPort("done", "Done", UiGraphPortDirection.Output)
                };
            }
            return new[]
            {
                new UiGraphPort("in", "In", UiGraphPortDirection.Input),
                new UiGraphPort("success", "Success", UiGraphPortDirection.Output),
                new UiGraphPort("failure", "Failure", UiGraphPortDirection.Output)
            };
        }

        private static string GraphTitle(CreatorGraphNodeKind kind) =>
            string.Concat(kind.ToString().Select((character, index) => index > 0 && char.IsUpper(character) ? " " + character : character.ToString()));

        private string GraphSubtitle(CreatorGraphNode node)
        {
            var parameters = string.Join("  •  ", node.Parameters.Select(pair => pair.Key + "=" + pair.Value).Take(3));
            var problem = GraphNodeContentProblem(node);
            if (string.IsNullOrEmpty(problem)) problem = GraphTargetSupportProblem(node);
            return string.IsNullOrEmpty(problem)
                ? parameters
                : "UNRESOLVED: " + problem + (parameters.Length == 0 ? string.Empty : "  •  " + parameters);
        }

        private UiTone GraphTone(CreatorGraphNode node) =>
            !string.IsNullOrEmpty(GraphNodeContentProblem(node)) || !string.IsNullOrEmpty(GraphTargetSupportProblem(node)) ? UiTone.Danger
                : node.Kind <= CreatorGraphNodeKind.ConversationDecision ? UiTone.Success
                : node.Kind == CreatorGraphNodeKind.Delay || node.Kind == CreatorGraphNodeKind.Repeat ? UiTone.Warning
                : UiTone.Neutral;
    }
}
