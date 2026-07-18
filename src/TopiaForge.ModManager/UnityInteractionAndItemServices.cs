using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.ModManager
{
    internal sealed class OwnerInteractionService : IInteractionService
    {
        private readonly IModLifetime lifetime;
        private readonly UnityEntityRegistry entities;
        private readonly UnityPlayerBackend player;
        private readonly IModLogger logger;

        public OwnerInteractionService(
            IModLifetime lifetime,
            UnityEntityRegistry entities,
            UnityPlayerBackend player,
            IModLogger logger)
        {
            this.lifetime = lifetime;
            this.entities = entities;
            this.player = player;
            this.logger = logger;
        }

        public OperationResult<IInteractableRegistration> Register(
            IEntity entity,
            InteractableDefinition definition,
            Action<InteractionEvent> handler)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (lifetime.IsStopping)
            {
                return OperationResult<IInteractableRegistration>.Failure(
                    ModErrorCode.Cancelled,
                    "The mod is stopping and cannot register an interaction.");
            }

            if (!entities.TryGetGameObject(entity, out var target))
            {
                return OperationResult<IInteractableRegistration>.Failure(
                    ModErrorCode.InvalidArgument,
                    "The interaction target was not created by this TopiaForge runtime or is no longer alive.");
            }

            if (target.GetComponent<UnityInteractionBridge>() != null)
            {
                return OperationResult<IInteractableRegistration>.Failure(
                    ModErrorCode.Conflict,
                    "The target already has a TopiaForge interaction registration.");
            }

            try
            {
                var bridge = target.AddComponent<UnityInteractionBridge>();
                bridge.Configure(this, entity, definition, handler, player, logger);
                lifetime.Track(bridge);
                return OperationResult<IInteractableRegistration>.Success(bridge);
            }
            catch (Exception exception)
            {
                return OperationResult<IInteractableRegistration>.Failure(
                    ModErrorCode.External,
                    "Robotopia rejected the interaction registration: " + exception.Message);
            }
        }

        public bool TryGetFocused(out IInteractableRegistration? interaction)
        {
            UnityMainThreadGuard.AssertCurrent();
            var focused = UIState.InteractTarget.Value as UnityInteractionBridge;
            if (focused != null && focused.IsActive && ReferenceEquals(focused.Owner, this))
            {
                interaction = focused;
                return true;
            }

            interaction = null;
            return false;
        }
    }

    internal sealed class UnityInteractionBridge : MonoBehaviour, IInteractable, IInteractableRegistration
    {
        private IEntity? entity;
        private InteractableDefinition? definition;
        private Action<InteractionEvent>? handler;
        private UnityPlayerBackend? player;
        private IModLogger? logger;
        private bool disposed;

        internal OwnerInteractionService? Owner { get; private set; }

        public GameObject GameObject => gameObject;

        public float ScreenRectExpansion => 0f;

        public IEntity Entity => entity ?? throw new ObjectDisposedException(nameof(UnityInteractionBridge));

        public bool IsActive => !disposed && this != null && isActiveAndEnabled && entity?.IsAlive == true;

        public Behaviour AsComponent() => this;

        internal void Configure(
            OwnerInteractionService owner,
            IEntity target,
            InteractableDefinition interaction,
            Action<InteractionEvent> callback,
            UnityPlayerBackend playerBackend,
            IModLogger ownerLogger)
        {
            Owner = owner;
            entity = target;
            definition = interaction;
            handler = callback;
            player = playerBackend;
            logger = ownerLogger;
        }

        public bool CanInteract(Transform hand)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (!IsActive || hand == null || definition == null)
            {
                return false;
            }

            return Vector3.Distance(hand.position, transform.position) <= definition.MaximumDistance;
        }

        public UniTask OnInteractAttempt(Transform hand, CancellationToken cancellationToken)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (!IsActive || hand == null || cancellationToken.IsCancellationRequested
                || entity == null || handler == null)
            {
                return UniTask.CompletedTask;
            }

            try
            {
                PlayerSnapshot snapshot;
                if (player?.TryGetSnapshot(out var current) == true && current != null)
                {
                    snapshot = current;
                }
                else
                {
                    snapshot = new PlayerSnapshot(
                        UnityPhysicsBackend.FromUnity(hand.position),
                        new TopiaForge.Mods.Ray(
                            UnityPhysicsBackend.FromUnity(hand.position),
                            UnityPhysicsBackend.FromUnity(hand.forward)));
                }

                handler(new InteractionEvent(entity, snapshot));
            }
            catch (Exception exception)
            {
                logger?.Error(exception, "A TopiaForge interaction callback failed.");
            }

            return UniTask.CompletedTask;
        }

        private void Awake()
        {
            ListenableExt.WithState<IInteractable?>(this, UIState.InteractTarget, OnInteractTargetChanged);
        }

        private void OnInteractTargetChanged(IInteractable? target)
        {
            if (ReferenceEquals(target, this) && definition != null)
            {
                UIState.InteractAction.Set(definition.Prompt);
            }
        }

        public void Dispose()
        {
            UnityMainThreadGuard.AssertCurrent();
            if (disposed)
            {
                return;
            }

            disposed = true;
            enabled = false;
            if (ReferenceEquals(UIState.InteractTarget.Value, this))
            {
                UIState.InteractTarget.Set(null);
            }

            Owner = null;
            entity = null;
            definition = null;
            handler = null;
            player = null;
            logger = null;
            if (this != null)
            {
                UnityEngine.Object.Destroy(this);
            }
        }
    }

    internal sealed class OwnerItemService : IItemService
    {
        private readonly IModLifetime lifetime;
        private readonly UnityEntityRegistry entities;
        private readonly IModLogger logger;

        public OwnerItemService(IModLifetime lifetime, UnityEntityRegistry entities, IModLogger logger)
        {
            this.lifetime = lifetime;
            this.entities = entities;
            this.logger = logger;
        }

        public bool TryGetHeld(out HeldItemSnapshot? item)
        {
            UnityMainThreadGuard.AssertCurrent();
            item = null;
            var player = PlayerController.FindPlayer();
            var held = player != null ? player.HeldItem : null;
            if (held == null || !held.IsHeld)
            {
                return false;
            }

            item = new HeldItemSnapshot(GetItemId(held.gameObject), entities.GetOrCreate(held.gameObject));
            return true;
        }

        public async Task<OperationResult<HeldItemSnapshot>> GiveAsync(
            ItemGrantRequest request,
            CancellationToken cancellationToken = default)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.Quantity != 1)
            {
                return OperationResult<HeldItemSnapshot>.Failure(
                    ModErrorCode.InvalidArgument,
                    "Robotopia can hold one world item at a time; request a quantity of one.");
            }

            var player = PlayerController.FindPlayer();
            if (player == null || player.Hand == null)
            {
                return OperationResult<HeldItemSnapshot>.Failure(
                    ModErrorCode.Unavailable,
                    "The Robotopia player and hand are not available in the current scene.");
            }

            if (player.HeldItem != null)
            {
                return OperationResult<HeldItemSnapshot>.Failure(
                    ModErrorCode.Conflict,
                    "The player is already holding an item.");
            }

            if (!TryFindPrefab(request.ItemId, out var prefab))
            {
                return OperationResult<HeldItemSnapshot>.Failure(
                    ModErrorCode.NotFound,
                    "No Robotopia world item is registered with id '" + request.ItemId + "'.");
            }

            if (lifetime.IsStopping || cancellationToken.IsCancellationRequested)
            {
                return OperationResult<HeldItemSnapshot>.Failure(ModErrorCode.Cancelled, "The item grant was cancelled.");
            }

            GameObject? instance = null;
            try
            {
                instance = UnityEngine.Object.Instantiate(prefab, player.Hand.position, player.Hand.rotation);
                var grabbable = instance.GetComponent<Grabbable>()
                    ?? instance.GetComponentInChildren<Grabbable>(true);
                if (grabbable == null)
                {
                    UnityEngine.Object.Destroy(instance);
                    return OperationResult<HeldItemSnapshot>.Failure(
                        ModErrorCode.External,
                        "The registered item prefab does not contain Robotopia's Grabbable component.");
                }

                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                           lifetime.StoppingToken,
                           cancellationToken))
                {
                    await grabbable.TransferTo(player.Hand, player.gameObject, linked.Token);
                }

                UnityMainThreadGuard.AssertCurrent();
                var snapshot = new HeldItemSnapshot(GetItemId(instance), entities.GetOrCreate(instance));
                instance = null;
                return OperationResult<HeldItemSnapshot>.Success(snapshot);
            }
            catch (OperationCanceledException)
            {
                return OperationResult<HeldItemSnapshot>.Failure(ModErrorCode.Cancelled, "The item grant was cancelled.");
            }
            catch (Exception exception)
            {
                logger.Error(exception, "A TopiaForge item grant failed.");
                return OperationResult<HeldItemSnapshot>.Failure(ModErrorCode.External, exception.Message);
            }
            finally
            {
                if (instance != null)
                {
                    UnityEngine.Object.Destroy(instance);
                }
            }
        }

        public async Task<OperationResult<IEntity>> DropHeldAsync(
            Vec3 velocity,
            CancellationToken cancellationToken = default)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (!velocity.IsFinite)
            {
                throw new ArgumentException("A drop velocity must be finite.", nameof(velocity));
            }

            var player = PlayerController.FindPlayer();
            var held = player != null ? player.HeldItem : null;
            if (held == null)
            {
                return OperationResult<IEntity>.Failure(ModErrorCode.NotFound, "The player is not holding an item.");
            }

            var entity = entities.GetOrCreate(held.gameObject);
            try
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                           lifetime.StoppingToken,
                           cancellationToken))
                {
                    await held.Drop(
                        velocity.LengthSquared > 0.000001f ? DropMode.Throw : DropMode.Drop,
                        velocity.LengthSquared > 0.000001f
                            ? (Vector3?)UnityPhysicsBackend.ToUnity(velocity)
                            : null,
                        linked.Token);
                }

                UnityMainThreadGuard.AssertCurrent();
                return OperationResult<IEntity>.Success(entity);
            }
            catch (OperationCanceledException)
            {
                return OperationResult<IEntity>.Failure(ModErrorCode.Cancelled, "The held-item drop was cancelled.");
            }
            catch (Exception exception)
            {
                logger.Error(exception, "A TopiaForge held-item drop failed.");
                return OperationResult<IEntity>.Failure(ModErrorCode.External, exception.Message);
            }
        }

        private static bool TryFindPrefab(string requestedId, out GameObject prefab)
        {
            prefab = null!;
            var registry = EquippableItemRegistry.Instance;
            if (registry == null)
            {
                return false;
            }

            var match = registry.IterEntries()
                .Select(entry => entry.worldItemPrefab)
                .OfType<GameObject>()
                .OrderBy(candidate => candidate.name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.name, requestedId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ToItemId(candidate.name), requestedId, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                return false;
            }

            prefab = match;
            return true;
        }

        private static string GetItemId(GameObject item)
        {
            var registry = EquippableItemRegistry.Instance;
            if (registry != null && registry.TryGetEntryByWorldItem(item, out var entry)
                && entry.worldItemPrefab != null)
            {
                return ToItemId(entry.worldItemPrefab.name);
            }

            return ToItemId(item.name);
        }

        private static string ToItemId(string name)
        {
            const string cloneSuffix = "(Clone)";
            if (name.EndsWith(cloneSuffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - cloneSuffix.Length);
            }

            var result = new StringBuilder(name.Length);
            var previousSeparator = true;
            foreach (var character in name.Normalize(NormalizationForm.FormKC))
            {
                if (char.IsLetterOrDigit(character))
                {
                    result.Append(char.ToLower(character, CultureInfo.InvariantCulture));
                    previousSeparator = false;
                }
                else if (!previousSeparator && result.Length > 0)
                {
                    result.Append('-');
                    previousSeparator = true;
                }
            }

            return result.ToString().Trim('-');
        }
    }
}
