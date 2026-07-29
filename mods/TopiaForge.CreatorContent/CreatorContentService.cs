using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.CreatorContent
{
    internal sealed partial class CreatorContentService :
        ICreatorContentService,
        ICreatorSceneAdapterRegistry,
        IOwnerBoundExtensionFactory,
        IDisposable
    {
        private const int MaximumCatalogEntries = 4096;
        private const int MaximumSceneAdapters = 64;
        private readonly object gate = new object();
        private readonly string providerId;
        private readonly IRuntimeInfo runtime;
        private readonly IModLogger logger;
        private readonly List<ContentRegistration> registrations = new List<ContentRegistration>();
        private readonly List<SceneAdapterRegistration> sceneAdapters = new List<SceneAdapterRegistration>();
        private readonly List<CreatorSession> sessions = new List<CreatorSession>();
        private readonly HashSet<string> activeSceneEditEntities = new HashSet<string>(StringComparer.Ordinal);
        private IReadOnlyList<CreatorCatalogSourceStatus> builtInStatuses = DefaultBuiltInStatuses();
        private Func<OperationResult<bool>>? refreshBuiltIns;
        private CreatorCatalogSnapshot? catalogSnapshot;
        private long revision;
        private bool disposed;

        public CreatorContentService(string providerId, IRuntimeInfo runtime, IModLogger logger)
        {
            this.providerId = providerId ?? string.Empty;
            this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public CreatorCatalogSnapshot Catalog
        {
            get
            {
                lock (gate)
                {
                    return CurrentCatalogLocked();
                }
            }
        }

        public OperationResult<CreatorCatalogSnapshot> RefreshCatalog()
        {
            lock (gate)
            {
                if (disposed)
                {
                    return OperationResult<CreatorCatalogSnapshot>.Failure(ModErrorCode.InvalidState, "Creator Content is disposed.");
                }
            }

            var refresh = refreshBuiltIns;
            if (refresh != null)
            {
                var refreshed = refresh();
                if (!refreshed.Succeeded)
                {
                    return OperationResult<CreatorCatalogSnapshot>.Failure(refreshed.ErrorCode, refreshed.ErrorMessage);
                }
            }
            lock (gate)
            {
                return disposed
                    ? OperationResult<CreatorCatalogSnapshot>.Failure(ModErrorCode.InvalidState, "Creator Content is disposed.")
                    : OperationResult<CreatorCatalogSnapshot>.Success(CurrentCatalogLocked());
            }
        }

        public OperationResult<ICreatorContentRegistration> Register(CreatorContentRegistrationRequest request) =>
            Register(providerId, null, request);

        public OperationResult<ICreatorSession> BeginSession(CreatorSessionOptions options) =>
            BeginSession(providerId, null, options);

        public OperationResult<ICreatorSceneAdapterRegistration> RegisterSceneAdapter(
            CreatorSceneAdapterRegistrationRequest request) =>
            RegisterSceneAdapter(providerId, null, request);

        object IOwnerBoundExtensionFactory.CreateOwnerFacade(
            Type contractType,
            string ownerModId,
            IModLifetime lifetime)
        {
            if (contractType != typeof(ICreatorContentService)
                && contractType != typeof(ICreatorSceneAdapterRegistry))
            {
                throw new ArgumentException("Unsupported Creator Content extension contract.", nameof(contractType));
            }

            return new OwnerFacade(this, ownerModId, lifetime);
        }

        internal OperationResult<ICreatorContentRegistration> Register(
            string ownerId,
            IModLifetime? ownerLifetime,
            CreatorContentRegistrationRequest request,
            string? sourceVersionOverride = null)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var validation = ValidateRegistration(request);
            if (validation != null)
            {
                return OperationResult<ICreatorContentRegistration>.Failure(ModErrorCode.InvalidArgument, validation);
            }

            lock (gate)
            {
                if (disposed)
                {
                    return OperationResult<ICreatorContentRegistration>.Failure(ModErrorCode.InvalidState, "Creator Content is disposed.");
                }
                if (ownerLifetime?.IsStopping == true)
                {
                    return OperationResult<ICreatorContentRegistration>.Failure(ModErrorCode.Cancelled, "The source mod is stopping.");
                }
                if (registrations.Count >= MaximumCatalogEntries)
                {
                    return OperationResult<ICreatorContentRegistration>.Failure(ModErrorCode.RateLimited, "The creator catalog reached its entry limit.");
                }

                var contentId = CreatorIds.Qualify(ownerId, request.LocalId);
                if (registrations.Any(entry => string.Equals(entry.Descriptor.ContentId, contentId, StringComparison.OrdinalIgnoreCase)))
                {
                    return OperationResult<ICreatorContentRegistration>.Failure(ModErrorCode.Conflict, "That source already registered the local content id.");
                }

                var sourceVersion = sourceVersionOverride ?? (runtime.ProviderVersions.TryGetValue(ownerId, out var version)
                    ? version.ToString()
                    : string.Empty);
                var descriptor = new CreatorContentDescriptor(
                    contentId,
                    ownerId,
                    sourceVersion,
                    request.LocalId,
                    request.DisplayName,
                    request.Description,
                    request.Kind,
                    request.TransformCapabilities);
                var registration = new ContentRegistration(
                    this,
                    descriptor,
                    request.Factory,
                    ownerLifetime,
                    logger);
                registrations.Add(registration);
                revision++;
                return OperationResult<ICreatorContentRegistration>.Success(registration);
            }
        }

        internal OperationResult<ICreatorContentRegistration> RegisterBuiltIn(
            string sourceId,
            string sourceVersion,
            CreatorContentRegistrationRequest request) =>
            Register(sourceId, null, request, sourceVersion);

        internal OperationResult<ICreatorSceneAdapterRegistration> RegisterSceneAdapter(
            string ownerId,
            IModLifetime? ownerLifetime,
            CreatorSceneAdapterRegistrationRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (!CreatorIds.IsLocalId(request.LocalId, 64))
            {
                return OperationResult<ICreatorSceneAdapterRegistration>.Failure(
                    ModErrorCode.InvalidArgument,
                    "Local scene-adapter ids must use 1-64 letters, digits, dots, underscores, or hyphens.");
            }
            if (request.DisplayName.Length > 128)
            {
                return OperationResult<ICreatorSceneAdapterRegistration>.Failure(
                    ModErrorCode.InvalidArgument,
                    "Scene-adapter display name exceeds 128 characters.");
            }

            var adapterId = CreatorIds.QualifyAdapter(ownerId, request.LocalId);
            if (!CreatorIds.IsLocalId(adapterId, 128))
            {
                return OperationResult<ICreatorSceneAdapterRegistration>.Failure(
                    ModErrorCode.InvalidArgument,
                    "The authenticated scene-adapter id exceeds the portable 128-character limit.");
            }

            lock (gate)
            {
                if (disposed)
                {
                    return OperationResult<ICreatorSceneAdapterRegistration>.Failure(
                        ModErrorCode.InvalidState,
                        "Creator Content is disposed.");
                }
                if (ownerLifetime?.IsStopping == true)
                {
                    return OperationResult<ICreatorSceneAdapterRegistration>.Failure(
                        ModErrorCode.Cancelled,
                        "The scene-adapter source mod is stopping.");
                }
                if (sceneAdapters.Count >= MaximumSceneAdapters)
                {
                    return OperationResult<ICreatorSceneAdapterRegistration>.Failure(
                        ModErrorCode.RateLimited,
                        "Creator Content reached its 64 scene-adapter limit.");
                }
                if (sceneAdapters.Any(adapter => string.Equals(
                    adapter.Descriptor.AdapterId,
                    adapterId,
                    StringComparison.OrdinalIgnoreCase)))
                {
                    return OperationResult<ICreatorSceneAdapterRegistration>.Failure(
                        ModErrorCode.Conflict,
                        "That source already registered the local scene-adapter id.");
                }

                var descriptor = new CreatorSceneAdapterDescriptor(
                    adapterId,
                    ownerId,
                    request.LocalId,
                    request.DisplayName);
                var registration = new SceneAdapterRegistration(
                    this,
                    descriptor,
                    request.Adapter,
                    ownerLifetime,
                    logger);
                sceneAdapters.Add(registration);
                return OperationResult<ICreatorSceneAdapterRegistration>.Success(registration);
            }
        }

        internal void SetBuiltInRefresher(Func<OperationResult<bool>> refresher)
        {
            refreshBuiltIns = refresher ?? throw new ArgumentNullException(nameof(refresher));
        }

        internal void UpdateBuiltInStatuses(IEnumerable<CreatorCatalogSourceStatus> statuses)
        {
            if (statuses == null) throw new ArgumentNullException(nameof(statuses));
            var next = statuses.ToArray();
            lock (gate)
            {
                if (!SameStatuses(builtInStatuses, next))
                {
                    builtInStatuses = next;
                    revision++;
                }
            }
        }

        internal OperationResult<ICreatorSession> BeginSession(
            string ownerId,
            IModLifetime? ownerLifetime,
            CreatorSessionOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.Purpose.Length > 128)
            {
                return OperationResult<ICreatorSession>.Failure(ModErrorCode.InvalidArgument, "Session purpose exceeds 128 characters.");
            }

            lock (gate)
            {
                if (disposed)
                {
                    return OperationResult<ICreatorSession>.Failure(ModErrorCode.InvalidState, "Creator Content is disposed.");
                }
                if (ownerLifetime?.IsStopping == true)
                {
                    return OperationResult<ICreatorSession>.Failure(ModErrorCode.Cancelled, "The consumer mod is stopping.");
                }

                var session = new CreatorSession(this, ownerId, ownerLifetime, options, logger);
                sessions.Add(session);
                return OperationResult<ICreatorSession>.Success(session);
            }
        }

        internal bool TryResolveRegistration(string contentId, out ContentRegistration? registration)
        {
            lock (gate)
            {
                registration = registrations.FirstOrDefault(entry =>
                    entry.IsAlive && string.Equals(entry.Descriptor.ContentId, contentId, StringComparison.OrdinalIgnoreCase));
                return registration != null;
            }
        }

        internal void Remove(ContentRegistration registration)
        {
            lock (gate)
            {
                if (registrations.Remove(registration))
                {
                    revision++;
                }
            }
        }

        internal void Remove(CreatorSession session)
        {
            lock (gate)
            {
                sessions.Remove(session);
            }
        }

        internal void Remove(SceneAdapterRegistration registration)
        {
            lock (gate)
            {
                sceneAdapters.Remove(registration);
            }
        }

        internal IReadOnlyList<SceneAdapterRegistration> SceneAdapters(string adapterId)
        {
            lock (gate)
            {
                return sceneAdapters
                    .Where(adapter => adapter.IsAlive
                        && (string.IsNullOrEmpty(adapterId)
                            || string.Equals(
                                adapter.Descriptor.AdapterId,
                                adapterId,
                                StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(adapter => adapter.Descriptor.AdapterId, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }

        internal bool TryReserveSceneEdit(string entityId)
        {
            lock (gate)
            {
                return !disposed && activeSceneEditEntities.Add(entityId);
            }
        }

        internal void ReleaseSceneEdit(string entityId)
        {
            lock (gate)
            {
                activeSceneEditEntities.Remove(entityId);
            }
        }

        public void OnSceneChanged()
        {
            CreatorSession[] active;
            lock (gate)
            {
                active = sessions.ToArray();
            }
            for (var index = active.Length - 1; index >= 0; index--)
            {
                active[index].Dispose();
            }
        }

        public void Dispose()
        {
            CreatorSession[] activeSessions;
            ContentRegistration[] activeRegistrations;
            SceneAdapterRegistration[] activeAdapters;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                activeSessions = sessions.ToArray();
                activeRegistrations = registrations.ToArray();
                activeAdapters = sceneAdapters.ToArray();
            }

            for (var index = activeSessions.Length - 1; index >= 0; index--) activeSessions[index].Dispose();
            for (var index = activeAdapters.Length - 1; index >= 0; index--) activeAdapters[index].Dispose();
            for (var index = activeRegistrations.Length - 1; index >= 0; index--) activeRegistrations[index].Dispose();
        }

        private CreatorCatalogSnapshot CreateCatalogLocked()
        {
            var entries = registrations
                .Where(entry => entry.IsAlive)
                .Select(entry => entry.Descriptor)
                .OrderBy(entry => entry.Kind)
                .ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.ContentId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var sourceMap = entries
                .GroupBy(entry => entry.SourceId, StringComparer.OrdinalIgnoreCase)
                .Select(group => new CreatorCatalogSourceStatus(
                    group.Key,
                    group.Key,
                    CreatorCatalogSourceState.Ready,
                    string.Empty,
                    group.Count()))
                .ToDictionary(source => source.SourceId, StringComparer.OrdinalIgnoreCase);
            foreach (var status in builtInStatuses)
            {
                sourceMap[status.SourceId] = status;
            }
            var sources = sourceMap.Values
                .OrderBy(source => source.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(source => source.SourceId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new CreatorCatalogSnapshot(revision, entries, sources);
        }

        private CreatorCatalogSnapshot CurrentCatalogLocked()
        {
            if (catalogSnapshot == null || catalogSnapshot.Revision != revision)
            {
                catalogSnapshot = CreateCatalogLocked();
            }
            return catalogSnapshot;
        }

        private static IReadOnlyList<CreatorCatalogSourceStatus> DefaultBuiltInStatuses()
        {
            const string reason = "A curated clean-room adapter is not installed; unsafe native scanning is disabled.";
            return new[]
            {
                new CreatorCatalogSourceStatus("robotopia.items", "Robotopia items", CreatorCatalogSourceState.Unavailable, reason, 0),
                new CreatorCatalogSourceStatus("robotopia.ugc-props", "Robotopia UGC props", CreatorCatalogSourceState.Unavailable, reason, 0),
                new CreatorCatalogSourceStatus("robotopia.vehicles", "Robotopia vehicles", CreatorCatalogSourceState.Unavailable, reason, 0),
                new CreatorCatalogSourceStatus("robotopia.characters", "Robotopia characters", CreatorCatalogSourceState.Unavailable, reason, 0)
            };
        }

        private static bool SameStatuses(
            IReadOnlyList<CreatorCatalogSourceStatus> first,
            IReadOnlyList<CreatorCatalogSourceStatus> second)
        {
            if (first.Count != second.Count) return false;
            var left = first.OrderBy(status => status.SourceId, StringComparer.OrdinalIgnoreCase).ToArray();
            var right = second.OrderBy(status => status.SourceId, StringComparer.OrdinalIgnoreCase).ToArray();
            for (var index = 0; index < left.Length; index++)
            {
                if (!string.Equals(left[index].SourceId, right[index].SourceId, StringComparison.OrdinalIgnoreCase)
                    || left[index].DisplayName != right[index].DisplayName
                    || left[index].State != right[index].State
                    || left[index].Message != right[index].Message
                    || left[index].EntryCount != right[index].EntryCount)
                {
                    return false;
                }
            }
            return true;
        }

        private static string? ValidateRegistration(CreatorContentRegistrationRequest request)
        {
            if (!CreatorIds.IsLocalId(request.LocalId)) return "Local content ids must use 1-128 letters, digits, dots, underscores, or hyphens.";
            if (request.DisplayName.Length > 128) return "Display name exceeds 128 characters.";
            if (request.Description.Length > 512) return "Description exceeds 512 characters.";
            return null;
        }
    }
}
