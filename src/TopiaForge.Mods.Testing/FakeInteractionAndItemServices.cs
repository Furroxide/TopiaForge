using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Deterministic interactable registry with explicit focus and invocation controls.</summary>
    public sealed class FakeInteractionService : IInteractionService
    {
        private readonly FakeModLifetime lifetime;
        private readonly List<FakeInteractableRegistration> registrations =
            new List<FakeInteractableRegistration>();

        /// <summary>Creates an owner-scoped fake interaction service.</summary>
        public FakeInteractionService(FakeModLifetime lifetime)
        {
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        }

        /// <summary>Gets the number of active registrations.</summary>
        public int ActiveRegistrationCount => registrations.Count;

        /// <summary>Gets or sets the currently focused registration.</summary>
        public FakeInteractableRegistration? Focused { get; set; }

        /// <inheritdoc/>
        public OperationResult<IInteractableRegistration> Register(
            IEntity entity,
            InteractableDefinition definition,
            Action<InteractionEvent> handler)
        {
            if (entity == null)
            {
                throw new ArgumentNullException(nameof(entity));
            }

            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            if (!entity.IsAlive)
            {
                return OperationResult<IInteractableRegistration>.Failure(
                    ModErrorCode.InvalidState,
                    "The interaction entity is no longer alive.");
            }

            var registration = new FakeInteractableRegistration(
                entity,
                definition,
                handler,
                value =>
                {
                    registrations.Remove(value);
                    if (ReferenceEquals(Focused, value))
                    {
                        Focused = null;
                    }
                });
            registrations.Add(registration);
            return lifetime.TrackResult<IInteractableRegistration>(
                registration,
                "The fake mod stopped before the interaction could be registered.");
        }

        /// <inheritdoc/>
        public bool TryGetFocused(out IInteractableRegistration? interaction)
        {
            interaction = Focused != null && Focused.IsActive ? Focused : null;
            return interaction != null;
        }
    }

    /// <summary>Inspectable interaction registration that a test can invoke explicitly.</summary>
    public sealed class FakeInteractableRegistration : IInteractableRegistration
    {
        private readonly Action<InteractionEvent> handler;
        private Action<FakeInteractableRegistration>? release;

        internal FakeInteractableRegistration(
            IEntity entity,
            InteractableDefinition definition,
            Action<InteractionEvent> handler,
            Action<FakeInteractableRegistration> release)
        {
            Entity = entity;
            Definition = definition;
            this.handler = handler;
            this.release = release;
        }

        /// <inheritdoc/>
        public IEntity Entity { get; }

        /// <summary>Gets the registered prompt and range.</summary>
        public InteractableDefinition Definition { get; }

        /// <inheritdoc/>
        public bool IsActive => release != null && Entity.IsAlive;

        /// <summary>Invokes the registered handler with an explicit player snapshot.</summary>
        public void Invoke(PlayerSnapshot player)
        {
            if (!IsActive)
            {
                throw new InvalidOperationException("The fake interaction is no longer active.");
            }

            handler(new InteractionEvent(Entity, player));
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            var callback = release;
            release = null;
            callback?.Invoke(this);
        }
    }

    /// <summary>In-memory held-item service with immediate, deterministic grants.</summary>
    public sealed class FakeItemService : IItemService
    {
        private readonly FakeEntityService entities;
        private readonly FakeModLifetime lifetime;
        private int grantIndex;

        /// <summary>Creates a fake item service using an entity registry.</summary>
        public FakeItemService(FakeEntityService entities)
        {
            this.entities = entities ?? throw new ArgumentNullException(nameof(entities));
            lifetime = entities.Lifetime;
        }

        /// <summary>Gets or sets the currently held item.</summary>
        public HeldItemSnapshot? HeldItem { get; set; }

        /// <summary>Gets or sets a stable error used to reject grants.</summary>
        public ModErrorCode GiveErrorCode { get; set; }

        /// <summary>Gets or sets the grant failure message.</summary>
        public string GiveErrorMessage { get; set; } = "The item cannot be granted in this test.";

        /// <inheritdoc/>
        public bool TryGetHeld(out HeldItemSnapshot? item)
        {
            item = HeldItem != null && HeldItem.Entity.IsAlive ? HeldItem : null;
            return item != null;
        }

        /// <inheritdoc/>
        public Task<OperationResult<HeldItemSnapshot>> GiveAsync(
            ItemGrantRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (cancellationToken.IsCancellationRequested || lifetime.StoppingToken.IsCancellationRequested)
            {
                return Task.FromResult(OperationResult<HeldItemSnapshot>.Failure(
                    ModErrorCode.Cancelled,
                    "The fake item grant was cancelled."));
            }

            if (GiveErrorCode != ModErrorCode.None)
            {
                return Task.FromResult(OperationResult<HeldItemSnapshot>.Failure(GiveErrorCode, GiveErrorMessage));
            }

            var entity = entities.Create(request.ItemId + "-" + (++grantIndex), Vec3.Zero);
            HeldItem = new HeldItemSnapshot(request.ItemId, entity);
            return Task.FromResult(OperationResult<HeldItemSnapshot>.Success(HeldItem));
        }

        /// <inheritdoc/>
        public Task<OperationResult<IEntity>> DropHeldAsync(
            Vec3 velocity,
            CancellationToken cancellationToken = default)
        {
            if (!velocity.IsFinite)
            {
                throw new ArgumentException("Drop velocity must be finite.", nameof(velocity));
            }

            if (cancellationToken.IsCancellationRequested || lifetime.StoppingToken.IsCancellationRequested)
            {
                return Task.FromResult(OperationResult<IEntity>.Failure(
                    ModErrorCode.Cancelled,
                    "The fake held-item drop was cancelled."));
            }

            if (!TryGetHeld(out var held))
            {
                return Task.FromResult(OperationResult<IEntity>.Failure(
                    ModErrorCode.NotFound,
                    "The player is not holding an item."));
            }

            HeldItem = null;
            return Task.FromResult(OperationResult<IEntity>.Success(held!.Entity));
        }
    }
}
