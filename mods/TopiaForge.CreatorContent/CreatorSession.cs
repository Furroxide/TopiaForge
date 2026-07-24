using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.CreatorContent
{
    internal sealed partial class CreatorSession : ICreatorSession
    {
        private readonly object gate = new object();
        private readonly CreatorContentService service;
        private readonly string ownerId;
        private readonly IModLifetime? ownerLifetime;
        private readonly IModLogger logger;
        private readonly List<CreatorSpawnHandle> instances = new List<CreatorSpawnHandle>();
        private bool alive = true;

        public CreatorSession(
            CreatorContentService service,
            string ownerId,
            IModLifetime? ownerLifetime,
            CreatorSessionOptions options,
            IModLogger logger)
        {
            this.service = service;
            this.ownerId = ownerId;
            this.ownerLifetime = ownerLifetime;
            Options = options;
            this.logger = logger;
        }

        public bool IsAlive
        {
            get
            {
                lock (gate)
                {
                    return alive && ownerLifetime?.IsStopping != true;
                }
            }
        }

        public CreatorSessionOptions Options { get; }

        public OperationResult<ICreatorSpawnHandle> Spawn(CreatorSpawnRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            lock (gate)
            {
                if (!alive || ownerLifetime?.IsStopping == true)
                {
                    return OperationResult<ICreatorSpawnHandle>.Failure(ModErrorCode.Cancelled, "The creator session is stopping.");
                }
                if (instances.Count >= Options.MaximumInstances)
                {
                    return OperationResult<ICreatorSpawnHandle>.Failure(ModErrorCode.RateLimited, "The creator session reached its instance limit.");
                }
            }

            if (!service.TryResolveRegistration(request.ContentId, out var registration) || registration == null)
            {
                return OperationResult<ICreatorSpawnHandle>.Failure(ModErrorCode.NotFound, "The requested creator content is not registered.");
            }

            var spawned = registration.Spawn(request.Transform);
            if (!spawned.TryGetValue(out var source))
            {
                return OperationResult<ICreatorSpawnHandle>.Failure(spawned.ErrorCode, spawned.ErrorMessage);
            }

            IEntity entity;
            try
            {
                entity = source.Entity;
                if (!source.IsAlive || entity == null || !entity.IsAlive)
                {
                    SafeDispose(source, "A creator source returned an unusable instance.");
                    return OperationResult<ICreatorSpawnHandle>.Failure(ModErrorCode.External, "The content source returned an unusable instance.");
                }
            }
            catch (Exception exception)
            {
                logger.Error(exception, "A creator source threw while exposing its spawned entity.");
                SafeDispose(source, "A creator source failed while exposing its spawned entity.");
                return OperationResult<ICreatorSpawnHandle>.Failure(ModErrorCode.External, "The content source returned an unusable instance.");
            }

            var handle = new CreatorSpawnHandle(this, registration, source, entity, logger);
            lock (gate)
            {
                if (!alive || ownerLifetime?.IsStopping == true || instances.Count >= Options.MaximumInstances)
                {
                    handle.InvalidateFromSession();
                    return OperationResult<ICreatorSpawnHandle>.Failure(ModErrorCode.Cancelled, "The creator session stopped while spawning.");
                }
                if (!registration.Attach(handle))
                {
                    handle.InvalidateFromSession();
                    return OperationResult<ICreatorSpawnHandle>.Failure(ModErrorCode.Unavailable, "The content source stopped while spawning.");
                }
                instances.Add(handle);
            }

            return OperationResult<ICreatorSpawnHandle>.Success(handle);
        }

        public void Detach(CreatorSpawnHandle instance)
        {
            lock (gate)
            {
                instances.Remove(instance);
            }
        }

        public void Dispose()
        {
            CreatorSpawnHandle[] active;
            CreatorTemporaryEditHandle[] activeEdits;
            CreatorSceneTargetHandle[] activeTargets;
            lock (gate)
            {
                if (!alive) return;
                alive = false;
                active = instances.ToArray();
                instances.Clear();
                CaptureSceneResourcesLocked(out activeEdits, out activeTargets);
            }

            for (var index = activeEdits.Length - 1; index >= 0; index--)
            {
                activeEdits[index].InvalidateFromSession();
            }
            for (var index = activeTargets.Length - 1; index >= 0; index--)
            {
                activeTargets[index].InvalidateFromSession();
            }
            for (var index = active.Length - 1; index >= 0; index--)
            {
                active[index].InvalidateFromSession();
            }
            service.Remove(this);
        }

        internal string OwnerId => ownerId;

        private void SafeDispose(IDisposable resource, string message)
        {
            try
            {
                resource.Dispose();
            }
            catch (Exception exception)
            {
                logger.Error(exception, message);
            }
        }
    }
}
