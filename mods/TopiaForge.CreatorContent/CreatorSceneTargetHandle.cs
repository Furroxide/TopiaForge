using System;
using TopiaForge.Mods;

namespace TopiaForge.CreatorContent
{
    internal sealed class CreatorSceneTargetHandle : ICreatorSceneTarget
    {
        private readonly object gate = new object();
        private readonly CreatorSession session;
        private readonly ICreatorSceneTarget source;
        private readonly IModLogger logger;
        private bool invalidated;
        private bool sourceFaulted;

        public CreatorSceneTargetHandle(
            CreatorSession session,
            SceneAdapterRegistration registration,
            ICreatorSceneTarget source,
            string sourceTargetId,
            string displayName,
            CreatorContentKind kind,
            CreatorSceneTargetCapabilities capabilities,
            string catalogContentId,
            IEntity entity,
            string entityId,
            IModLogger logger)
        {
            this.session = session;
            Registration = registration;
            this.source = source;
            SourceTargetId = sourceTargetId;
            DisplayName = displayName;
            Kind = kind;
            Capabilities = capabilities;
            CatalogContentId = catalogContentId;
            Entity = entity;
            EntityId = entityId;
            this.logger = logger;
            Id = registration.Descriptor.AdapterId + ":" + sourceTargetId;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public CreatorContentKind Kind { get; }
        public CreatorSceneTargetCapabilities Capabilities { get; }
        public string CatalogContentId { get; }
        public string AdapterId => Registration.Descriptor.AdapterId;
        public IEntity Entity { get; }

        public bool IsAlive
        {
            get
            {
                if (!session.IsAlive || !Registration.IsAlive)
                {
                    return false;
                }

                bool sourceAlive;
                lock (gate)
                {
                    if (invalidated || sourceFaulted)
                    {
                        return false;
                    }

                    try
                    {
                        sourceAlive = source.IsAlive && Entity.IsAlive;
                    }
                    catch (Exception exception)
                    {
                        sourceFaulted = true;
                        logger.Error(exception, "Creator scene adapter '" + AdapterId + "' threw while checking target '" + Id + "'.");
                        return false;
                    }
                }

                return sourceAlive && session.IsAlive && Registration.IsAlive;
            }
        }

        internal SceneAdapterRegistration Registration { get; }
        internal ICreatorSceneTarget Source => source;
        internal string SourceTargetId { get; }
        internal string EntityId { get; }
        internal string CacheKey => AdapterId + "\u001f" + SourceTargetId;

        internal bool BelongsTo(CreatorSession expectedSession) => ReferenceEquals(session, expectedSession);

        internal bool Matches(SceneAdapterRegistration registration, string sourceTargetId, string entityId) =>
            ReferenceEquals(Registration, registration)
            && string.Equals(SourceTargetId, sourceTargetId, StringComparison.Ordinal)
            && string.Equals(EntityId, entityId, StringComparison.Ordinal);

        internal void InvalidateFromSession()
        {
            lock (gate)
            {
                invalidated = true;
            }
        }
    }
}
