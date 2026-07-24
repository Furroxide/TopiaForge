using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.RobotKit
{
    internal sealed class RobotEditLease : IRobotEditLease
    {
        private readonly RobotEditTarget target;
        private readonly IModLogger logger;
        private readonly Action released;
        private readonly TransformState originalTransform;
        private readonly GameReflection.BrainStateSnapshot? originalBrain;
        private readonly object? originalHackedPersonality;
        private readonly List<EditedProperty> editOrder = new List<EditedProperty>(3);
        private TransformState? lastTransform;
        private GameReflection.BrainStateSnapshot? lastBrainState;
        private object? temporaryPersonality;
        private bool active = true;

        public RobotEditLease(RobotEditTarget target, IModLogger logger, Action released)
        {
            this.target = target ?? throw new ArgumentNullException(nameof(target));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.released = released ?? throw new ArgumentNullException(nameof(released));
            if (!target.TryGetTransform(out originalTransform))
            {
                throw new InvalidOperationException("Robot target disappeared before its edit snapshot was captured.");
            }

            originalBrain = GameReflection.CaptureBrainState(target.Root);
            originalHackedPersonality = RobotPersonalityBridge.GetHackedPersonality(target.Agent);
        }

        public IRobotEditTarget Target => target;
        public bool IsActive => active && target.IsAlive;

        public OperationResult<TransformState> PreviewTransform(TransformState transform)
        {
            var unavailable = EnsureActive<TransformState>();
            if (unavailable != null)
            {
                return unavailable;
            }

            try
            {
                RobotEditTransform.Apply(target.Root.transform, transform);
                if (!target.TryGetTransform(out var applied))
                {
                    RobotEditTransform.Apply(target.Root.transform, originalTransform);
                    return OperationResult<TransformState>.Failure(
                        ModErrorCode.External,
                        "The temporary robot transform could not be verified.");
                }

                lastTransform = applied;
                MarkEdited(EditedProperty.Transform);
                return OperationResult<TransformState>.Success(applied);
            }
            catch (Exception exception)
            {
                return OperationResult<TransformState>.Failure(ModErrorCode.External, exception.Message);
            }
        }

        public OperationResult<bool> PreviewBrainMode(RobotBrainMode mode)
        {
            if (!Enum.IsDefined(typeof(RobotBrainMode), mode))
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Unknown robot brain mode.");
            }

            var unavailable = EnsureActive<bool>();
            if (unavailable != null)
            {
                return unavailable;
            }

            if (originalBrain == null)
            {
                return OperationResult<bool>.Failure(ModErrorCode.Unavailable, "The native robot brain could not be snapshotted safely.");
            }

            var before = GameReflection.CaptureBrainState(target.Root);
            if (before == null)
            {
                return OperationResult<bool>.Failure(ModErrorCode.Unavailable, "The current native robot brain state could not be verified.");
            }

            try
            {
                GameReflection.ApplyBrainMode(target.Root, mode, originalBrain, logger);
                lastBrainState = GameReflection.CaptureBrainState(target.Root);
                if (lastBrainState == null)
                {
                    GameReflection.RestoreBrainState(target.Root, before, logger);
                    return OperationResult<bool>.Failure(ModErrorCode.External, "The temporary brain state could not be verified.");
                }
                MarkEdited(EditedProperty.Brain);
                return OperationResult<bool>.Success(true);
            }
            catch (Exception exception)
            {
                GameReflection.RestoreBrainState(target.Root, before, logger);
                return OperationResult<bool>.Failure(ModErrorCode.External, exception.Message);
            }
        }

        public OperationResult<bool> PreviewPersonality(RobotPersonalityDraft personality)
        {
            if (personality == null)
            {
                throw new ArgumentNullException(nameof(personality));
            }

            var unavailable = EnsureActive<bool>();
            if (unavailable != null)
            {
                return unavailable;
            }

            var applied = RobotPersonalityBridge.Apply(target.Agent, personality);
            if (!applied.TryGetValue(out var created))
            {
                return OperationResult<bool>.Failure(applied.ErrorCode, applied.ErrorMessage);
            }

            RobotPersonalityBridge.DestroyTemporary(temporaryPersonality);
            temporaryPersonality = created;
            MarkEdited(EditedProperty.Personality);
            return OperationResult<bool>.Success(true);
        }

        public OperationResult<bool> Restore()
        {
            if (!active)
            {
                return OperationResult<bool>.Success(false);
            }

            active = false;
            var conflict = false;
            Exception? restoreFailure = null;
            for (var index = editOrder.Count - 1; index >= 0; index--)
            {
                try
                {
                    if (!target.IsAlive)
                    {
                        break;
                    }

                    switch (editOrder[index])
                    {
                        case EditedProperty.Transform:
                            conflict |= !RestoreTransform();
                            break;
                        case EditedProperty.Brain:
                            conflict |= !RestoreBrain();
                            break;
                        case EditedProperty.Personality:
                            conflict |= !RestorePersonality();
                            break;
                    }
                }
                catch (Exception exception)
                {
                    restoreFailure = restoreFailure ?? exception;
                    logger.Warn("Robot edit restoration failed: " + exception.Message);
                }
            }

            RobotPersonalityBridge.DestroyTemporary(temporaryPersonality);
            temporaryPersonality = null;
            released();

            if (restoreFailure != null)
            {
                return OperationResult<bool>.Failure(ModErrorCode.External, restoreFailure.Message);
            }

            return conflict
                ? OperationResult<bool>.Failure(ModErrorCode.Conflict, "Robot changed outside Creator Tools; conflicting properties were not overwritten.")
                : OperationResult<bool>.Success(true);
        }

        public void Dispose()
        {
            var result = Restore();
            if (!result.Succeeded && result.ErrorCode != ModErrorCode.Conflict)
            {
                logger.Warn("Robot edit lease could not restore cleanly: " + result.ErrorMessage);
            }
        }

        private OperationResult<T>? EnsureActive<T>() where T : notnull
        {
            if (!active)
            {
                return OperationResult<T>.Failure(ModErrorCode.InvalidState, "Robot edit lease is no longer active.");
            }

            if (!target.IsAlive)
            {
                return OperationResult<T>.Failure(ModErrorCode.NotFound, "Robot edit target is no longer alive.");
            }

            return null;
        }

        private bool RestoreTransform()
        {
            if (!lastTransform.HasValue
                || !target.TryGetTransform(out var current))
            {
                return false;
            }

            var last = lastTransform.Value;
            var restorePosition = current.Position == last.Position;
            var restoreRotation = current.Rotation == last.Rotation;
            var restoreScale = current.Scale == last.Scale;
            if (!restorePosition && !restoreRotation && !restoreScale)
            {
                return false;
            }

            RobotEditTransform.Apply(
                target.Root.transform,
                new TransformState(
                    restorePosition ? originalTransform.Position : current.Position,
                    restoreRotation ? originalTransform.Rotation : current.Rotation,
                    restoreScale ? originalTransform.Scale : current.Scale));
            return restorePosition && restoreRotation && restoreScale;
        }

        private bool RestoreBrain()
        {
            return originalBrain != null
                && lastBrainState != null
                && GameReflection.RestoreBrainStateConflictSafe(target.Root, originalBrain, lastBrainState, logger);
        }

        private bool RestorePersonality()
        {
            if (!RobotPersonalityBridge.IsCurrent(target.Agent, temporaryPersonality))
            {
                return false;
            }

            RobotPersonalityBridge.Restore(target.Agent, originalHackedPersonality);
            return true;
        }

        private void MarkEdited(EditedProperty property)
        {
            if (!editOrder.Contains(property))
            {
                editOrder.Add(property);
            }
        }

        private enum EditedProperty
        {
            Transform,
            Brain,
            Personality
        }
    }
}
