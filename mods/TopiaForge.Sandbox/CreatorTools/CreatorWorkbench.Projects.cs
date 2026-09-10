using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.CreatorTools.Shared
{
    internal sealed partial class CreatorWorkbench
    {
        private readonly Dictionary<string, RobotObjectiveState> objectiveStates =
            new Dictionary<string, RobotObjectiveState>(StringComparer.Ordinal);
        private readonly HashSet<string> enteredRadiusNodes = new HashSet<string>(StringComparer.Ordinal);
        private Task<OperationResult<CreatorProjectSummary>>? projectSaveTask;

        private void BeginProjectList()
        {
            if (projects != null && projectListTask == null) projectListTask = projects.ListAsync();
        }

        private void PollProjectTasks()
        {
            PollProjectDeletion();
            if (projectListTask?.IsCompleted == true)
            {
                var result = projectListTask.GetAwaiter().GetResult();
                projectListTask = null;
                if (result.TryGetValue(out var snapshot))
                {
                    projectSummaries = snapshot.Projects;
                    if (projectSummaries.Count > 0 && string.IsNullOrEmpty(selectedProjectId))
                    {
                        selectedProjectId = projectSummaries[0].Id;
                    }
                }
                else status = result.ErrorMessage;
                RefreshUi();
            }
            if (projectLoadTask?.IsCompleted == true)
            {
                var result = projectLoadTask.GetAwaiter().GetResult();
                projectLoadTask = null;
                if (result.TryGetValue(out var project))
                {
                    var stopped = StopProject(removeProjectEntities: true, removeProjectBindings: true);
                    if (!stopped.Succeeded)
                    {
                        context.Ui.ShowToast(stopped.ErrorMessage, UiTone.Danger);
                        RefreshUi();
                        return;
                    }
                    activeProject = project;
                    confirmedNativeProjectId = string.Empty;
                    graphViewport = UiGraphViewport.Default;
                    selectedGraphNodeId = project.Nodes.FirstOrDefault()?.Id ?? string.Empty;
                    LoadGraphNodeParameters(project.Nodes.FirstOrDefault());
                    status = "Loaded project " + project.DisplayName + ".";
                }
                else status = result.ErrorMessage;
                RefreshUi();
            }
            if (projectSaveTask?.IsCompleted == true)
            {
                var result = projectSaveTask.GetAwaiter().GetResult();
                projectSaveTask = null;
                status = result.TryGetValue(out var summary)
                    ? "Saved project " + summary.DisplayName + "."
                    : result.ErrorMessage;
                BeginProjectList();
                RefreshUi();
            }
        }

        private OperationResult<string> LoadSelectedProject()
        {
            if (projects == null) return OperationResult<string>.Failure(ModErrorCode.Unavailable, "The project library is unavailable.");
            if (string.IsNullOrEmpty(selectedProjectId)) return OperationResult<string>.Failure(ModErrorCode.NotFound, "Choose a project first.");
            if (projectLoadTask != null) return OperationResult<string>.Failure(ModErrorCode.Conflict, "A project is already loading.");
            projectLoadTask = projects.LoadAsync(selectedProjectId);
            status = "Loading project…";
            return OperationResult<string>.Success(status);
        }

        private OperationResult<string> RunProject()
        {
            var allowed = EnsureMutationAllowed();
            if (!allowed.Succeeded) return OperationResult<string>.Failure(allowed.ErrorCode, allowed.ErrorMessage);
            if (activeProject == null) return OperationResult<string>.Failure(ModErrorCode.NotFound, "Load or create a project first.");
            if (projects != null)
            {
                var validation = projects.Validate(activeProject);
                var error = validation.Issues.FirstOrDefault(issue => issue.Severity == CreatorProjectValidationSeverity.Error);
                if (error != null) return OperationResult<string>.Failure(ModErrorCode.InvalidArgument, error.Message);
            }
            if (activeProject.Scope != options.ProjectScope)
            {
                return OperationResult<string>.Failure(ModErrorCode.Conflict, "This project belongs to the " + activeProject.Scope + " host.");
            }
            if (activeProject.Scope == CreatorProjectScope.Sandbox
                && !string.Equals(activeProject.WorldId, options.WorldId, StringComparison.OrdinalIgnoreCase))
            {
                return OperationResult<string>.Failure(ModErrorCode.Conflict, "This project targets managed world " + activeProject.WorldId + ".");
            }
            if (!string.IsNullOrEmpty(activeProject.SceneName)
                && !string.Equals(activeProject.SceneName, ActiveSceneName(), StringComparison.Ordinal))
            {
                return OperationResult<string>.Failure(ModErrorCode.Conflict, "This project targets scene " + activeProject.SceneName + ".");
            }
            foreach (var node in activeProject.Nodes)
            {
                var supportProblem = GraphTargetSupportProblem(node);
                if (!string.IsNullOrEmpty(supportProblem))
                {
                    return OperationResult<string>.Failure(
                        ModErrorCode.Conflict,
                        "Graph node '" + node.Id + "' cannot run: " + supportProblem + ".");
                }
            }
            content.RefreshCatalog();
            foreach (var entity in activeProject.Entities)
            {
                var problem = ProjectContentProblem(entity);
                if (!string.IsNullOrEmpty(problem))
                {
                    return OperationResult<string>.Failure(
                        ModErrorCode.NotFound,
                        "Project entity '" + entity.DisplayName + "' cannot run: " + problem + ".");
                }
            }
            if (activeProject.NativeBindings.Count > 0
                && (!string.Equals(confirmedNativeProjectId, activeProject.Id, StringComparison.Ordinal)
                    || activeProject.NativeBindings.Any(binding => !projectBindings.ContainsKey(binding.Id))))
            {
                return OperationResult<string>.Failure(
                    ModErrorCode.Conflict,
                    "Resolve and explicitly confirm this project's native scene bindings before running.");
            }

            var stopped = StopProject(removeProjectEntities: true, removeProjectBindings: false);
            if (!stopped.Succeeded) return stopped;
            projectRunOrigin = activeProject.Origin == CreatorProjectOrigin.PlayerAtRun
                && context.LocalPlayer.TryGetSnapshot(out var runPlayer) && runPlayer != null
                ? runPlayer.Position
                : Vec3.Zero;
            foreach (var entity in activeProject.Entities.Where(item => item.SpawnOnStart))
            {
                var spawned = SpawnProjectEntity(entity);
                if (!spawned.Succeeded)
                {
                    return FailProjectStart(spawned.ErrorCode, spawned.ErrorMessage);
                }
            }
            runner = new CreatorEventGraphRunner(activeProject, this);
            var interactions = RegisterProjectInteractions();
            if (!interactions.Succeeded)
            {
                return FailProjectStart(interactions.ErrorCode, interactions.ErrorMessage);
            }
            var started = runner.Start();
            if (!started.Succeeded)
            {
                var errorCode = started.ErrorCode;
                var errorMessage = started.ErrorMessage;
                var failed = FailProjectStart(errorCode, errorMessage);
                status = failed.ErrorMessage;
                RefreshUi();
                return failed;
            }

            status = "Running " + activeProject.DisplayName + ".";
            RefreshUi();
            return OperationResult<string>.Success(status);
        }

        private OperationResult<string> StopProject(bool removeProjectEntities, bool removeProjectBindings = false)
        {
            var cleanup = OperationResult<bool>.Success(true);
            void Clean(Action action) { var attempted = TryCleanup(action); cleanup = MergeCleanup(cleanup, attempted); }
            var stoppedRunner = runner;
            runner = null;
            Clean(() => stoppedRunner?.Dispose());
            if (graphConversationOwned) Clean(() => EndConversation());
            Clean(DisposeGraphAudio);
            Clean(() => DisposeProjectInteractions());
            objectiveStates.Clear();
            enteredRadiusNodes.Clear();
            projectRunOrigin = Vec3.Zero;
            if (removeProjectEntities)
            {
                foreach (var rosterId in projectEntities.Values.ToArray())
                {
                    var entry = FindRoster(rosterId);
                    if (entry != null) cleanup = MergeCleanup(cleanup, RemoveOwnedEntry(entry, fireRemoved: false));
                }
                projectEntities.Clear();
            }
            foreach (var rosterId in projectBindings.Values.ToArray())
            {
                var entry = FindRoster(rosterId);
                if (entry == null) continue;
                if (removeProjectBindings)
                {
                    roster.Remove(entry);
                    Clean(entry.Dispose);
                }
                else
                {
                    Clean(() => entry.NativeEdit?.Dispose());
                    Clean(() => entry.RobotEdit?.Dispose());
                    entry.NativeEdit = null;
                    entry.RobotEdit = null;
                    entry.NativeHidden = false;
                }
            }
            if (removeProjectBindings)
            {
                projectBindings.Clear();
                confirmedNativeProjectId = string.Empty;
            }
            status = cleanup.Succeeded ? "Event project stopped."
                : "Event project stopped with cleanup problems: " + cleanup.ErrorMessage;
            RefreshUi();
            return cleanup.Succeeded ? OperationResult<string>.Success(status)
                : OperationResult<string>.Failure(cleanup.ErrorCode, status);
        }

        private OperationResult<string> FailProjectStart(ModErrorCode error, string message)
        {
            var cleanup = StopProject(removeProjectEntities: true);
            return OperationResult<string>.Failure(error, message + (cleanup.Succeeded ? string.Empty : " " + cleanup.ErrorMessage));
        }

        private OperationResult<string> SpawnProjectEntity(CreatorProjectEntity definition)
        {
            if (projectEntities.TryGetValue(definition.Id, out var existing) && FindRoster(existing)?.IsAlive == true)
            {
                return OperationResult<string>.Success(definition.DisplayName + " is already spawned.");
            }
            var capacity = EnsureOwnedCapacity();
            if (!capacity.Succeeded)
            {
                return OperationResult<string>.Failure(capacity.ErrorCode, capacity.ErrorMessage);
            }
            var authored = definition.Transform;
            var transform = new TransformState(authored.Position + projectRunOrigin, authored.Rotation, authored.Scale);
            if (TryRobotKitProjectContent(definition.ContentId, out var robotTypeId))
            {
                return SpawnProjectRobot(definition, transform, robotTypeId);
            }
            var result = creatorSession!.Spawn(new CreatorSpawnRequest(definition.ContentId, transform));
            if (!result.TryGetValue(out var handle))
            {
                return OperationResult<string>.Failure(result.ErrorCode, result.ErrorMessage);
            }
            var entry = new CreatorRosterEntry(
                "project:" + definition.Id,
                definition.DisplayName,
                handle.Descriptor.Kind,
                owned: true,
                cleanup: handle)
            {
                Spawn = handle,
                SourceId = definition.ContentId
            };
            if (robots.TryGetRobot(handle.Entity, out var agent)) entry.Robot = agent;
            roster.Add(entry);
            projectEntities[definition.Id] = entry.Id;
            if (runner != null)
            {
                var interactions = RegisterProjectInteractionsFor(definition.Id);
                if (!interactions.Succeeded)
                {
                    var cleanup = RemoveOwnedEntry(entry, fireRemoved: false);
                    return WithCleanupFailure(interactions.ErrorCode, interactions.ErrorMessage, cleanup);
                }
            }
            return OperationResult<string>.Success(definition.DisplayName + " spawned.");
        }


    }
}
