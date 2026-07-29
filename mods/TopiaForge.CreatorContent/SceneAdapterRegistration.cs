using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.CreatorContent
{
    internal sealed class SceneAdapterRegistration : ICreatorSceneAdapterRegistration
    {
        private readonly object gate = new object();
        private readonly CreatorContentService service;
        private readonly ICreatorSceneAdapter adapter;
        private readonly IModLifetime? ownerLifetime;
        private readonly IModLogger logger;
        private readonly List<CreatorTemporaryEditHandle> edits = new List<CreatorTemporaryEditHandle>();
        private bool alive = true;

        public SceneAdapterRegistration(
            CreatorContentService service,
            CreatorSceneAdapterDescriptor descriptor,
            ICreatorSceneAdapter adapter,
            IModLifetime? ownerLifetime,
            IModLogger logger)
        {
            this.service = service;
            Descriptor = descriptor;
            this.adapter = adapter;
            this.ownerLifetime = ownerLifetime;
            this.logger = logger;
        }

        public CreatorSceneAdapterDescriptor Descriptor { get; }

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

        public OperationResult<ICreatorSceneTarget> Resolve(IEntity entity)
        {
            if (!IsAlive)
            {
                return OperationResult<ICreatorSceneTarget>.Failure(
                    ModErrorCode.Unavailable,
                    "The native scene adapter is no longer available.");
            }

            try
            {
                return adapter.ResolveSceneTarget(entity)
                    ?? OperationResult<ICreatorSceneTarget>.Failure(
                        ModErrorCode.External,
                        "The native scene adapter returned no resolve result.");
            }
            catch (Exception exception)
            {
                logger.Error(exception, "Creator scene adapter '" + Descriptor.AdapterId + "' threw while resolving a target.");
                return OperationResult<ICreatorSceneTarget>.Failure(
                    ModErrorCode.External,
                    "The native scene adapter failed while resolving a target.");
            }
        }

        public OperationResult<IReadOnlyList<ICreatorSceneTarget>> Query(CreatorSceneQuery query)
        {
            if (!IsAlive)
            {
                return OperationResult<IReadOnlyList<ICreatorSceneTarget>>.Failure(
                    ModErrorCode.Unavailable,
                    "The native scene adapter is no longer available.");
            }

            try
            {
                return adapter.QuerySceneTargets(query)
                    ?? OperationResult<IReadOnlyList<ICreatorSceneTarget>>.Failure(
                        ModErrorCode.External,
                        "The native scene adapter returned no query result.");
            }
            catch (Exception exception)
            {
                logger.Error(exception, "Creator scene adapter '" + Descriptor.AdapterId + "' threw while querying targets.");
                return OperationResult<IReadOnlyList<ICreatorSceneTarget>>.Failure(
                    ModErrorCode.External,
                    "The native scene adapter failed while querying targets.");
            }
        }

        public OperationResult<ICreatorTemporaryEdit> BeginEdit(ICreatorSceneTarget target)
        {
            if (!IsAlive)
            {
                return OperationResult<ICreatorTemporaryEdit>.Failure(
                    ModErrorCode.Unavailable,
                    "The native scene adapter is no longer available.");
            }

            try
            {
                return adapter.BeginTemporaryEdit(target)
                    ?? OperationResult<ICreatorTemporaryEdit>.Failure(
                        ModErrorCode.External,
                        "The native scene adapter returned no temporary-edit result.");
            }
            catch (Exception exception)
            {
                logger.Error(exception, "Creator scene adapter '" + Descriptor.AdapterId + "' threw while beginning an edit.");
                return OperationResult<ICreatorTemporaryEdit>.Failure(
                    ModErrorCode.External,
                    "The native scene adapter failed while beginning an edit.");
            }
        }

        public bool Attach(CreatorTemporaryEditHandle edit)
        {
            lock (gate)
            {
                if (!alive || ownerLifetime?.IsStopping == true) return false;
                edits.Add(edit);
                return true;
            }
        }

        public void Detach(CreatorTemporaryEditHandle edit)
        {
            lock (gate)
            {
                edits.Remove(edit);
            }
        }

        public void Dispose()
        {
            CreatorTemporaryEditHandle[] active;
            lock (gate)
            {
                if (!alive) return;
                alive = false;
                active = edits.ToArray();
                edits.Clear();
            }

            service.Remove(this);
            for (var index = active.Length - 1; index >= 0; index--)
            {
                active[index].InvalidateFromAdapter();
            }
        }
    }
}
