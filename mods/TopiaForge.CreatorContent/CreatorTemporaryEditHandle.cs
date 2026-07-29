using System;
using TopiaForge.Mods;

namespace TopiaForge.CreatorContent
{
    internal sealed class CreatorTemporaryEditHandle : ICreatorTemporaryEdit
    {
        private readonly object gate = new object();
        private readonly CreatorContentService service;
        private readonly CreatorSession session;
        private readonly SceneAdapterRegistration registration;
        private readonly IModLogger logger;
        private ICreatorTemporaryEdit? source;
        private bool sourceFaulted;

        public CreatorTemporaryEditHandle(
            CreatorContentService service,
            CreatorSession session,
            SceneAdapterRegistration registration,
            CreatorSceneTargetHandle target,
            ICreatorTemporaryEdit source,
            CreatorSceneTargetCapabilities capabilities,
            IModLogger logger)
        {
            this.service = service;
            this.session = session;
            this.registration = registration;
            Target = target;
            this.source = source;
            Capabilities = capabilities;
            this.logger = logger;
        }

        public ICreatorSceneTarget Target { get; }
        public CreatorSceneTargetCapabilities Capabilities { get; }

        public bool IsAlive
        {
            get
            {
                lock (gate)
                {
                    if (source == null || sourceFaulted || !Target.IsAlive)
                    {
                        return false;
                    }

                    try
                    {
                        return source.IsAlive;
                    }
                    catch (Exception exception)
                    {
                        sourceFaulted = true;
                        logger.Error(exception, "Creator scene adapter '" + Target.AdapterId + "' threw while checking a temporary edit.");
                        return false;
                    }
                }
            }
        }

        public bool TryGetTransform(out TransformState transform)
        {
            lock (gate)
            {
                if (source == null || sourceFaulted || !Target.IsAlive)
                {
                    transform = TransformState.Identity;
                    return false;
                }

                try
                {
                    if (!source.IsAlive)
                    {
                        transform = TransformState.Identity;
                        return false;
                    }
                    return source.TryGetTransform(out transform);
                }
                catch (Exception exception)
                {
                    sourceFaulted = true;
                    logger.Error(exception, "Creator scene adapter '" + Target.AdapterId + "' threw while reading a temporary edit.");
                    transform = TransformState.Identity;
                    return false;
                }
            }
        }

        public OperationResult<TransformState> SetTransform(TransformState transform)
        {
            lock (gate)
            {
                var usable = RequireUsable<TransformState>();
                if (usable != null) return usable;

                TransformState current;
                try
                {
                    if (!source!.TryGetTransform(out current))
                    {
                        return OperationResult<TransformState>.Failure(
                            ModErrorCode.Unavailable,
                            "The native scene adapter could not read the current transform.");
                    }
                }
                catch (Exception exception)
                {
                    return Fault<TransformState>(exception, "reading a temporary transform");
                }

                var unsupported = UnsupportedChanges(current, transform, Capabilities);
                if (unsupported != null)
                {
                    return OperationResult<TransformState>.Failure(ModErrorCode.InvalidArgument, unsupported);
                }

                try
                {
                    return source!.SetTransform(transform);
                }
                catch (Exception exception)
                {
                    return Fault<TransformState>(exception, "applying a temporary transform");
                }
            }
        }

        public OperationResult<bool> SetTemporarilyHidden(bool hidden)
        {
            lock (gate)
            {
                var usable = RequireUsable<bool>();
                if (usable != null) return usable;
                if ((Capabilities & CreatorSceneTargetCapabilities.TemporaryVisibility) == 0)
                {
                    return OperationResult<bool>.Failure(
                        ModErrorCode.InvalidArgument,
                        "This native scene target does not support temporary visibility edits.");
                }

                try
                {
                    return source!.SetTemporarilyHidden(hidden);
                }
                catch (Exception exception)
                {
                    return Fault<bool>(exception, "changing temporary visibility");
                }
            }
        }

        public OperationResult<bool> Restore()
        {
            ICreatorTemporaryEdit? completed = null;
            OperationResult<bool> result;
            lock (gate)
            {
                if (source == null) return OperationResult<bool>.Success(false);
                try
                {
                    result = source.Restore();
                }
                catch (Exception exception)
                {
                    return Fault<bool>(exception, "restoring a temporary edit");
                }

                if (!result.Succeeded) return result;
                completed = source;
                source = null;
            }

            Complete(completed);
            return result;
        }

        public void Dispose()
        {
            ICreatorTemporaryEdit? current;
            lock (gate)
            {
                current = source;
                source = null;
            }
            if (current == null) return;

            try
            {
                var restored = current.Restore();
                if (!restored.Succeeded)
                {
                    logger.Warn("Creator scene adapter '" + Target.AdapterId
                        + "' could not restore a temporary edit during cleanup: " + restored.ErrorMessage);
                }
            }
            catch (Exception exception)
            {
                logger.Error(exception, "Creator scene adapter '" + Target.AdapterId + "' threw while restoring a temporary edit during cleanup.");
            }

            Complete(current);
        }

        internal void InvalidateFromAdapter() => Dispose();
        internal void InvalidateFromSession() => Dispose();

        private OperationResult<T>? RequireUsable<T>() where T : notnull
        {
            if (source == null)
            {
                return OperationResult<T>.Failure(ModErrorCode.InvalidState, "The temporary scene edit has ended.");
            }
            if (sourceFaulted)
            {
                return OperationResult<T>.Failure(ModErrorCode.External, "The native scene adapter failed an earlier operation.");
            }
            if (!Target.IsAlive)
            {
                return OperationResult<T>.Failure(ModErrorCode.NotFound, "The native scene target is no longer available.");
            }

            try
            {
                return source.IsAlive
                    ? null
                    : OperationResult<T>.Failure(ModErrorCode.InvalidState, "The source temporary edit is no longer alive.");
            }
            catch (Exception exception)
            {
                return Fault<T>(exception, "checking a temporary edit");
            }
        }

        private OperationResult<T> Fault<T>(Exception exception, string operation) where T : notnull
        {
            sourceFaulted = true;
            logger.Error(exception, "Creator scene adapter '" + Target.AdapterId + "' threw while " + operation + ".");
            return OperationResult<T>.Failure(ModErrorCode.External, "The native scene adapter failed while " + operation + ".");
        }

        private void Complete(ICreatorTemporaryEdit current)
        {
            try
            {
                current.Dispose();
            }
            catch (Exception exception)
            {
                logger.Error(exception, "Creator scene adapter '" + Target.AdapterId + "' threw while releasing a temporary edit.");
            }

            session.Detach(this);
            registration.Detach(this);
            service.ReleaseSceneEdit(((CreatorSceneTargetHandle)Target).EntityId);
        }

        private static string? UnsupportedChanges(
            TransformState current,
            TransformState requested,
            CreatorSceneTargetCapabilities capabilities)
        {
            if (current.Position != requested.Position && (capabilities & CreatorSceneTargetCapabilities.Position) == 0)
            {
                return "This native scene target does not support position edits.";
            }
            if (current.Rotation != requested.Rotation && (capabilities & CreatorSceneTargetCapabilities.Rotation) == 0)
            {
                return "This native scene target does not support rotation edits.";
            }
            if (current.Scale != requested.Scale && (capabilities & CreatorSceneTargetCapabilities.Scale) == 0)
            {
                return "This native scene target does not support scale edits.";
            }
            return null;
        }
    }
}
