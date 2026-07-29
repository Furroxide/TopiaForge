using System;
using System.Collections.Generic;

namespace TopiaForge.Mods
{
    /// <summary>Describes a bounded creator session.</summary>
    public sealed class CreatorSessionOptions
    {
        /// <summary>Creates session options.</summary>
        public CreatorSessionOptions(string purpose, int maximumInstances = 256)
        {
            if (string.IsNullOrWhiteSpace(purpose)) throw new ArgumentException("A session purpose is required.", nameof(purpose));
            if (maximumInstances < 1 || maximumInstances > 256) throw new ArgumentOutOfRangeException(nameof(maximumInstances));
            Purpose = purpose;
            MaximumInstances = maximumInstances;
        }

        /// <summary>Gets the diagnostic purpose.</summary>
        public string Purpose { get; }
        /// <summary>Gets the maximum concurrently live instances.</summary>
        public int MaximumInstances { get; }
    }

    /// <summary>Describes one catalog spawn.</summary>
    public sealed class CreatorSpawnRequest
    {
        /// <summary>Creates a spawn request.</summary>
        public CreatorSpawnRequest(string contentId, TransformState transform)
        {
            if (string.IsNullOrWhiteSpace(contentId)) throw new ArgumentException("A content id is required.", nameof(contentId));
            ContentId = contentId;
            Transform = transform;
        }

        /// <summary>Gets the stable catalog content id.</summary>
        public string ContentId { get; }
        /// <summary>Gets the initial world transform.</summary>
        public TransformState Transform { get; }
    }

    /// <summary>Describes a bounded query for editable scene targets.</summary>
    public sealed class CreatorSceneQuery
    {
        /// <summary>Creates a scene target query.</summary>
        public CreatorSceneQuery(
            Vec3? center = null,
            float radius = 0f,
            string nameContains = "",
            int maximumResults = 128,
            string adapterId = "")
        {
            if (center.HasValue && !center.Value.IsFinite) throw new ArgumentException("The center must be finite.", nameof(center));
            if (radius < 0f || float.IsNaN(radius) || float.IsInfinity(radius)) throw new ArgumentOutOfRangeException(nameof(radius));
            if (!center.HasValue && radius > 0f) throw new ArgumentException("A radius requires a center.", nameof(radius));
            if (maximumResults < 1 || maximumResults > 256) throw new ArgumentOutOfRangeException(nameof(maximumResults));
            if ((nameContains ?? string.Empty).Length > 128) throw new ArgumentOutOfRangeException(nameof(nameContains));
            if ((adapterId ?? string.Empty).Length > 128) throw new ArgumentOutOfRangeException(nameof(adapterId));
            Center = center;
            Radius = radius;
            NameContains = nameContains ?? string.Empty;
            MaximumResults = maximumResults;
            AdapterId = adapterId ?? string.Empty;
        }

        /// <summary>Gets the optional query center.</summary>
        public Vec3? Center { get; }
        /// <summary>Gets the inclusive radius, or zero for no distance filter.</summary>
        public float Radius { get; }
        /// <summary>Gets the optional case-insensitive name fragment.</summary>
        public string NameContains { get; }
        /// <summary>Gets the maximum result count.</summary>
        public int MaximumResults { get; }
        /// <summary>Gets an optional stable adapter id that returned targets must match.</summary>
        public string AdapterId { get; }
    }

    /// <summary>Identifies reversible operations supported by a borrowed scene target.</summary>
    [Flags]
    public enum CreatorSceneTargetCapabilities
    {
        /// <summary>No reversible mutation is supported.</summary>
        None = 0,
        /// <summary>Position can be changed and restored.</summary>
        Position = 1,
        /// <summary>Rotation can be changed and restored.</summary>
        Rotation = 2,
        /// <summary>Scale can be changed and restored.</summary>
        Scale = 4,
        /// <summary>Visibility can be changed and restored by a validated adapter.</summary>
        TemporaryVisibility = 8,
        /// <summary>The target maps to catalog content that can be duplicated as a new owned instance.</summary>
        CatalogDuplicate = 16,
        /// <summary>Every reversible transform component is supported.</summary>
        Transform = Position | Rotation | Scale
    }

    /// <summary>Represents one provider-approved borrowed scene object.</summary>
    public interface ICreatorSceneTarget
    {
        /// <summary>Gets the process-local target id; it must never be persisted.</summary>
        string Id { get; }
        /// <summary>Gets a user-facing name.</summary>
        string DisplayName { get; }
        /// <summary>Gets its best content classification.</summary>
        CreatorContentKind Kind { get; }
        /// <summary>Gets available reversible operations.</summary>
        CreatorSceneTargetCapabilities Capabilities { get; }
        /// <summary>Gets a stable catalog id when safe duplication is available, otherwise an empty string.</summary>
        string CatalogContentId { get; }
        /// <summary>Gets the stable native adapter id required by persisted scene bindings.</summary>
        string AdapterId { get; }
        /// <summary>Gets the safe runtime entity handle.</summary>
        IEntity Entity { get; }
        /// <summary>Gets whether the target remains valid in its original scene.</summary>
        bool IsAlive { get; }
    }

    /// <summary>Represents a tool-owned spawned instance.</summary>
    public interface ICreatorSpawnHandle : IDisposable
    {
        /// <summary>Gets the originating catalog descriptor.</summary>
        CreatorContentDescriptor Descriptor { get; }
        /// <summary>Gets the safe runtime entity handle.</summary>
        IEntity Entity { get; }
        /// <summary>Gets whether the spawned object remains usable.</summary>
        bool IsAlive { get; }
        /// <summary>Tries to read the current transform.</summary>
        bool TryGetTransform(out TransformState transform);
        /// <summary>Applies supported transform components.</summary>
        OperationResult<TransformState> SetTransform(TransformState transform);
        /// <summary>Creates a fresh owned instance from the same catalog source at an explicit transform.</summary>
        OperationResult<ICreatorSpawnHandle> Duplicate(TransformState transform);
        /// <summary>Destroys this tool-owned instance. Repeated calls succeed with <see langword="false"/>.</summary>
        OperationResult<bool> Despawn();
    }

    /// <summary>Exclusive reversible edit lease for a borrowed native scene target.</summary>
    public interface ICreatorTemporaryEdit : IDisposable
    {
        /// <summary>Gets the borrowed target.</summary>
        ICreatorSceneTarget Target { get; }
        /// <summary>Gets operations captured and supported by this lease.</summary>
        CreatorSceneTargetCapabilities Capabilities { get; }
        /// <summary>Gets whether the target and lease remain usable.</summary>
        bool IsAlive { get; }
        /// <summary>Tries to read the current transform.</summary>
        bool TryGetTransform(out TransformState transform);
        /// <summary>Applies supported transform components without committing them.</summary>
        OperationResult<TransformState> SetTransform(TransformState transform);
        /// <summary>Temporarily changes visibility when a validated adapter supports it.</summary>
        OperationResult<bool> SetTemporarilyHidden(bool hidden);
        /// <summary>Restores the captured state. Repeated calls succeed with <see langword="false"/>.</summary>
        OperationResult<bool> Restore();
    }

    /// <summary>Owns creator operations for one F5 opening or event-graph run.</summary>
    public interface ICreatorSession : IDisposable
    {
        /// <summary>Gets whether the session accepts work.</summary>
        bool IsAlive { get; }
        /// <summary>Gets the session options.</summary>
        CreatorSessionOptions Options { get; }
        /// <summary>Spawns tool-owned content.</summary>
        OperationResult<ICreatorSpawnHandle> Spawn(CreatorSpawnRequest request);
        /// <summary>Validates and wraps a runtime entity only when a safe native adapter recognizes it.</summary>
        OperationResult<ICreatorSceneTarget> ResolveSceneTarget(IEntity entity);
        /// <summary>Returns provider-approved native targets or a stable unavailable result.</summary>
        OperationResult<IReadOnlyList<ICreatorSceneTarget>> QuerySceneTargets(CreatorSceneQuery query);
        /// <summary>Captures an exclusive reversible snapshot for a provider-approved target.</summary>
        OperationResult<ICreatorTemporaryEdit> BeginTemporaryEdit(ICreatorSceneTarget target);
    }

    /// <summary>Owner-bound creator catalog and session service.</summary>
    public interface ICreatorContentService
    {
        /// <summary>Gets the latest immutable catalog snapshot.</summary>
        CreatorCatalogSnapshot Catalog { get; }
        /// <summary>Refreshes safe built-in sources and returns the latest snapshot.</summary>
        OperationResult<CreatorCatalogSnapshot> RefreshCatalog();
        /// <summary>Registers custom content attributed to the authenticated calling mod.</summary>
        OperationResult<ICreatorContentRegistration> Register(CreatorContentRegistrationRequest request);
        /// <summary>Begins a lifetime-owned creator session.</summary>
        OperationResult<ICreatorSession> BeginSession(CreatorSessionOptions options);
    }
}
