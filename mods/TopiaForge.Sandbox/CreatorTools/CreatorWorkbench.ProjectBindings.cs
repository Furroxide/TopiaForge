using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.CreatorTools.Shared
{
    internal sealed partial class CreatorWorkbench
    {
        private void ConfirmNativeBindings()
        {
            if (activeProject == null || activeProject.NativeBindings.Count == 0) return;
            if (confirmation?.IsOpen == true) return;
            var listed = string.Join("\n", activeProject.NativeBindings.Select(binding => "• " + binding.DisplayName));
            var shown = context.Ui.ShowModal(
                new UiModalRequest(
                    "RESOLVE NATIVE SCENE TARGETS?",
                    "These bindings are scene-specific and temporary. Confirm each resolved target before the event can run:\n" + listed,
                    "RESOLVE & CONFIRM",
                    destructive: false),
                confirmed =>
                {
                    confirmation = null;
                    if (!confirmed) return;
                    var result = ResolveNativeBindings();
                    status = result.Succeeded ? result.Value ?? "Bindings confirmed." : result.ErrorMessage;
                    if (!result.Succeeded) context.Ui.ShowToast(status, UiTone.Danger);
                    RefreshUi();
                });
            shown.TryGetValue(out confirmation);
        }

        private OperationResult<string> ResolveNativeBindings()
        {
            if (activeProject == null || creatorSession == null)
            {
                return OperationResult<string>.Failure(ModErrorCode.InvalidState, "No active project session.");
            }
            ClearResolvedProjectBindings();
            var staged = new List<KeyValuePair<string, CreatorRosterEntry>>();
            OperationResult<string> Fail(ModErrorCode code, string message)
            {
                foreach (var item in staged) item.Value.Dispose();
                return OperationResult<string>.Failure(code, message);
            }
            if (activeProject.Scope != options.ProjectScope)
            {
                return Fail(ModErrorCode.Conflict, "This project belongs to the " + activeProject.Scope + " host.");
            }
            if (activeProject.Scope == CreatorProjectScope.Sandbox
                && !string.Equals(activeProject.WorldId, options.WorldId, StringComparison.OrdinalIgnoreCase))
            {
                return Fail(ModErrorCode.Conflict, "This project targets managed world " + activeProject.WorldId + ".");
            }
            if (!string.IsNullOrEmpty(activeProject.SceneName)
                && !string.Equals(activeProject.SceneName, ActiveSceneName(), StringComparison.Ordinal))
            {
                return Fail(ModErrorCode.Conflict, "This project targets scene " + activeProject.SceneName + ".");
            }
            foreach (var binding in activeProject.NativeBindings)
            {
                if (!string.Equals(binding.SceneName, ActiveSceneName(), StringComparison.Ordinal))
                {
                    return Fail(ModErrorCode.Conflict, "Binding '" + binding.DisplayName + "' targets another scene.");
                }
                if (string.Equals(binding.AdapterId, RobotKitNativeAdapterId, StringComparison.Ordinal))
                {
                    if (robotEditor?.IsAvailable != true)
                    {
                        return Fail(ModErrorCode.Unavailable, "RobotKit native editing is unavailable in this scene.");
                    }
                    var robotMatches = robotEditor.Targets
                        .Where(target => target.IsNativeSceneObject && target.IsAlive
                            && string.Equals(target.SceneName, binding.SceneName, StringComparison.Ordinal)
                            && target.DisplayName.IndexOf(binding.NameContains, StringComparison.OrdinalIgnoreCase) >= 0
                            && target.TryGetTransform(out var transform)
                            && Vec3.Distance(transform.Position, binding.ExpectedPosition) <= binding.SearchRadius)
                        .OrderBy(target => target.Id, StringComparer.Ordinal)
                        .Take(2)
                        .ToArray();
                    if (robotMatches.Length != 1)
                    {
                        return Fail(
                            ModErrorCode.Conflict,
                            "Binding '" + binding.DisplayName + "' resolved " + robotMatches.Length + " native robots; exactly one is required.");
                    }
                    var robotEntry = new CreatorRosterEntry(
                        "project-native:" + binding.Id,
                        binding.DisplayName,
                        CreatorContentKind.Robot,
                        owned: false)
                    {
                        RobotTarget = robotMatches[0],
                        SourceId = RobotKitNativeAdapterId
                    };
                    staged.Add(new KeyValuePair<string, CreatorRosterEntry>(binding.Id, robotEntry));
                    continue;
                }
                var query = creatorSession.QuerySceneTargets(new CreatorSceneQuery(
                    binding.ExpectedPosition,
                    binding.SearchRadius,
                    binding.NameContains,
                    maximumResults: 2,
                    adapterId: binding.AdapterId));
                if (!query.TryGetValue(out var matches))
                {
                    return Fail(query.ErrorCode, query.ErrorMessage);
                }
                if (matches.Count != 1)
                {
                    return Fail(
                        ModErrorCode.Conflict,
                        "Binding '" + binding.DisplayName + "' resolved " + matches.Count + " targets; exactly one is required.");
                }
                var target = matches[0];
                if (!string.Equals(target.AdapterId, binding.AdapterId, StringComparison.Ordinal))
                {
                    return Fail(
                        ModErrorCode.Conflict,
                        "Binding '" + binding.DisplayName + "' resolved through an unexpected native adapter.");
                }
                var entry = new CreatorRosterEntry(
                    "project-native:" + binding.Id,
                    binding.DisplayName,
                    target.Kind,
                    owned: false)
                {
                    NativeTarget = target,
                    SourceId = target.CatalogContentId
                };
                staged.Add(new KeyValuePair<string, CreatorRosterEntry>(binding.Id, entry));
            }
            foreach (var item in staged)
            {
                roster.Add(item.Value);
                projectBindings[item.Key] = item.Value.Id;
            }
            confirmedNativeProjectId = activeProject.Id;
            return OperationResult<string>.Success(activeProject.NativeBindings.Count + " native bindings confirmed for this session.");
        }

        private void ClearResolvedProjectBindings()
        {
            confirmedNativeProjectId = string.Empty;
            foreach (var rosterId in projectBindings.Values.ToArray())
            {
                var old = FindRoster(rosterId);
                if (old == null) continue;
                old.Dispose();
                roster.Remove(old);
            }
            projectBindings.Clear();
        }

        private static OperationResult<bool> MissingProjectEntity(CreatorGraphNode node) =>
            OperationResult<bool>.Failure(ModErrorCode.NotFound, "Project target '" + CreatorEventGraphRunner.TargetParameter(node) + "' is not available.");

        private static string Param(CreatorGraphNode node, string name) => CreatorEventGraphRunner.Parameter(node, name);

        private static RobotObjective? ParseObjective(string value)
        {
            var parts = (value ?? string.Empty).Split(new[] { ':' }, 2);
            var kind = parts[0].Trim().ToUpperInvariant();
            var target = parts.Length > 1 ? parts[1].Trim() : string.Empty;
            if (kind == "IDLE") return RobotObjective.Idle();
            if (kind == "WANDER") return target.Length == 0 ? RobotObjective.Wander() : RobotObjective.Wander(target);
            if (target.Length == 0) return null;
            if (kind == "FOLLOW") return RobotObjective.Follow(target);
            if (kind == "GO_TO") return RobotObjective.GoTo(target);
            if (kind == "PATROL") return RobotObjective.PatrolTo(target);
            if (kind == "FLEE") return RobotObjective.Flee(target);
            return null;
        }

        private OperationResult<bool> EvaluateStateCondition(CreatorGraphNode node)
        {
            var condition = Param(node, CreatorGraphParameters.Value).Trim();
            if (bool.TryParse(condition, out var literal)) return OperationResult<bool>.Success(literal);
            var entry = ProjectRoster(node);
            if (string.Equals(condition, "entity.alive", StringComparison.OrdinalIgnoreCase))
            {
                return OperationResult<bool>.Success(entry?.IsAlive == true);
            }
            if (string.Equals(condition, "robot.autonomous", StringComparison.OrdinalIgnoreCase))
            {
                return OperationResult<bool>.Success(entry?.Robot?.BrainMode == RobotBrainMode.Autonomous);
            }
            const string objectivePrefix = "objective:";
            if (condition.StartsWith(objectivePrefix, StringComparison.OrdinalIgnoreCase)
                && entry?.Robot != null && objectives?.TryGetObjective(entry.Robot, out var handle) == true && handle != null)
            {
                return OperationResult<bool>.Success(string.Equals(
                    handle.State.ToString(),
                    condition.Substring(objectivePrefix.Length),
                    StringComparison.OrdinalIgnoreCase));
            }
            return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Unsupported state condition '" + condition + "'.");
        }
    }
}
