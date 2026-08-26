using System;
using System.Globalization;
using TopiaForge.Mods;

namespace TopiaForge.CreatorTools.Shared
{
    internal sealed partial class CreatorWorkbench
    {
        private void SelectRoster(string id)
        {
            selectedRosterId = id;
            var entry = SelectedRoster();
            if (entry != null && TryGetTransform(entry, out var transform))
            {
                xText = Format(transform.Position.X);
                yText = Format(transform.Position.Y);
                zText = Format(transform.Position.Z);
                qxText = Format(transform.Rotation.X);
                qyText = Format(transform.Rotation.Y);
                qzText = Format(transform.Rotation.Z);
                qwText = Format(transform.Rotation.W);
                sxText = Format(transform.Scale.X);
                syText = Format(transform.Scale.Y);
                szText = Format(transform.Scale.Z);
            }
            LoadRobotAppearanceDraft(entry);
            RefreshUi();
        }

        private bool TryGetTransform(CreatorRosterEntry entry, out TransformState transform)
        {
            if (entry.Spawn?.TryGetTransform(out transform) == true) return true;
            if (entry.NativeEdit?.TryGetTransform(out transform) == true) return true;
            if (entry.RobotEdit?.Target.TryGetTransform(out transform) == true) return true;
            if (entry.RobotTarget?.TryGetTransform(out transform) == true) return true;
            if (entry.NativeTarget != null && context.Entities.TryGetTransform(entry.NativeTarget.Entity, out transform)) return true;
            if (entry.Entity != null && context.Entities.TryGetTransform(entry.Entity, out transform)) return true;
            transform = TransformState.Identity;
            return false;
        }

        private OperationResult<TransformState> SetTransform(
            CreatorRosterEntry entry,
            TransformState transform,
            bool recordHistory = true)
        {
            var allowed = EnsureMutationAllowed();
            if (!allowed.Succeeded)
            {
                return OperationResult<TransformState>.Failure(allowed.ErrorCode, allowed.ErrorMessage);
            }
            var previous = TransformState.Identity;
            var hadPrevious = recordHistory && TryGetTransform(entry, out previous);
            OperationResult<TransformState> result;
            if (entry.Spawn != null) result = entry.Spawn.SetTransform(transform);
            else if (entry.Robot != null && entry.Owned) result = context.Entities.SetTransform(entry.Robot, transform);
            else if (entry.RobotTarget != null)
            {
                var lease = EnsureRobotEdit(entry);
                result = lease.TryGetValue(out var edit)
                    ? edit.PreviewTransform(transform)
                    : OperationResult<TransformState>.Failure(lease.ErrorCode, lease.ErrorMessage);
            }
            else if (entry.NativeTarget != null)
            {
                var lease = EnsureNativeEdit(entry);
                result = lease.TryGetValue(out var edit)
                    ? edit.SetTransform(transform)
                    : OperationResult<TransformState>.Failure(lease.ErrorCode, lease.ErrorMessage);
            }
            else result = OperationResult<TransformState>.Failure(ModErrorCode.Unavailable, "This target does not expose safe transform editing.");
            if (result.Succeeded && hadPrevious) RecordTransform(entry, previous);
            return result;
        }

        private OperationResult<string> ApplyTransform()
        {
            var entry = SelectedRoster();
            if (entry == null) return OperationResult<string>.Failure(ModErrorCode.NotFound, "Choose a roster target first.");
            if (!TryGetTransform(entry, out var current))
            {
                return OperationResult<string>.Failure(ModErrorCode.Unavailable, "The selected transform cannot be read.");
            }
            if (!TryFloat(xText, out var x) || !TryFloat(yText, out var y) || !TryFloat(zText, out var z)
                || !TryFloat(qxText, out var qx) || !TryFloat(qyText, out var qy)
                || !TryFloat(qzText, out var qz) || !TryFloat(qwText, out var qw)
                || !TryFloat(sxText, out var sx) || !TryFloat(syText, out var sy) || !TryFloat(szText, out var sz)
                || sx == 0f || sy == 0f || sz == 0f)
            {
                return OperationResult<string>.Failure(ModErrorCode.InvalidArgument, "Transform values must be finite and scale cannot be zero.");
            }
            TransformState transform;
            try
            {
                transform = new TransformState(new Vec3(x, y, z), new Quat(qx, qy, qz, qw), new Vec3(sx, sy, sz));
            }
            catch (ArgumentException exception)
            {
                return OperationResult<string>.Failure(ModErrorCode.InvalidArgument, exception.Message);
            }
            var result = SetTransform(entry, transform);
            if (result.Succeeded)
            {
                recorder?.Observe(CreatorObservation.TransformedInstance);
            }
            return ToText(result, entry.DisplayName + " transform updated.");
        }

        private OperationResult<string> ToggleNativeHidden()
        {
            var allowed = EnsureMutationAllowed();
            if (!allowed.Succeeded) return OperationResult<string>.Failure(allowed.ErrorCode, allowed.ErrorMessage);
            var entry = SelectedRoster();
            if (entry?.NativeTarget == null
                || (entry.NativeTarget.Capabilities & CreatorSceneTargetCapabilities.TemporaryVisibility) == 0)
            {
                return OperationResult<string>.Failure(ModErrorCode.Unavailable, "This native target does not support temporary visibility.");
            }
            var lease = EnsureNativeEdit(entry);
            if (!lease.TryGetValue(out var edit))
            {
                return OperationResult<string>.Failure(lease.ErrorCode, lease.ErrorMessage);
            }
            var next = !entry.NativeHidden;
            var result = edit.SetTemporarilyHidden(next);
            if (result.Succeeded) entry.NativeHidden = next;
            return ToText(result, entry.DisplayName + (next ? " temporarily hidden." : " visible again."));
        }

        private OperationResult<string> Nudge(Vec3 offset)
        {
            var entry = SelectedRoster();
            if (entry == null) return OperationResult<string>.Failure(ModErrorCode.NotFound, "Choose a roster target first.");
            if (!TryGetTransform(entry, out var current))
            {
                return OperationResult<string>.Failure(ModErrorCode.Unavailable, "The selected transform cannot be read.");
            }
            var next = new TransformState(current.Position + offset, current.Rotation, current.Scale);
            var result = SetTransform(entry, next);
            if (result.Succeeded)
            {
                xText = Format(next.Position.X);
                yText = Format(next.Position.Y);
                zText = Format(next.Position.Z);
            }
            return ToText(result, entry.DisplayName + " moved.");
        }

        private OperationResult<string> MoveToAim()
        {
            var entry = SelectedRoster();
            if (entry == null) return OperationResult<string>.Failure(ModErrorCode.NotFound, "Choose a roster target first.");
            var aimed = AimTransform();
            if (!aimed.TryGetValue(out var target))
            {
                return OperationResult<string>.Failure(aimed.ErrorCode, aimed.ErrorMessage);
            }
            if (TryGetTransform(entry, out var current))
            {
                target = new TransformState(target.Position, current.Rotation, current.Scale);
            }
            var result = SetTransform(entry, target);
            if (result.Succeeded)
            {
                SelectRoster(entry.Id);
                recorder?.Observe(CreatorObservation.TransformedInstance);
                // Relocating a borrowed native robot is the location half of
                // creator.personality-and-location-restore; owned spawns are
                // not restored on End Session because they are removed.
                if (!entry.Owned && entry.RobotTarget != null)
                {
                    recorder?.Observe(
                        CreatorObservation.PreviewedRobotLocation);
                }
            }
            return ToText(result, entry.DisplayName + " moved to the aim point.");
        }

        private OperationResult<string> SetSelectedBrain(RobotBrainMode mode)
        {
            var allowed = EnsureMutationAllowed();
            if (!allowed.Succeeded) return OperationResult<string>.Failure(allowed.ErrorCode, allowed.ErrorMessage);
            var entry = SelectedRoster();
            if (entry == null) return OperationResult<string>.Failure(ModErrorCode.NotFound, "Choose a robot first.");
            OperationResult<bool> result;
            if (entry.Robot != null)
            {
                result = entry.Robot.SetBrainMode(mode);
            }
            else if (entry.RobotTarget != null)
            {
                var lease = EnsureRobotEdit(entry);
                if (!lease.TryGetValue(out var edit))
                {
                    return OperationResult<string>.Failure(lease.ErrorCode, lease.ErrorMessage);
                }
                result = edit.PreviewBrainMode(mode);
            }
            else
            {
                return OperationResult<string>.Failure(ModErrorCode.Unavailable, "This catalog adapter does not expose robot brain control.");
            }
            return ToText(result, entry.DisplayName + " brain set to " + mode + ".");
        }

        private OperationResult<string> ApplyPersonality()
        {
            var allowed = EnsureMutationAllowed();
            if (!allowed.Succeeded) return OperationResult<string>.Failure(allowed.ErrorCode, allowed.ErrorMessage);
            var entry = SelectedRoster();
            if (entry == null) return OperationResult<string>.Failure(ModErrorCode.NotFound, "Choose a robot first.");
            RobotPersonalityDraft draft;
            try
            {
                draft = new RobotPersonalityDraft(personaName, personaInstructions, options.ChatTemperature);
            }
            catch (ArgumentException exception)
            {
                return OperationResult<string>.Failure(ModErrorCode.InvalidArgument, exception.Message);
            }
            var lease = EnsureRobotEdit(entry);
            if (!lease.TryGetValue(out var edit))
            {
                return OperationResult<string>.Failure(lease.ErrorCode, lease.ErrorMessage);
            }
            var previewed = edit.PreviewPersonality(draft);
            if (previewed.Succeeded && !entry.Owned && entry.RobotTarget != null)
            {
                recorder?.Observe(
                    CreatorObservation.PreviewedRobotPersonality);
            }
            return ToText(previewed, entry.DisplayName + " personality preview applied.");
        }

        private OperationResult<string> SetEmote(string emote)
        {
            var allowed = EnsureMutationAllowed();
            if (!allowed.Succeeded) return OperationResult<string>.Failure(allowed.ErrorCode, allowed.ErrorMessage);
            var entry = SelectedRoster();
            if (entry?.Robot == null)
            {
                return OperationResult<string>.Failure(ModErrorCode.Unavailable, "This target does not expose RobotKit emotes.");
            }
            return ToText(entry.Robot.SetEmote(emote), entry.DisplayName + " emote updated.");
        }

        private OperationResult<string> SetObjective(RobotObjective objective)
        {
            var allowed = EnsureMutationAllowed();
            if (!allowed.Succeeded) return OperationResult<string>.Failure(allowed.ErrorCode, allowed.ErrorMessage);
            var entry = SelectedRoster();
            if (entry?.Robot == null || objectives == null)
            {
                return OperationResult<string>.Failure(ModErrorCode.Unavailable, "This target does not expose RobotKit objectives.");
            }
            var result = objectives.SetObjective(entry.Robot, objective);
            return result.Succeeded
                ? OperationResult<string>.Success(entry.DisplayName + " programmed: " + objective.Describe() + ".")
                : OperationResult<string>.Failure(result.ErrorCode, result.ErrorMessage);
        }

        private OperationResult<IRobotEditLease> EnsureRobotEdit(CreatorRosterEntry entry)
        {
            if (entry.RobotEdit?.IsActive == true) return OperationResult<IRobotEditLease>.Success(entry.RobotEdit);
            if (robotEditor == null)
            {
                return OperationResult<IRobotEditLease>.Failure(ModErrorCode.Unavailable, "Robot personality editing is unavailable.");
            }
            if (entry.RobotTarget == null && entry.Robot != null
                && robotEditor.TryResolve(entry.Robot, out var resolvedTarget))
            {
                entry.RobotTarget = resolvedTarget;
            }
            if (entry.RobotTarget == null)
            {
                return OperationResult<IRobotEditLease>.Failure(ModErrorCode.Unavailable, "This robot has no editable native target.");
            }
            var result = robotEditor.BeginTemporaryEdit(entry.RobotTarget);
            if (result.TryGetValue(out var edit)) entry.RobotEdit = edit;
            return result;
        }

        private OperationResult<ICreatorTemporaryEdit> EnsureNativeEdit(CreatorRosterEntry entry)
        {
            if (entry.NativeEdit?.IsAlive == true) return OperationResult<ICreatorTemporaryEdit>.Success(entry.NativeEdit);
            if (creatorSession == null || entry.NativeTarget == null)
            {
                return OperationResult<ICreatorTemporaryEdit>.Failure(ModErrorCode.Unavailable, "Native editing is unavailable.");
            }
            var result = creatorSession.BeginTemporaryEdit(entry.NativeTarget);
            if (result.TryGetValue(out var edit)) entry.NativeEdit = edit;
            return result;
        }

        private static OperationResult<string> ToText<T>(OperationResult<T> result, string success) where T : notnull =>
            result.Succeeded
                ? OperationResult<string>.Success(success)
                : OperationResult<string>.Failure(result.ErrorCode, result.ErrorMessage);

        private static bool TryFloat(string value, out float parsed) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
            && !float.IsNaN(parsed) && !float.IsInfinity(parsed);

        private static string Format(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
