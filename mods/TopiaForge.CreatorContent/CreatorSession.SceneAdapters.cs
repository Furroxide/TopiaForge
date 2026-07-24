using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.CreatorContent
{
    internal sealed partial class CreatorSession
    {
        private const int MaximumSceneTargets = 256;
        private const CreatorSceneTargetCapabilities AllTargetCapabilities =
            CreatorSceneTargetCapabilities.Transform
            | CreatorSceneTargetCapabilities.TemporaryVisibility
            | CreatorSceneTargetCapabilities.CatalogDuplicate;
        private const CreatorSceneTargetCapabilities EditCapabilities =
            CreatorSceneTargetCapabilities.Transform
            | CreatorSceneTargetCapabilities.TemporaryVisibility;

        private readonly List<CreatorTemporaryEditHandle> edits = new List<CreatorTemporaryEditHandle>();
        private readonly Dictionary<string, CreatorSceneTargetHandle> targetsByKey =
            new Dictionary<string, CreatorSceneTargetHandle>(StringComparer.Ordinal);
        private readonly Dictionary<string, CreatorSceneTargetHandle> targetsByEntity =
            new Dictionary<string, CreatorSceneTargetHandle>(StringComparer.Ordinal);

        public OperationResult<ICreatorSceneTarget> ResolveSceneTarget(IEntity entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            var available = RequireSceneSession();
            if (available != null) return OperationResult<ICreatorSceneTarget>.Failure(available.Value.Code, available.Value.Message);

            try
            {
                if (!entity.IsAlive || !IsSafeRuntimeId(entity.Id, 256))
                {
                    return OperationResult<ICreatorSceneTarget>.Failure(
                        ModErrorCode.NotFound,
                        "The scene entity is no longer available or has no bounded runtime identity.");
                }
            }
            catch (Exception exception)
            {
                logger.Error(exception, "A scene entity threw while Creator Content checked it for native editing.");
                return OperationResult<ICreatorSceneTarget>.Failure(ModErrorCode.External, "The scene entity could not be validated.");
            }

            var adapters = service.SceneAdapters(string.Empty);
            if (adapters.Count == 0) return NoAdapters<ICreatorSceneTarget>();

            CreatorSceneTargetHandle? match = null;
            foreach (var adapter in adapters)
            {
                var resolved = adapter.Resolve(entity);
                if (!resolved.TryGetValue(out var source))
                {
                    if (resolved.ErrorCode == ModErrorCode.NotFound) continue;
                    return OperationResult<ICreatorSceneTarget>.Failure(resolved.ErrorCode, resolved.ErrorMessage);
                }

                var wrapped = WrapTarget(adapter, source, entity);
                if (!wrapped.TryGetValue(out var target))
                {
                    return OperationResult<ICreatorSceneTarget>.Failure(wrapped.ErrorCode, wrapped.ErrorMessage);
                }
                if (match != null && !ReferenceEquals(match, target))
                {
                    return OperationResult<ICreatorSceneTarget>.Failure(
                        ModErrorCode.Conflict,
                        "More than one authenticated scene adapter claimed the same entity.");
                }
                match = target;
            }

            return match == null
                ? OperationResult<ICreatorSceneTarget>.Failure(ModErrorCode.NotFound, "No authenticated scene adapter recognized that entity.")
                : OperationResult<ICreatorSceneTarget>.Success(match);
        }

        public OperationResult<IReadOnlyList<ICreatorSceneTarget>> QuerySceneTargets(CreatorSceneQuery query)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            var available = RequireSceneSession();
            if (available != null)
            {
                return OperationResult<IReadOnlyList<ICreatorSceneTarget>>.Failure(available.Value.Code, available.Value.Message);
            }
            if (!string.IsNullOrEmpty(query.AdapterId) && !CreatorIds.IsLocalId(query.AdapterId, 128))
            {
                return OperationResult<IReadOnlyList<ICreatorSceneTarget>>.Failure(
                    ModErrorCode.InvalidArgument,
                    "The requested scene-adapter id is not portable.");
            }

            var adapters = service.SceneAdapters(query.AdapterId);
            if (adapters.Count == 0) return NoAdapters<IReadOnlyList<ICreatorSceneTarget>>();

            var matches = new List<CreatorSceneTargetHandle>(query.MaximumResults);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var adapter in adapters)
            {
                var remaining = query.MaximumResults - matches.Count;
                if (remaining == 0) break;
                var sourceQuery = new CreatorSceneQuery(
                    query.Center,
                    query.Radius,
                    query.NameContains,
                    remaining,
                    string.Empty);
                var queried = adapter.Query(sourceQuery);
                if (!queried.TryGetValue(out var sources))
                {
                    return OperationResult<IReadOnlyList<ICreatorSceneTarget>>.Failure(queried.ErrorCode, queried.ErrorMessage);
                }

                int count;
                try
                {
                    count = sources.Count;
                }
                catch (Exception exception)
                {
                    logger.Error(exception, "Creator scene adapter '" + adapter.Descriptor.AdapterId + "' returned a faulting target list.");
                    return AdapterFailure<IReadOnlyList<ICreatorSceneTarget>>("querying native scene targets");
                }
                if (count > remaining)
                {
                    return AdapterFailure<IReadOnlyList<ICreatorSceneTarget>>("honoring the bounded scene query");
                }

                for (var index = 0; index < count; index++)
                {
                    ICreatorSceneTarget source;
                    try
                    {
                        source = sources[index];
                    }
                    catch (Exception exception)
                    {
                        logger.Error(exception, "Creator scene adapter '" + adapter.Descriptor.AdapterId + "' returned a faulting target list.");
                        return AdapterFailure<IReadOnlyList<ICreatorSceneTarget>>("querying native scene targets");
                    }

                    var wrapped = WrapTarget(adapter, source, null);
                    if (!wrapped.TryGetValue(out var target))
                    {
                        return OperationResult<IReadOnlyList<ICreatorSceneTarget>>.Failure(wrapped.ErrorCode, wrapped.ErrorMessage);
                    }
                    var filter = ValidateQueryMatch(target, query);
                    if (filter != null)
                    {
                        return OperationResult<IReadOnlyList<ICreatorSceneTarget>>.Failure(filter.Value.Code, filter.Value.Message);
                    }
                    if (seen.Add(target.Id)) matches.Add(target);
                }
            }

            var ordered = matches
                .OrderBy(target => target.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(target => target.Id, StringComparer.Ordinal)
                .Cast<ICreatorSceneTarget>()
                .ToArray();
            return OperationResult<IReadOnlyList<ICreatorSceneTarget>>.Success(ordered);
        }

        public OperationResult<ICreatorTemporaryEdit> BeginTemporaryEdit(ICreatorSceneTarget target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            var available = RequireSceneSession();
            if (available != null) return OperationResult<ICreatorTemporaryEdit>.Failure(available.Value.Code, available.Value.Message);
            if (!(target is CreatorSceneTargetHandle wrapped) || !wrapped.BelongsTo(this))
            {
                return OperationResult<ICreatorTemporaryEdit>.Failure(
                    ModErrorCode.InvalidArgument,
                    "Temporary edits require a target returned by this creator session.");
            }
            if (!wrapped.IsAlive)
            {
                return OperationResult<ICreatorTemporaryEdit>.Failure(ModErrorCode.NotFound, "The native scene target is no longer available.");
            }
            if ((wrapped.Capabilities & EditCapabilities) == 0)
            {
                return OperationResult<ICreatorTemporaryEdit>.Failure(
                    ModErrorCode.Unavailable,
                    "The native scene target exposes no reversible edit capabilities.");
            }
            if (!service.TryReserveSceneEdit(wrapped.EntityId))
            {
                return OperationResult<ICreatorTemporaryEdit>.Failure(
                    ModErrorCode.Conflict,
                    "Another creator session already holds the exclusive edit lease for this entity.");
            }

            var begun = wrapped.Registration.BeginEdit(wrapped.Source);
            if (!begun.TryGetValue(out var source))
            {
                service.ReleaseSceneEdit(wrapped.EntityId);
                return OperationResult<ICreatorTemporaryEdit>.Failure(begun.ErrorCode, begun.ErrorMessage);
            }

            var validated = ValidateEditSource(wrapped, source);
            if (!validated.TryGetValue(out var capabilities))
            {
                SafeDispose(source, "A scene adapter returned an unusable temporary edit.");
                service.ReleaseSceneEdit(wrapped.EntityId);
                return OperationResult<ICreatorTemporaryEdit>.Failure(validated.ErrorCode, validated.ErrorMessage);
            }

            var edit = new CreatorTemporaryEditHandle(
                service,
                this,
                wrapped.Registration,
                wrapped,
                source,
                capabilities,
                logger);
            if (!wrapped.Registration.Attach(edit))
            {
                edit.Dispose();
                return OperationResult<ICreatorTemporaryEdit>.Failure(
                    ModErrorCode.Unavailable,
                    "The native scene adapter stopped while the edit was being captured.");
            }
            if (!Attach(edit))
            {
                edit.Dispose();
                return OperationResult<ICreatorTemporaryEdit>.Failure(
                    ModErrorCode.Cancelled,
                    "The creator session stopped while the edit was being captured.");
            }

            return OperationResult<ICreatorTemporaryEdit>.Success(edit);
        }

        internal void Detach(CreatorTemporaryEditHandle edit)
        {
            lock (gate)
            {
                edits.Remove(edit);
            }
        }

        private bool Attach(CreatorTemporaryEditHandle edit)
        {
            lock (gate)
            {
                if (!alive || ownerLifetime?.IsStopping == true) return false;
                edits.Add(edit);
                return true;
            }
        }

        private OperationResult<CreatorSceneTargetHandle> WrapTarget(
            SceneAdapterRegistration registration,
            ICreatorSceneTarget source,
            IEntity? expectedEntity)
        {
            if (!registration.IsAlive)
            {
                return OperationResult<CreatorSceneTargetHandle>.Failure(
                    ModErrorCode.Unavailable,
                    "The native scene adapter is no longer available.");
            }
            if (source == null)
            {
                return AdapterFailure<CreatorSceneTargetHandle>("returning a native scene target");
            }

            string sourceTargetId;
            string displayName;
            CreatorContentKind kind;
            CreatorSceneTargetCapabilities capabilities;
            string catalogContentId;
            IEntity entity;
            string entityId;
            try
            {
                sourceTargetId = source.Id;
                displayName = source.DisplayName;
                kind = source.Kind;
                capabilities = source.Capabilities;
                catalogContentId = source.CatalogContentId;
                entity = source.Entity;
                entityId = entity?.Id ?? string.Empty;
                if (!source.IsAlive || entity == null || !entity.IsAlive)
                {
                    return OperationResult<CreatorSceneTargetHandle>.Failure(ModErrorCode.NotFound, "The native scene target is no longer available.");
                }
            }
            catch (Exception exception)
            {
                logger.Error(exception, "Creator scene adapter '" + registration.Descriptor.AdapterId + "' threw while describing a target.");
                return AdapterFailure<CreatorSceneTargetHandle>("describing a native scene target");
            }

            if (!IsSafeRuntimeId(sourceTargetId, 128)
                || !IsDisplayName(displayName)
                || !IsSafeRuntimeId(entityId, 256)
                || !Enum.IsDefined(typeof(CreatorContentKind), kind)
                || (capabilities & ~AllTargetCapabilities) != 0)
            {
                return AdapterFailure<CreatorSceneTargetHandle>("describing a bounded native scene target");
            }
            if (expectedEntity != null && !ReferenceEquals(entity, expectedEntity))
            {
                return AdapterFailure<CreatorSceneTargetHandle>("preserving resolved scene-entity identity");
            }

            var duplicate = (capabilities & CreatorSceneTargetCapabilities.CatalogDuplicate) != 0;
            if (duplicate)
            {
                if (string.IsNullOrWhiteSpace(catalogContentId)
                    || catalogContentId.Length > 256
                    || !service.TryResolveRegistration(catalogContentId, out var content)
                    || content == null
                    || !string.Equals(
                        content.Descriptor.SourceId,
                        registration.Descriptor.SourceId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return AdapterFailure<CreatorSceneTargetHandle>("authenticating a catalog duplicate recipe");
                }
            }
            else if (!string.IsNullOrEmpty(catalogContentId))
            {
                return AdapterFailure<CreatorSceneTargetHandle>("describing a catalog duplicate recipe");
            }

            var key = registration.Descriptor.AdapterId + "\u001f" + sourceTargetId;
            lock (gate)
            {
                if (!alive || ownerLifetime?.IsStopping == true)
                {
                    return OperationResult<CreatorSceneTargetHandle>.Failure(ModErrorCode.Cancelled, "The creator session is stopping.");
                }
                if (!registration.IsAlive)
                {
                    return OperationResult<CreatorSceneTargetHandle>.Failure(
                        ModErrorCode.Unavailable,
                        "The native scene adapter stopped while a target was being validated.");
                }
                if (targetsByKey.TryGetValue(key, out var existingByKey))
                {
                    if (existingByKey.Matches(registration, sourceTargetId, entityId) && existingByKey.IsAlive)
                    {
                        return OperationResult<CreatorSceneTargetHandle>.Success(existingByKey);
                    }
                    if (existingByKey.IsAlive)
                    {
                        return OperationResult<CreatorSceneTargetHandle>.Failure(
                            ModErrorCode.Conflict,
                            "A scene adapter reused one target id for different entities.");
                    }
                    RemoveTargetLocked(existingByKey);
                }
                if (targetsByEntity.TryGetValue(entityId, out var existingByEntity))
                {
                    if (existingByEntity.IsAlive)
                    {
                        return OperationResult<CreatorSceneTargetHandle>.Failure(
                            ModErrorCode.Conflict,
                            "More than one authenticated scene target claimed the same entity.");
                    }
                    RemoveTargetLocked(existingByEntity);
                }
                if (targetsByKey.Count >= MaximumSceneTargets)
                {
                    return OperationResult<CreatorSceneTargetHandle>.Failure(
                        ModErrorCode.RateLimited,
                        "The creator session reached its 256 native-target limit.");
                }

                var target = new CreatorSceneTargetHandle(
                    this,
                    registration,
                    source,
                    sourceTargetId,
                    displayName,
                    kind,
                    capabilities,
                    duplicate ? catalogContentId : string.Empty,
                    entity,
                    entityId,
                    logger);
                targetsByKey.Add(target.CacheKey, target);
                targetsByEntity.Add(entityId, target);
                return OperationResult<CreatorSceneTargetHandle>.Success(target);
            }
        }

        private OperationResult<CreatorSceneTargetCapabilities> ValidateEditSource(
            CreatorSceneTargetHandle target,
            ICreatorTemporaryEdit source)
        {
            if (source == null) return AdapterFailure<CreatorSceneTargetCapabilities>("returning a temporary edit");
            try
            {
                var capabilities = source.Capabilities;
                if (!ReferenceEquals(source.Target, target.Source)
                    || !source.IsAlive
                    || (capabilities & ~AllTargetCapabilities) != 0
                    || (capabilities & ~target.Capabilities) != 0)
                {
                    return AdapterFailure<CreatorSceneTargetCapabilities>("describing a temporary edit");
                }

                capabilities &= EditCapabilities;
                return capabilities == CreatorSceneTargetCapabilities.None
                    ? OperationResult<CreatorSceneTargetCapabilities>.Failure(
                        ModErrorCode.Unavailable,
                        "The scene adapter captured no reversible edit capabilities.")
                    : OperationResult<CreatorSceneTargetCapabilities>.Success(capabilities);
            }
            catch (Exception exception)
            {
                logger.Error(exception, "Creator scene adapter '" + target.AdapterId + "' threw while describing a temporary edit.");
                return AdapterFailure<CreatorSceneTargetCapabilities>("describing a temporary edit");
            }
        }

        private (ModErrorCode Code, string Message)? ValidateQueryMatch(
            CreatorSceneTargetHandle target,
            CreatorSceneQuery query)
        {
            if (!string.IsNullOrEmpty(query.NameContains)
                && target.DisplayName.IndexOf(query.NameContains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return (ModErrorCode.External, "A native scene adapter returned a target outside the requested name filter.");
            }
            if (query.Center.HasValue && query.Radius > 0f)
            {
                try
                {
                    var position = target.Entity.Position;
                    if (!position.IsFinite || Vec3.Distance(position, query.Center.Value) > query.Radius)
                    {
                        return (ModErrorCode.External, "A native scene adapter returned a target outside the requested radius.");
                    }
                }
                catch (Exception exception)
                {
                    logger.Error(exception, "Creator scene adapter '" + target.AdapterId + "' threw while exposing target position.");
                    return (ModErrorCode.External, "A native scene adapter failed while applying the requested radius.");
                }
            }
            return null;
        }

        private (ModErrorCode Code, string Message)? RequireSceneSession()
        {
            lock (gate)
            {
                return alive && ownerLifetime?.IsStopping != true
                    ? ((ModErrorCode, string)?)null
                    : (ModErrorCode.Cancelled, "The creator session is stopping.");
            }
        }

        private void CaptureSceneResourcesLocked(
            out CreatorTemporaryEditHandle[] activeEdits,
            out CreatorSceneTargetHandle[] activeTargets)
        {
            activeEdits = edits.ToArray();
            edits.Clear();
            activeTargets = new List<CreatorSceneTargetHandle>(targetsByKey.Values).ToArray();
            targetsByKey.Clear();
            targetsByEntity.Clear();
        }

        private void RemoveTargetLocked(CreatorSceneTargetHandle target)
        {
            targetsByKey.Remove(target.CacheKey);
            if (targetsByEntity.TryGetValue(target.EntityId, out var entityTarget)
                && ReferenceEquals(entityTarget, target))
            {
                targetsByEntity.Remove(target.EntityId);
            }
            target.InvalidateFromSession();
        }

        private static bool IsSafeRuntimeId(string value, int maximumLength)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength) return false;
            for (var index = 0; index < value.Length; index++)
            {
                if (char.IsControl(value[index])) return false;
            }
            return true;
        }

        private static bool IsDisplayName(string value) =>
            !string.IsNullOrWhiteSpace(value) && value.Length <= 128;

        private static OperationResult<T> NoAdapters<T>() where T : notnull =>
            OperationResult<T>.Failure(
                ModErrorCode.Unavailable,
                "No authenticated native scene adapter is available; unsafe object scanning remains disabled.");

        private static OperationResult<T> AdapterFailure<T>(string operation) where T : notnull =>
            OperationResult<T>.Failure(ModErrorCode.External, "The native scene adapter failed while " + operation + ".");
    }
}
