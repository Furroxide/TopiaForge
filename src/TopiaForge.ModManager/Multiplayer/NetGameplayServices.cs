using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// The network seam's entity service. It changes no behaviour yet: every member forwards its arguments to the service
    /// the gameplay host made and returns exactly what that returns, failures and exceptions included. Authority routing
    /// (WS3.7) makes <c>SetTransform</c> and <c>Destroy</c> validated requests on joined players here, and
    /// <c>AcquireMotion</c> the motion lease GravityGun holds.
    /// </summary>
    internal sealed class NetEntityService : IEntityService
    {
        private readonly IEntityService inner;

        internal NetEntityService(IEntityService inner, RuntimeReplication replication)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            Replication = replication ?? throw new ArgumentNullException(nameof(replication));
        }

        /// <summary>Gets the service the gameplay host made.</summary>
        internal IEntityService Inner => inner;

        /// <summary>Gets the replication seam of the context this service belongs to.</summary>
        internal RuntimeReplication Replication { get; }

        public bool TryGetTransform(IEntity entity, out TransformState transform) => inner.TryGetTransform(entity, out transform);

        public OperationResult<TransformState> SetTransform(IEntity entity, TransformState transform) => inner.SetTransform(entity, transform);

        public IReadOnlyList<IEntity> Query(EntityQuery query) => inner.Query(query);

        public OperationResult<bool> Destroy(IEntity entity) => inner.Destroy(entity);

        public OperationResult<IEntityMotion> AcquireMotion(IEntity entity) => inner.AcquireMotion(entity);
    }

    /// <summary>
    /// The network seam's item service. It changes no behaviour yet: every member forwards to the service the gameplay host
    /// made and returns exactly what that returns. Authority routing (WS3.7) tracks native held-item ownership here.
    /// </summary>
    internal sealed class NetItemService : IItemService
    {
        private readonly IItemService inner;

        internal NetItemService(IItemService inner, RuntimeReplication replication)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            Replication = replication ?? throw new ArgumentNullException(nameof(replication));
        }

        /// <summary>Gets the service the gameplay host made.</summary>
        internal IItemService Inner => inner;

        /// <summary>Gets the replication seam of the context this service belongs to.</summary>
        internal RuntimeReplication Replication { get; }

        public bool TryGetHeld(out HeldItemSnapshot? item) => inner.TryGetHeld(out item);

        public Task<OperationResult<HeldItemSnapshot>> GiveAsync(ItemGrantRequest request, CancellationToken cancellationToken = default) =>
            inner.GiveAsync(request, cancellationToken);

        public Task<OperationResult<IEntity>> DropHeldAsync(Vec3 velocity, CancellationToken cancellationToken = default) =>
            inner.DropHeldAsync(velocity, cancellationToken);
    }

    /// <summary>
    /// The network seam's UI service. It changes no behaviour yet: every member forwards to the service the gameplay host
    /// made and returns exactly what that returns. Presentation routing (WS3.12) mirrors a host gamemode server's HUD and
    /// broadcasts its toasts here.
    /// </summary>
    internal sealed class NetUiService : IUiService
    {
        private readonly IUiService inner;

        internal NetUiService(IUiService inner, RuntimeReplication replication)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            Replication = replication ?? throw new ArgumentNullException(nameof(replication));
        }

        /// <summary>Gets the service the gameplay host made.</summary>
        internal IUiService Inner => inner;

        /// <summary>Gets the replication seam of the context this service belongs to.</summary>
        internal RuntimeReplication Replication { get; }

        public UiAccessibilityPreferences Accessibility => inner.Accessibility;

        public OperationResult<UiAccessibilityPreferences> ApplyAccessibility(UiAccessibilityPreferences preferences) =>
            inner.ApplyAccessibility(preferences);

        public OperationResult<IUiSurface> CreateSurface(UiSurfaceRequest request) => inner.CreateSurface(request);

        public OperationResult<IUiModal> ShowModal(UiModalRequest request, Action<bool> completed) => inner.ShowModal(request, completed);

        public OperationResult<bool> ShowToast(string message, UiTone tone = UiTone.Neutral) => inner.ShowToast(message, tone);
    }
}
