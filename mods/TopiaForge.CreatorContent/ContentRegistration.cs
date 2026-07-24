using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.CreatorContent
{
    internal sealed class ContentRegistration : ICreatorContentRegistration
    {
        private readonly object gate = new object();
        private readonly CreatorContentService service;
        private readonly ICreatorContentFactory factory;
        private readonly IModLifetime? ownerLifetime;
        private readonly IModLogger logger;
        private readonly List<CreatorSpawnHandle> instances = new List<CreatorSpawnHandle>();
        private bool alive = true;

        public ContentRegistration(
            CreatorContentService service,
            CreatorContentDescriptor descriptor,
            ICreatorContentFactory factory,
            IModLifetime? ownerLifetime,
            IModLogger logger)
        {
            this.service = service;
            Descriptor = descriptor;
            this.factory = factory;
            this.ownerLifetime = ownerLifetime;
            this.logger = logger;
        }

        public CreatorContentDescriptor Descriptor { get; }
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

        public OperationResult<ICreatorSourceInstance> Spawn(TransformState transform)
        {
            lock (gate)
            {
                if (!alive || ownerLifetime?.IsStopping == true)
                {
                    return OperationResult<ICreatorSourceInstance>.Failure(
                        ModErrorCode.Unavailable,
                        "The content source is no longer available.");
                }
            }

            try
            {
                return factory.Spawn(transform);
            }
            catch (Exception exception)
            {
                logger.Error(exception, "Creator factory '" + Descriptor.ContentId + "' threw while spawning.");
                return OperationResult<ICreatorSourceInstance>.Failure(
                    ModErrorCode.External,
                    "The content source failed while spawning '" + Descriptor.DisplayName + "'.");
            }
        }

        public bool Attach(CreatorSpawnHandle instance)
        {
            lock (gate)
            {
                if (!alive || ownerLifetime?.IsStopping == true) return false;
                if (instances.Contains(instance)) return true;
                instances.Add(instance);
                return true;
            }
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
            lock (gate)
            {
                if (!alive) return;
                alive = false;
                active = new CreatorSpawnHandle[instances.Count];
                instances.CopyTo(active);
                instances.Clear();
            }

            service.Remove(this);
            for (var index = active.Length - 1; index >= 0; index--)
            {
                active[index].InvalidateFromSource();
            }
        }
    }
}
