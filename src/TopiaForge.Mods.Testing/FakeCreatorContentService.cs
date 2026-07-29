using System;
using System.Collections.Generic;
using System.Linq;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Deterministic in-memory creator catalog and owned-spawn service.</summary>
    public sealed class FakeCreatorContentService : ICreatorContentService, IDisposable
    {
        private readonly FakeModLifetime lifetime;
        private readonly List<Registration> registrations = new List<Registration>();
        private readonly List<Session> sessions = new List<Session>();
        private readonly List<FakeCreatorSceneTarget> sceneTargets = new List<FakeCreatorSceneTarget>();
        private long revision;
        private bool disposed;

        /// <summary>Creates a fake creator service.</summary>
        public FakeCreatorContentService(FakeModLifetime lifetime, string sourceId = "test.creator")
        {
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
            if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("A source id is required.", nameof(sourceId));
            SourceId = sourceId;
        }

        /// <summary>Gets the source id attributed by the fake.</summary>
        public string SourceId { get; }
        /// <summary>Gets the number of live custom registrations.</summary>
        public int ActiveRegistrationCount => registrations.Count(registration => registration.IsAlive);
        /// <summary>Gets the number of live creator sessions.</summary>
        public int ActiveSessionCount => sessions.Count(session => session.IsAlive);
        /// <summary>Gets the number of live spawned instances.</summary>
        public int ActiveInstanceCount => sessions.Sum(session => session.ActiveInstanceCount);
        /// <summary>Gets the number of live spawned instances.</summary>
        public int ActiveSpawnCount => ActiveInstanceCount;
        /// <summary>Gets the number of active exclusive temporary-edit leases.</summary>
        public int ActiveEditCount => sessions.Sum(session => session.ActiveEditCount);
        /// <summary>Gets the number of configured live native targets.</summary>
        public int ActiveSceneTargetCount => sceneTargets.Count(target => target.IsAlive);
        /// <summary>Gets or sets an expected failure returned by content registration.</summary>
        public ModErrorCode RegisterErrorCode { get; set; }
        /// <summary>Gets or sets an expected failure returned by new sessions.</summary>
        public ModErrorCode BeginSessionErrorCode { get; set; }
        /// <summary>Gets or sets an expected failure returned instead of invoking registered factories.</summary>
        public ModErrorCode FactoryErrorCode { get; set; }
        /// <summary>Gets or sets an expected failure returned by scene-target resolution.</summary>
        public ModErrorCode ResolveTargetErrorCode { get; set; }
        /// <summary>Gets or sets an expected failure returned by scene-target queries.</summary>
        public ModErrorCode QueryTargetsErrorCode { get; set; }
        /// <summary>Gets or sets an expected failure returned when beginning a temporary edit.</summary>
        public ModErrorCode BeginEditErrorCode { get; set; }
        /// <summary>Gets or sets an expected failure returned by temporary transform edits.</summary>
        public ModErrorCode EditTransformErrorCode { get; set; }
        /// <summary>Gets or sets an expected failure returned by temporary visibility edits.</summary>
        public ModErrorCode EditVisibilityErrorCode { get; set; }
        /// <summary>Gets or sets an expected failure returned by edit restoration.</summary>
        public ModErrorCode RestoreEditErrorCode { get; set; }

        /// <summary>Adds a provider-approved fake native target for resolve/query/edit tests.</summary>
        public void AddSceneTarget(FakeCreatorSceneTarget target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (disposed || lifetime.IsStopping) throw new ObjectDisposedException(nameof(FakeCreatorContentService));
            if (sceneTargets.Any(existing => string.Equals(existing.Id, target.Id, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("A fake creator scene target is already registered as '" + target.Id + "'.");
            }
            sceneTargets.Add(target);
        }

        /// <inheritdoc />
        public CreatorCatalogSnapshot Catalog => new CreatorCatalogSnapshot(
            revision,
            registrations.Where(registration => registration.IsAlive)
                .Select(registration => registration.Descriptor)
                .OrderBy(descriptor => descriptor.Kind)
                .ThenBy(descriptor => descriptor.DisplayName, StringComparer.OrdinalIgnoreCase),
            new[]
            {
                new CreatorCatalogSourceStatus(
                    SourceId,
                    SourceId,
                    CreatorCatalogSourceState.Ready,
                    string.Empty,
                    ActiveRegistrationCount)
            });

        /// <inheritdoc />
        public OperationResult<CreatorCatalogSnapshot> RefreshCatalog() => disposed
            ? OperationResult<CreatorCatalogSnapshot>.Failure(ModErrorCode.InvalidState, "The fake creator service is disposed.")
            : OperationResult<CreatorCatalogSnapshot>.Success(Catalog);

        /// <inheritdoc />
        public OperationResult<ICreatorContentRegistration> Register(CreatorContentRegistrationRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (RegisterErrorCode != ModErrorCode.None)
            {
                return OperationResult<ICreatorContentRegistration>.Failure(RegisterErrorCode, "The fake rejected creator registration.");
            }
            if (disposed || lifetime.IsStopping)
            {
                return OperationResult<ICreatorContentRegistration>.Failure(ModErrorCode.Cancelled, "The fake creator service is stopping.");
            }
            var contentId = SourceId.ToLowerInvariant() + ":" + request.LocalId.ToLowerInvariant();
            if (registrations.Any(registration => string.Equals(registration.Descriptor.ContentId, contentId, StringComparison.OrdinalIgnoreCase)))
            {
                return OperationResult<ICreatorContentRegistration>.Failure(ModErrorCode.Conflict, "The fake source already registered that local id.");
            }
            var descriptor = new CreatorContentDescriptor(
                contentId,
                SourceId,
                "1.0.0-test",
                request.LocalId,
                request.DisplayName,
                request.Description,
                request.Kind,
                request.TransformCapabilities);
            var registration = new Registration(this, descriptor, request.Factory);
            registrations.Add(registration);
            revision++;
            return lifetime.TrackResult<ICreatorContentRegistration>(
                registration,
                registration.AttachLifetimeLease,
                "The fake lifetime stopped during creator registration.");
        }

        /// <inheritdoc />
        public OperationResult<ICreatorSession> BeginSession(CreatorSessionOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (BeginSessionErrorCode != ModErrorCode.None)
            {
                return OperationResult<ICreatorSession>.Failure(BeginSessionErrorCode, "The fake rejected the creator session.");
            }
            if (disposed || lifetime.IsStopping)
            {
                return OperationResult<ICreatorSession>.Failure(ModErrorCode.Cancelled, "The fake creator service is stopping.");
            }
            var session = new Session(this, options);
            sessions.Add(session);
            return lifetime.TrackResult<ICreatorSession>(
                session,
                session.AttachLifetimeLease,
                "The fake lifetime stopped during creator session creation.");
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var session in sessions.ToArray()) session.Dispose();
            foreach (var registration in registrations.ToArray()) registration.Dispose();
            sceneTargets.Clear();
        }

        private Registration? Find(string contentId) => registrations.FirstOrDefault(registration =>
            registration.IsAlive && string.Equals(registration.Descriptor.ContentId, contentId, StringComparison.OrdinalIgnoreCase));

        private FakeCreatorSceneTarget? Find(IEntity entity) => sceneTargets.FirstOrDefault(target =>
            target.IsAlive && ReferenceEquals(target.Entity, entity));

        private void Remove(Registration registration)
        {
            if (registrations.Remove(registration)) revision++;
        }

        private void Remove(Session session) => sessions.Remove(session);

        private sealed class Registration : ICreatorContentRegistration
        {
            private readonly FakeCreatorContentService service;
            private readonly List<SpawnHandle> instances = new List<SpawnHandle>();
            private IDisposable? lifetimeLease;
            private bool alive = true;

            public Registration(FakeCreatorContentService service, CreatorContentDescriptor descriptor, ICreatorContentFactory factory)
            {
                this.service = service;
                Descriptor = descriptor;
                Factory = factory;
            }

            public CreatorContentDescriptor Descriptor { get; }
            public ICreatorContentFactory Factory { get; }
            public bool IsAlive => alive;
            public void AttachLifetimeLease(IDisposable lease) =>
                lifetimeLease = lease ?? throw new ArgumentNullException(nameof(lease));
            public void Attach(SpawnHandle handle) => instances.Add(handle);
            public void Detach(SpawnHandle handle) => instances.Remove(handle);
            public void Dispose()
            {
                if (!alive) return;
                alive = false;
                service.Remove(this);
                var active = instances.ToArray();
                for (var index = active.Length - 1; index >= 0; index--) active[index].Dispose();
                System.Threading.Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
            }
        }

        private sealed class Session : ICreatorSession
        {
            private readonly FakeCreatorContentService service;
            private readonly List<SpawnHandle> instances = new List<SpawnHandle>();
            private readonly List<FakeCreatorTemporaryEdit> edits = new List<FakeCreatorTemporaryEdit>();
            private IDisposable? lifetimeLease;
            private bool alive = true;

            public Session(FakeCreatorContentService service, CreatorSessionOptions options)
            {
                this.service = service;
                Options = options;
            }

            public bool IsAlive => alive;
            public CreatorSessionOptions Options { get; }
            public int ActiveInstanceCount => instances.Count(instance => instance.IsAlive);
            public int ActiveEditCount => edits.Count(edit => edit.IsAlive);
            public void AttachLifetimeLease(IDisposable lease) =>
                lifetimeLease = lease ?? throw new ArgumentNullException(nameof(lease));

            public OperationResult<ICreatorSpawnHandle> Spawn(CreatorSpawnRequest request)
            {
                if (request == null) throw new ArgumentNullException(nameof(request));
                if (!alive) return OperationResult<ICreatorSpawnHandle>.Failure(ModErrorCode.InvalidState, "The fake session is disposed.");
                if (ActiveInstanceCount >= Options.MaximumInstances)
                {
                    return OperationResult<ICreatorSpawnHandle>.Failure(ModErrorCode.RateLimited, "The fake session reached its instance limit.");
                }
                if (service.FactoryErrorCode != ModErrorCode.None)
                {
                    return OperationResult<ICreatorSpawnHandle>.Failure(
                        service.FactoryErrorCode,
                        "The fake rejected the registered creator factory.");
                }
                var registration = service.Find(request.ContentId);
                if (registration == null) return OperationResult<ICreatorSpawnHandle>.Failure(ModErrorCode.NotFound, "The fake content id is not registered.");
                OperationResult<ICreatorSourceInstance> result;
                try
                {
                    result = registration.Factory.Spawn(request.Transform);
                }
                catch (Exception exception)
                {
                    return OperationResult<ICreatorSpawnHandle>.Failure(ModErrorCode.External, exception.Message);
                }
                if (!result.TryGetValue(out var source))
                {
                    return OperationResult<ICreatorSpawnHandle>.Failure(result.ErrorCode, result.ErrorMessage);
                }
                SpawnHandle handle;
                try
                {
                    var entity = source.Entity;
                    if (!source.IsAlive || entity == null || !entity.IsAlive)
                    {
                        source.Dispose();
                        return OperationResult<ICreatorSpawnHandle>.Failure(ModErrorCode.External, "The fake factory returned an unusable instance.");
                    }
                    handle = new SpawnHandle(this, registration, source, entity);
                }
                catch (Exception exception)
                {
                    try { source.Dispose(); } catch { }
                    return OperationResult<ICreatorSpawnHandle>.Failure(ModErrorCode.External, exception.Message);
                }
                instances.Add(handle);
                registration.Attach(handle);
                return OperationResult<ICreatorSpawnHandle>.Success(handle);
            }

            public OperationResult<ICreatorSceneTarget> ResolveSceneTarget(IEntity entity)
            {
                if (entity == null) throw new ArgumentNullException(nameof(entity));
                if (!alive) return OperationResult<ICreatorSceneTarget>.Failure(ModErrorCode.InvalidState, "The fake session is disposed.");
                if (service.ResolveTargetErrorCode != ModErrorCode.None)
                {
                    return OperationResult<ICreatorSceneTarget>.Failure(
                        service.ResolveTargetErrorCode,
                        "The fake rejected scene-target resolution.");
                }
                var target = service.Find(entity);
                return target == null
                    ? OperationResult<ICreatorSceneTarget>.Failure(ModErrorCode.NotFound, "The fake entity is not an approved scene target.")
                    : OperationResult<ICreatorSceneTarget>.Success(target);
            }

            public OperationResult<IReadOnlyList<ICreatorSceneTarget>> QuerySceneTargets(CreatorSceneQuery query)
            {
                if (query == null) throw new ArgumentNullException(nameof(query));
                if (!alive) return OperationResult<IReadOnlyList<ICreatorSceneTarget>>.Failure(ModErrorCode.InvalidState, "The fake session is disposed.");
                if (service.QueryTargetsErrorCode != ModErrorCode.None)
                {
                    return OperationResult<IReadOnlyList<ICreatorSceneTarget>>.Failure(
                        service.QueryTargetsErrorCode,
                        "The fake rejected the scene-target query.");
                }

                var radiusSquared = query.Radius * query.Radius;
                var found = service.sceneTargets
                    .Where(target => target.IsAlive
                        && (query.AdapterId.Length == 0
                            || string.Equals(target.AdapterId, query.AdapterId, StringComparison.OrdinalIgnoreCase))
                        && (query.NameContains.Length == 0
                            || target.DisplayName.IndexOf(query.NameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                        && (!query.Center.HasValue || query.Radius <= 0f
                            || (target.Entity.Position - query.Center.Value).LengthSquared <= radiusSquared))
                    .Take(query.MaximumResults)
                    .Cast<ICreatorSceneTarget>()
                    .ToArray();
                return OperationResult<IReadOnlyList<ICreatorSceneTarget>>.Success(found);
            }

            public OperationResult<ICreatorTemporaryEdit> BeginTemporaryEdit(ICreatorSceneTarget target)
            {
                if (target == null) throw new ArgumentNullException(nameof(target));
                if (!alive) return OperationResult<ICreatorTemporaryEdit>.Failure(ModErrorCode.InvalidState, "The fake session is disposed.");
                if (service.BeginEditErrorCode != ModErrorCode.None)
                {
                    return OperationResult<ICreatorTemporaryEdit>.Failure(
                        service.BeginEditErrorCode,
                        "The fake rejected the temporary edit.");
                }
                if (!(target is FakeCreatorSceneTarget fake) || !service.sceneTargets.Contains(fake))
                {
                    return OperationResult<ICreatorTemporaryEdit>.Failure(ModErrorCode.InvalidArgument, "The target is not owned by this fake creator service.");
                }
                if (!fake.IsAlive)
                {
                    return OperationResult<ICreatorTemporaryEdit>.Failure(ModErrorCode.NotFound, "The fake scene target is no longer alive.");
                }

                var edit = new FakeCreatorTemporaryEdit(
                    fake,
                    () => service.EditTransformErrorCode,
                    () => service.EditVisibilityErrorCode,
                    () => service.RestoreEditErrorCode,
                    Detach);
                if (!fake.TryAcquire(edit))
                {
                    edit.Abandon();
                    return OperationResult<ICreatorTemporaryEdit>.Failure(ModErrorCode.Conflict, "The fake scene target already has an exclusive edit lease.");
                }
                edits.Add(edit);
                return OperationResult<ICreatorTemporaryEdit>.Success(edit);
            }

            public void Detach(SpawnHandle handle) => instances.Remove(handle);
            public void Detach(FakeCreatorTemporaryEdit edit) => edits.Remove(edit);
            public void Dispose()
            {
                if (!alive) return;
                alive = false;
                var activeEdits = edits.ToArray();
                for (var index = activeEdits.Length - 1; index >= 0; index--) activeEdits[index].Dispose();
                var active = instances.ToArray();
                for (var index = active.Length - 1; index >= 0; index--) active[index].Dispose();
                service.Remove(this);
                System.Threading.Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
            }
        }

        private sealed class SpawnHandle : ICreatorSpawnHandle
        {
            private readonly Session session;
            private readonly Registration registration;
            private ICreatorSourceInstance? source;

            public SpawnHandle(
                Session session,
                Registration registration,
                ICreatorSourceInstance source,
                IEntity entity)
            {
                this.session = session;
                this.registration = registration;
                this.source = source;
                Entity = entity;
            }

            public CreatorContentDescriptor Descriptor => registration.Descriptor;
            public IEntity Entity { get; }
            public bool IsAlive => source?.IsAlive == true && Entity.IsAlive;
            public bool TryGetTransform(out TransformState transform)
            {
                if (source != null) return source.TryGetTransform(out transform);
                transform = TransformState.Identity;
                return false;
            }
            public OperationResult<TransformState> SetTransform(TransformState transform) => source == null
                ? OperationResult<TransformState>.Failure(ModErrorCode.InvalidState, "The fake spawn is disposed.")
                : source.SetTransform(transform);
            public OperationResult<ICreatorSpawnHandle> Duplicate(TransformState transform) =>
                session.Spawn(new CreatorSpawnRequest(Descriptor.ContentId, transform));
            public OperationResult<bool> Despawn()
            {
                var current = source;
                source = null;
                if (current == null) return OperationResult<bool>.Success(false);
                session.Detach(this);
                registration.Detach(this);
                current.Dispose();
                return OperationResult<bool>.Success(true);
            }
            public void Dispose() => _ = Despawn();
        }
    }
}
