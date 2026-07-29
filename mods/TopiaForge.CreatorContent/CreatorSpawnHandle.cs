using System;
using TopiaForge.Mods;

namespace TopiaForge.CreatorContent
{
    internal sealed class CreatorSpawnHandle : ICreatorSpawnHandle
    {
        private readonly object gate = new object();
        private readonly CreatorSession session;
        private readonly ContentRegistration registration;
        private readonly IModLogger logger;
        private ICreatorSourceInstance? source;
        private bool sourceFaulted;
        private bool detached;

        public CreatorSpawnHandle(
            CreatorSession session,
            ContentRegistration registration,
            ICreatorSourceInstance source,
            IEntity entity,
            IModLogger logger)
        {
            this.session = session;
            this.registration = registration;
            this.source = source;
            Entity = entity;
            this.logger = logger;
        }

        public CreatorContentDescriptor Descriptor => registration.Descriptor;
        public IEntity Entity { get; }
        public bool IsAlive
        {
            get
            {
                lock (gate)
                {
                    if (source == null || sourceFaulted) return false;
                    try
                    {
                        return source.IsAlive && Entity.IsAlive;
                    }
                    catch (Exception exception)
                    {
                        sourceFaulted = true;
                        logger.Error(exception, "Creator source threw while checking '" + Descriptor.ContentId + "'.");
                        return false;
                    }
                }
            }
        }

        public bool TryGetTransform(out TransformState transform)
        {
            lock (gate)
            {
                if (source == null || sourceFaulted)
                {
                    transform = TransformState.Identity;
                    return false;
                }

                try
                {
                    return source.TryGetTransform(out transform);
                }
                catch (Exception exception)
                {
                    sourceFaulted = true;
                    logger.Error(exception, "Creator source threw while reading '" + Descriptor.ContentId + "'.");
                    transform = TransformState.Identity;
                    return false;
                }
            }
        }

        public OperationResult<TransformState> SetTransform(TransformState transform)
        {
            lock (gate)
            {
                if (source == null)
                {
                    return OperationResult<TransformState>.Failure(ModErrorCode.InvalidState, "The spawned creator instance is no longer alive.");
                }
                if (sourceFaulted)
                {
                    return OperationResult<TransformState>.Failure(ModErrorCode.External, "The content source failed an earlier liveness check.");
                }

                TransformState current;
                try
                {
                    if (!source.IsAlive)
                    {
                        return OperationResult<TransformState>.Failure(ModErrorCode.InvalidState, "The spawned creator instance is no longer alive.");
                    }
                    if (!source.TryGetTransform(out current))
                    {
                        return OperationResult<TransformState>.Failure(ModErrorCode.Unavailable, "The source could not read the current transform.");
                    }
                }
                catch (Exception exception)
                {
                    sourceFaulted = true;
                    logger.Error(exception, "Creator source threw while reading '" + Descriptor.ContentId + "'.");
                    return OperationResult<TransformState>.Failure(ModErrorCode.External, "The content source failed while reading its transform.");
                }

                var unsupported = UnsupportedChanges(current, transform, Descriptor.TransformCapabilities);
                if (unsupported != null)
                {
                    return OperationResult<TransformState>.Failure(ModErrorCode.InvalidArgument, unsupported);
                }

                try
                {
                    return source.SetTransform(transform);
                }
                catch (Exception exception)
                {
                    sourceFaulted = true;
                    logger.Error(exception, "Creator source threw while editing '" + Descriptor.ContentId + "'.");
                    return OperationResult<TransformState>.Failure(ModErrorCode.External, "The content source failed while applying its transform.");
                }
            }
        }

        public OperationResult<ICreatorSpawnHandle> Duplicate(TransformState transform)
        {
            lock (gate)
            {
                if (source == null)
                {
                    return OperationResult<ICreatorSpawnHandle>.Failure(ModErrorCode.InvalidState, "The spawned creator instance is no longer alive.");
                }
            }
            return session.Spawn(new CreatorSpawnRequest(Descriptor.ContentId, transform));
        }

        public OperationResult<bool> Despawn() => Release(detach: true);
        public void Dispose() => _ = Release(detach: true);
        public void InvalidateFromSource() => _ = Release(detach: true);
        public void InvalidateFromSession() => _ = Release(detach: true);

        private OperationResult<bool> Release(bool detach)
        {
            ICreatorSourceInstance? current;
            lock (gate)
            {
                current = source;
                source = null;
                if (current == null)
                {
                    return OperationResult<bool>.Success(false);
                }
            }

            if (detach && !detached)
            {
                detached = true;
                session.Detach(this);
                registration.Detach(this);
            }

            try
            {
                current.Dispose();
                return OperationResult<bool>.Success(true);
            }
            catch (Exception exception)
            {
                logger.Error(exception, "Creator source threw while despawning '" + Descriptor.ContentId + "'.");
                return OperationResult<bool>.Failure(ModErrorCode.External, "The content source failed while despawning its instance.");
            }
        }

        private static string? UnsupportedChanges(
            TransformState current,
            TransformState requested,
            CreatorTransformCapabilities capabilities)
        {
            if (current.Position != requested.Position && (capabilities & CreatorTransformCapabilities.Position) == 0)
            {
                return "This content does not support position edits.";
            }
            if (current.Rotation != requested.Rotation && (capabilities & CreatorTransformCapabilities.Rotation) == 0)
            {
                return "This content does not support rotation edits.";
            }
            if (current.Scale != requested.Scale && (capabilities & CreatorTransformCapabilities.Scale) == 0)
            {
                return "This content does not support scale edits.";
            }
            return null;
        }
    }
}
