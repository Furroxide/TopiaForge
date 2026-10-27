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
    /// optimistic ownership lease; the runtime streams it and retains a thrown body's simulation tail after disposal. A
    /// blocker is the one exception in every realm: a move keeps the size it was made with, and nothing acquires its motion.
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
            transform = KeepBlockerSize(entity, transform);
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

        public OperationResult<IEntityMotion> AcquireMotion(IEntity entity)
        {
            // A blocker stays where its code puts it: no tool holds or throws one, whichever handle to it the tool has.
            if (IsBlocker(entity)) return OperationResult<IEntityMotion>.Failure(ModErrorCode.NotAuthoritative, NetPhysicsService.NotHeld);
            return Replication.Services.Motion?.Acquire(Replication, entity, inner) ?? inner.AcquireMotion(entity);
        }

        private static readonly Vec3 Unit = new Vec3(1f, 1f, 1f);

        private bool IsBlocker(IEntity? entity) => entity != null && (entity is IBlocker || Replication.Services.IsBlocker(entity));

        /// <summary>
        /// A blocker keeps the size it was made with, as <c>BlockerRequest.Box</c> ignores a placement's scale: a scale here
        /// would take the host's box past its bounds while every player's copy, built from its description, kept its size.
        /// </summary>
        private TransformState KeepBlockerSize(IEntity? entity, TransformState transform) =>
            transform.Scale == Unit || !IsBlocker(entity) ? transform : new TransformState(transform.Position, transform.Rotation, Unit);
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
    /// Routes an exact consuming context's presentation. Targeted player UI uses its separate authenticated path.
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

        public OperationResult<IUiSurface> CreateSurface(UiSurfaceRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            UnityMainThreadGuard.AssertCurrent();
            var presentation = Replication.Services.Players?.Presentation;
            var profile = Replication.Profile;
            // Retain an eligible solo HUD too, so opening the existing game to players needs no author rebuild.
            var eligible = Core.Multiplayer.PresentationRoutingPolicy.Route(Core.Multiplayer.NetRealm.ListenHost, profile,
                Core.Multiplayer.PresentationKind.HudSurface) == Core.Multiplayer.PresentationRoute.MirrorToEveryone;
            return request.Kind == UiSurfaceKind.Hud && presentation != null && eligible
                ? presentation.CreateHud(Replication, inner, request) : inner.CreateSurface(request);
        }

        public OperationResult<IUiModal> ShowModal(UiModalRequest request, Action<bool> completed) => inner.ShowModal(request, completed);

        public OperationResult<bool> ShowToast(string message, UiTone tone = UiTone.Neutral)
        {
            UnityMainThreadGuard.AssertCurrent();
            var presentation = Replication.Services.Players?.Presentation;
            return presentation != null && Core.Multiplayer.PresentationRoutingPolicy.Route(Replication.Services.Runtime.Realm,
                Replication.Profile, Core.Multiplayer.PresentationKind.Toast) == Core.Multiplayer.PresentationRoute.BroadcastToEveryone
                ? presentation.Toast(Replication, inner, message, tone) : inner.ShowToast(message, tone);
        }
    }
}
