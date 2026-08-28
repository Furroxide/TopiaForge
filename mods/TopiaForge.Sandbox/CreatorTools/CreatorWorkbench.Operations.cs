using System;
using System.Globalization;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.CreatorTools.Shared
{
    internal sealed partial class CreatorWorkbench
    {
        public OperationResult<string> SpawnRobot()
        {
            var entry = catalog.FirstOrDefault(item => item.IsRobotKit);
            if (entry == null)
            {
                entry = new CreatorCatalogEntry(
                    "robotkit:default",
                    "Default robot",
                    string.Empty,
                    CreatorContentKind.Robot);
            }
            return Spawn(entry);
        }

        public OperationResult<string> SpawnSelected()
        {
            var entry = FindCatalog(selectedCatalogId);
            return entry == null
                ? OperationResult<string>.Failure(ModErrorCode.NotFound, "Choose a catalog entry first.")
                : Spawn(entry);
        }

        public OperationResult<string> Undo()
        {
            var result = UndoHistory();
            if (result.Succeeded)
            {
                RefreshUi();
            }
            return result;
        }

        public OperationResult<string> CleanUpEverything()
        {
            var removed = 0;
            if (runner != null || projectEntities.Count > 0 || projectBindings.Count > 0)
            {
                removed += projectEntities.Count;
                StopProject(removeProjectEntities: true);
            }
            for (var index = roster.Count - 1; index >= 0; index--)
            {
                if (!roster[index].Owned) continue;
                Despawn(roster[index]);
                roster[index].Dispose();
                roster.RemoveAt(index);
                removed++;
            }
            selectedRosterId = string.Empty;
            ClearHistory();
            status = removed.ToString(CultureInfo.InvariantCulture) + " owned objects removed.";
            RefreshUi();
            return OperationResult<string>.Success(status);
        }

        public OperationResult<string> ToggleRobotSimulation()
        {
            var allowed = EnsureMutationAllowed();
            if (!allowed.Succeeded) return OperationResult<string>.Failure(allowed.ErrorCode, allowed.ErrorMessage);
            var robotsChanged = 0;
            foreach (var entry in roster)
            {
                if (entry.Robot == null || !entry.Robot.IsAlive) continue;
                entry.Robot.Stop();
                objectives?.ClearObjective(entry.Robot);
                robotsChanged++;
            }
            status = robotsChanged.ToString(CultureInfo.InvariantCulture) + " robots stopped.";
            RefreshUi();
            return OperationResult<string>.Success(status);
        }

        public string DescribeStatus() =>
            "session=" + (IsSessionActive ? "active" : "inactive")
            + ", roster=" + roster.Count.ToString(CultureInfo.InvariantCulture)
            + ", catalog=" + catalog.Count.ToString(CultureInfo.InvariantCulture)
            + ", event=" + (runner?.IsRunning == true ? "running" : "stopped");

        public OperationResult<bool> RefreshNativeRoster()
        {
            if (!IsSessionActive) return OperationResult<bool>.Success(false);
            var changed = false;
            if (robotEditor?.IsAvailable == true)
            {
                foreach (var target in robotEditor.Targets)
                {
                    if (!target.IsNativeSceneObject || !target.IsAlive || FindRoster("robot-native:" + target.Id) != null) continue;
                    roster.Add(new CreatorRosterEntry(
                        "robot-native:" + target.Id,
                        target.DisplayName,
                        CreatorContentKind.Robot,
                        owned: false)
                    {
                        RobotTarget = target
                    });
                    changed = true;
                }
            }

            var queried = creatorSession?.QuerySceneTargets(new CreatorSceneQuery(maximumResults: 128));
            if (queried != null && queried.TryGetValue(out var targets))
            {
                foreach (var target in targets)
                {
                    if (!target.IsAlive || FindRoster("native:" + target.Id) != null) continue;
                    roster.Add(new CreatorRosterEntry(
                        "native:" + target.Id,
                        target.DisplayName,
                        target.Kind,
                        owned: false)
                    {
                        NativeTarget = target,
                        SourceId = target.CatalogContentId
                    });
                    changed = true;
                }
            }
            if (changed) RefreshUi();
            return OperationResult<bool>.Success(changed);
        }

        private OperationResult<string> Spawn(CreatorCatalogEntry catalogEntry, TransformState? explicitTransform = null)
        {
            var allowed = EnsureMutationAllowed();
            if (!allowed.Succeeded) return OperationResult<string>.Failure(allowed.ErrorCode, allowed.ErrorMessage);
            var started = EnsureSession();
            if (!started.Succeeded)
            {
                return OperationResult<string>.Failure(started.ErrorCode, started.ErrorMessage);
            }
            var capacity = EnsureOwnedCapacity();
            if (!capacity.Succeeded)
            {
                return OperationResult<string>.Failure(capacity.ErrorCode, capacity.ErrorMessage);
            }

            var transformResult = explicitTransform.HasValue
                ? OperationResult<TransformState>.Success(explicitTransform.Value)
                : AimTransform();
            if (!transformResult.TryGetValue(out var transform))
            {
                return OperationResult<string>.Failure(transformResult.ErrorCode, transformResult.ErrorMessage);
            }

            return catalogEntry.IsRobotKit
                ? SpawnRobotKit(catalogEntry, transform)
                : SpawnCatalog(catalogEntry, transform);
        }

        private OperationResult<string> SpawnRobotKit(CreatorCatalogEntry catalogEntry, TransformState transform)
        {
            if (!robots.IsAvailable)
            {
                return OperationResult<string>.Failure(ModErrorCode.Unavailable, "RobotKit cannot spawn in this scene.");
            }
            var number = nextOwnedNumber++;
            var result = robots.Spawn(new RobotAgentSpawnRequest(
                transform.Position,
                brainMode: RobotBrainMode.Dormant,
                name: "Creator Robot " + number.ToString(CultureInfo.InvariantCulture),
                robotTypeId: string.Equals(catalogEntry.SourceId, "default", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : catalogEntry.SourceId));
            if (!result.TryGetValue(out var agent))
            {
                return OperationResult<string>.Failure(result.ErrorCode, result.ErrorMessage);
            }

            context.Entities.SetTransform(agent, transform);
            var entry = new CreatorRosterEntry(
                "owned-robot:" + number.ToString(CultureInfo.InvariantCulture),
                agent.Name,
                CreatorContentKind.Robot,
                owned: true,
                cleanup: context.Lifetime.Defer(() => agent.Despawn()))
            {
                Robot = agent,
                SourceId = catalogEntry.SourceId,
                TargetName = "ROBOT " + number.ToString(CultureInfo.InvariantCulture)
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
            RecordSpawn(entry);
            SelectRoster(entry.Id);
            status = entry.DisplayName + " spawned.";
            context.Ui.ShowToast(status, UiTone.Success);
            RefreshUi();
            return OperationResult<string>.Success(status);
        }

        private OperationResult<string> SpawnCatalog(CreatorCatalogEntry catalogEntry, TransformState transform)
        {
            var result = creatorSession!.Spawn(new CreatorSpawnRequest(catalogEntry.SourceId, transform));
            if (!result.TryGetValue(out var handle))
            {
                return OperationResult<string>.Failure(result.ErrorCode, result.ErrorMessage);
            }
            var number = nextOwnedNumber++;
            var entry = new CreatorRosterEntry(
                "owned-content:" + number.ToString(CultureInfo.InvariantCulture),
                handle.Descriptor.DisplayName + " " + number.ToString(CultureInfo.InvariantCulture),
                handle.Descriptor.Kind,
                owned: true,
                cleanup: handle)
            {
                Spawn = handle,
                SourceId = handle.Descriptor.ContentId
            };
            if (robots.TryGetRobot(handle.Entity, out var agent)) entry.Robot = agent;
            roster.Add(entry);
            RecordSpawn(entry);
            SelectRoster(entry.Id);
            status = entry.DisplayName + " spawned.";
            context.Ui.ShowToast(status, UiTone.Success);
            RefreshUi();
            return OperationResult<string>.Success(status);
        }

        private OperationResult<string> DuplicateSelected()
        {
            var allowed = EnsureMutationAllowed();
            if (!allowed.Succeeded) return OperationResult<string>.Failure(allowed.ErrorCode, allowed.ErrorMessage);
            var entry = SelectedRoster();
            if (entry == null) return OperationResult<string>.Failure(ModErrorCode.NotFound, "Choose a roster target first.");
            if (!TryGetTransform(entry, out var transform))
            {
                return OperationResult<string>.Failure(ModErrorCode.Unavailable, "The selected target has no editable transform.");
            }
            var offset = new TransformState(
                transform.Position + new Vec3(1f, 0f, 1f),
                transform.Rotation,
                transform.Scale);
            if (entry.Robot != null && entry.Owned)
            {
                var catalogEntry = new CreatorCatalogEntry(
                    "robotkit:" + (string.IsNullOrEmpty(entry.SourceId) ? "default" : entry.SourceId),
                    entry.DisplayName,
                    string.Empty,
                    CreatorContentKind.Robot);
                var duplicatedRobot = Spawn(catalogEntry, offset);
                if (duplicatedRobot.Succeeded)
                {
                }
                return duplicatedRobot;
            }
            if (entry.Spawn != null)
            {
                var capacity = EnsureOwnedCapacity();
                if (!capacity.Succeeded) return OperationResult<string>.Failure(capacity.ErrorCode, capacity.ErrorMessage);
                var duplicated = entry.Spawn.Duplicate(offset);
                if (!duplicated.TryGetValue(out var handle))
                {
                    return OperationResult<string>.Failure(duplicated.ErrorCode, duplicated.ErrorMessage);
                }
                var number = nextOwnedNumber++;
                var duplicate = new CreatorRosterEntry(
                    "owned-content:" + number.ToString(CultureInfo.InvariantCulture),
                    handle.Descriptor.DisplayName + " " + number.ToString(CultureInfo.InvariantCulture),
                    handle.Descriptor.Kind,
                    owned: true,
                    cleanup: handle)
                {
                    Spawn = handle,
                    SourceId = handle.Descriptor.ContentId
                };
                roster.Add(duplicate);
                RecordSpawn(duplicate);
                SelectRoster(duplicate.Id);
                status = duplicate.DisplayName + " duplicated.";
                RefreshUi();
                return OperationResult<string>.Success(status);
            }
            if (entry.NativeTarget != null
                && (entry.NativeTarget.Capabilities & CreatorSceneTargetCapabilities.CatalogDuplicate) != 0
                && !string.IsNullOrEmpty(entry.NativeTarget.CatalogContentId))
            {
                var duplicatedNative = Spawn(
                    new CreatorCatalogEntry(
                        "content:" + entry.NativeTarget.CatalogContentId,
                        entry.DisplayName,
                        string.Empty,
                        entry.Kind),
                    offset);
                if (duplicatedNative.Succeeded)
                {
                }
                return duplicatedNative;
            }
            return OperationResult<string>.Failure(ModErrorCode.Conflict, "Borrowed scene targets cannot be duplicated safely.");
        }

        private OperationResult<string> RemoveSelected()
        {
            var entry = SelectedRoster();
            return entry == null
                ? OperationResult<string>.Failure(ModErrorCode.NotFound, "Choose a roster target first.")
                : Remove(entry);
        }

        private OperationResult<string> Remove(CreatorRosterEntry entry)
        {
            if (!entry.Owned)
            {
                var allowed = EnsureMutationAllowed();
                if (!allowed.Succeeded) return OperationResult<string>.Failure(allowed.ErrorCode, allowed.ErrorMessage);
                if (entry.NativeTarget == null
                    || (entry.NativeTarget.Capabilities & CreatorSceneTargetCapabilities.TemporaryVisibility) == 0)
                {
                    return OperationResult<string>.Failure(ModErrorCode.Conflict, "This borrowed target cannot be hidden safely.");
                }
                var lease = EnsureNativeEdit(entry);
                if (!lease.TryGetValue(out var edit)) return OperationResult<string>.Failure(lease.ErrorCode, lease.ErrorMessage);
                var hidden = edit.SetTemporarilyHidden(true);
                if (!hidden.Succeeded) return OperationResult<string>.Failure(hidden.ErrorCode, hidden.ErrorMessage);
                entry.NativeHidden = true;
                RecordNativeHidden(entry);
                return OperationResult<string>.Success(entry.DisplayName + " temporarily hidden; End Session & Restore restores it.");
            }
            var projectId = ProjectIdForRoster(entry.Id);
            if (TryGetTransform(entry, out var previous)) RecordDespawn(entry, previous, projectId);
            Despawn(entry);
            if (!string.IsNullOrEmpty(projectId))
            {
                DisposeProjectInteractions(projectId);
                projectEntities.Remove(projectId);
                runner?.Fire(CreatorGraphNodeKind.EntityRemoved, projectId);
            }
            roster.Remove(entry);
            entry.Dispose();
            if (string.Equals(selectedRosterId, entry.Id, StringComparison.Ordinal)) selectedRosterId = string.Empty;
            status = entry.DisplayName + " removed.";
            RefreshUi();
            return OperationResult<string>.Success(status);
        }

        private static void Despawn(CreatorRosterEntry entry)
        {
            if (entry.Robot != null) entry.Robot.Despawn();
            else entry.Spawn?.Despawn();
        }

        private OperationResult<TransformState> AimTransform()
        {
            if (!context.LocalPlayer.TryGetSnapshot(out var player) || player == null)
            {
                return OperationResult<TransformState>.Failure(ModErrorCode.Unavailable, "A gameplay player and camera are required.");
            }
            var position = player.AimRay.GetPoint(8f);
            if (context.Physics.TryRaycast(player.AimRay, 40f, out var hit) && hit != null)
            {
                position = hit.Point + hit.Normal * 0.5f;
            }
            return OperationResult<TransformState>.Success(new TransformState(position, Quat.Identity, new Vec3(1f, 1f, 1f)));
        }

        private OperationResult<bool> EnsureOwnedCapacity() =>
            roster.Count(entry => entry.Owned && entry.IsAlive) < options.MaximumInstances
                ? OperationResult<bool>.Success(true)
                : OperationResult<bool>.Failure(ModErrorCode.Conflict, "The creator session instance limit was reached.");
    }
}
