using System;
using System.Collections.Generic;

namespace TopiaForge.Mods
{
    /// <summary>Immutable authenticated metadata for one explicit native scene adapter.</summary>
    public sealed class CreatorSceneAdapterDescriptor
    {
        /// <summary>Creates scene-adapter metadata.</summary>
        public CreatorSceneAdapterDescriptor(
            string adapterId,
            string sourceId,
            string localId,
            string displayName)
        {
            if (string.IsNullOrWhiteSpace(adapterId)) throw new ArgumentException("An adapter id is required.", nameof(adapterId));
            if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("A source id is required.", nameof(sourceId));
            if (string.IsNullOrWhiteSpace(localId)) throw new ArgumentException("A local id is required.", nameof(localId));
            if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("A display name is required.", nameof(displayName));
            AdapterId = adapterId;
            SourceId = sourceId;
            LocalId = localId;
            DisplayName = displayName;
        }

        /// <summary>Gets the stable source-qualified adapter id used by native-binding recipes.</summary>
        public string AdapterId { get; }
        /// <summary>Gets the authenticated registering mod id.</summary>
        public string SourceId { get; }
        /// <summary>Gets the stable adapter id inside the registering mod.</summary>
        public string LocalId { get; }
        /// <summary>Gets the user-facing adapter name.</summary>
        public string DisplayName { get; }
    }

    /// <summary>
    /// Explicit source-owned bridge for a bounded set of reversible native scene targets.
    /// Implementations must use only native surfaces and assets owned or explicitly authorized by their mod;
    /// arbitrary Resources scans and cross-package loading are not permitted.
    /// </summary>
    /// <remarks>
    /// Returned targets and edits are source-local claims. Creator Content authenticates their adapter identity,
    /// validates catalog-duplicate recipes, enforces process-wide exclusive leases, and wraps every result before it
    /// reaches a creator session.
    /// </remarks>
    public interface ICreatorSceneAdapter
    {
        /// <summary>Resolves an entity only when this adapter explicitly recognizes it as reversible.</summary>
        OperationResult<ICreatorSceneTarget> ResolveSceneTarget(IEntity entity);
        /// <summary>Returns a bounded set of explicitly recognized targets without broad resource scanning.</summary>
        OperationResult<IReadOnlyList<ICreatorSceneTarget>> QuerySceneTargets(CreatorSceneQuery query);
        /// <summary>Captures a source-owned reversible edit for one target returned by this adapter.</summary>
        OperationResult<ICreatorTemporaryEdit> BeginTemporaryEdit(ICreatorSceneTarget target);
    }

    /// <summary>Requests one owner-authenticated native scene-adapter registration.</summary>
    public sealed class CreatorSceneAdapterRegistrationRequest
    {
        /// <summary>Creates a scene-adapter registration request.</summary>
        public CreatorSceneAdapterRegistrationRequest(
            string localId,
            string displayName,
            ICreatorSceneAdapter adapter)
        {
            if (string.IsNullOrWhiteSpace(localId)) throw new ArgumentException("A local id is required.", nameof(localId));
            if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("A display name is required.", nameof(displayName));
            LocalId = localId;
            DisplayName = displayName;
            Adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        }

        /// <summary>Gets the stable id inside the registering mod.</summary>
        public string LocalId { get; }
        /// <summary>Gets the user-facing adapter name.</summary>
        public string DisplayName { get; }
        /// <summary>Gets the explicit source-owned adapter.</summary>
        public ICreatorSceneAdapter Adapter { get; }
    }

    /// <summary>Lifetime handle for an authenticated scene-adapter registration.</summary>
    public interface ICreatorSceneAdapterRegistration : IDisposable
    {
        /// <summary>Gets authenticated adapter metadata.</summary>
        CreatorSceneAdapterDescriptor Descriptor { get; }
        /// <summary>Gets whether the adapter remains registered.</summary>
        bool IsAlive { get; }
    }

    /// <summary>
    /// Owner-bound registry for explicit native scene adapters. The provider derives the public adapter namespace
    /// from the authenticated calling mod; callers cannot register on behalf of another package.
    /// </summary>
    public interface ICreatorSceneAdapterRegistry
    {
        /// <summary>Registers an explicit reversible scene adapter for the authenticated calling mod.</summary>
        OperationResult<ICreatorSceneAdapterRegistration> RegisterSceneAdapter(
            CreatorSceneAdapterRegistrationRequest request);
    }
}
