using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// The network seam's entity service. Offline and on a listen host it changes no behaviour: every member forwards its
    /// arguments to the service the gameplay host made and returns exactly what that returns, failures and exceptions
    /// included. On a joined player, <c>SetTransform</c> and <c>Destroy</c> are routed (WS3.7): a change to an object of
    /// the player's own is a request the host validates, one on the player's intent to anything else the network world
    /// holds is refused, and a scripted one leaves the host's objects to the host's own run of the same script. An entity
    /// the network world does not hold is the game's, as offline. Online <c>AcquireMotion</c> wraps a known body in an
    /// optimistic ownership lease; the runtime streams it and retains a thrown body's simulation tail after disposal.
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

        public OperationResult<TransformState> SetTransform(IEntity entity, TransformState transform)
        {
            var authority = Replication.JoinedAuthority;
            if (authority == null) return inner.SetTransform(entity, transform);
            var route = Replication.RouteCurrentIntent();
            if (route != InternalRoute.Forwarded && route != InternalRoute.Shadow) return inner.SetTransform(entity, transform);
            return authority.RouteTransform(entity, transform, inner, route == InternalRoute.Shadow) ?? inner.SetTransform(entity, transform);
        }

        public IReadOnlyList<IEntity> Query(EntityQuery query) => inner.Query(query);

        public OperationResult<bool> Destroy(IEntity entity)
        {
            var authority = Replication.JoinedAuthority;
            if (authority == null) return inner.Destroy(entity);
            var route = Replication.RouteCurrentIntent();
            if (route != InternalRoute.Forwarded && route != InternalRoute.Shadow) return inner.Destroy(entity);
            return authority.RouteDestroy(entity, route == InternalRoute.Shadow) ?? inner.Destroy(entity);
        }

        public OperationResult<IEntityMotion> AcquireMotion(IEntity entity) =>
            Replication.Services.Motion?.Acquire(Replication, entity, inner) ?? inner.AcquireMotion(entity);
    }

    /// <summary>
    /// The network seam's item service. It changes no behaviour yet: every member forwards to the service the gameplay host
    /// made and returns exactly what that returns. The network world's held-item tracker observes native pickup edges.
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
