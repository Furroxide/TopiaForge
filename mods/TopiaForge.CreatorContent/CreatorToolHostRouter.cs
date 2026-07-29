using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.CreatorContent
{
    internal sealed partial class CreatorToolHostRouter :
        ICreatorToolHostRouter,
        IOwnerBoundExtensionFactory,
        IDisposable
    {
        private readonly object gate = new object();
        private readonly string providerId;
        private readonly IInputService input;
        private readonly ISceneService scenes;
        private readonly IModLogger logger;
        private readonly List<HostRegistration> registrations = new List<HostRegistration>();
        private readonly List<InputBinding> providerToggleBindings = new List<InputBinding>();
        private readonly Dictionary<string, HostToggleBinding> hostToggleBindings =
            new Dictionary<string, HostToggleBinding>(StringComparer.OrdinalIgnoreCase);
        private HostRegistration? active;
        private IInputAction? toggleAction;
        private bool suppressToggleUntilRelease;
        private bool disposed;

        public CreatorToolHostRouter(
            string providerId,
            IInputService input,
            ISceneService scenes,
            IModLogger logger)
        {
            this.providerId = providerId;
            this.input = input;
            this.scenes = scenes;
            this.logger = logger;
        }

        public CreatorToolHostDescriptor? ActiveHost
        {
            get
            {
                lock (gate)
                {
                    return active?.Descriptor;
                }
            }
        }

        public OperationResult<bool> AttachInput(string toggleKey)
        {
            if (string.IsNullOrWhiteSpace(toggleKey))
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "A creator toggle key is required.");
            }

            InputBinding providerBinding;
            try
            {
                providerBinding = InputBinding.Key(toggleKey);
            }
            catch (ArgumentException exception)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, exception.Message);
            }

            IReadOnlyList<InputBinding> bindings;
            lock (gate)
            {
                if (disposed) return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The creator tool router is disposed.");
                if (toggleAction != null) return OperationResult<bool>.Success(false);
                providerToggleBindings.Clear();
                providerToggleBindings.Add(providerBinding);
                bindings = BuildToggleBindingsLocked();
                if (bindings.Count > 8)
                {
                    providerToggleBindings.Clear();
                    return OperationResult<bool>.Failure(ModErrorCode.RateLimited, "The shared creator action reached its binding limit.");
                }
            }
            var result = input.RegisterAction(new InputActionDefinition(
                "creator-tools.toggle",
                "Creator tools",
                bindings,
                suppressWhileUiFocused: false));
            if (!result.TryGetValue(out var action))
            {
                lock (gate)
                {
                    providerToggleBindings.Clear();
                }
                return OperationResult<bool>.Failure(result.ErrorCode, result.ErrorMessage);
            }
            lock (gate)
            {
                if (disposed)
                {
                    action.Dispose();
                    return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The creator tool router is disposed.");
                }
                toggleAction = action;
            }
            return OperationResult<bool>.Success(true);
        }

        public void Tick()
        {
            IInputAction? action;
            bool suppress;
            lock (gate)
            {
                action = toggleAction;
                suppress = suppressToggleUntilRelease;
                if (suppress && action?.IsHeld != true)
                {
                    suppressToggleUntilRelease = false;
                }
            }
            // Rebinding cannot clear an input implementation's already-sampled edge. Ignore the held/release
            // transition after a host-only key is removed so it cannot open a different eligible host.
            if (suppress) return;
            if (action?.WasPressed != true) return;
            var result = Toggle();
            if (!result.Succeeded && result.ErrorCode != ModErrorCode.Unavailable)
            {
                logger.Warn("Creator F5 toggle failed: " + result.ErrorMessage);
            }
        }

        public OperationResult<ICreatorToolHostRegistration> RegisterHost(CreatorToolHostRegistrationRequest request) =>
            RegisterHost(providerId, null, request);

        public OperationResult<bool> Toggle()
        {
            lock (gate)
            {
                if (disposed) return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The creator tool router is disposed.");
                if (active != null)
                {
                    // Close outside the lock so a host may safely query or re-enter provider services.
                }
            }
            if (ActiveHost != null) return CloseActive(CreatorToolCloseReason.UserToggle);

            var context = CreateContext();
            HostRegistration[] candidates;
            lock (gate)
            {
                candidates = registrations
                    .Where(registration => registration.IsAlive)
                    .OrderByDescending(registration => registration.Descriptor.Priority)
                    .ThenBy(registration => registration.Descriptor.SourceId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(registration => registration.Descriptor.LocalId, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            foreach (var candidate in candidates)
            {
                bool available;
                try
                {
                    available = candidate.Host.CanOpen(context);
                }
                catch (Exception exception)
                {
                    logger.Error(exception, "Creator host '" + candidate.Descriptor.HostId + "' threw during availability check.");
                    continue;
                }
                if (!available) continue;

                OperationResult<bool> opened;
                try
                {
                    opened = candidate.Host.Open(context);
                }
                catch (Exception exception)
                {
                    logger.Error(exception, "Creator host '" + candidate.Descriptor.HostId + "' threw while opening.");
                    continue;
                }
                if (!opened.Succeeded || opened.Value != true) continue;
                lock (gate)
                {
                    if (!disposed && candidate.IsAlive)
                    {
                        active = candidate;
                        return OperationResult<bool>.Success(true);
                    }
                }
                SafeClose(candidate, CreatorToolCloseReason.HostUnavailable);
            }

            return OperationResult<bool>.Failure(ModErrorCode.Unavailable, "No registered creator workbench is available in the active scene.");
        }

        public OperationResult<bool> CloseActive(CreatorToolCloseReason reason = CreatorToolCloseReason.Requested)
        {
            if (!Enum.IsDefined(typeof(CreatorToolCloseReason), reason))
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Unknown creator tool close reason.");
            }
            HostRegistration? current;
            lock (gate)
            {
                current = active;
                active = null;
            }
            if (current == null) return OperationResult<bool>.Success(false);
            return SafeClose(current, reason);
        }

        object IOwnerBoundExtensionFactory.CreateOwnerFacade(Type contractType, string ownerModId, IModLifetime lifetime)
        {
            if (contractType != typeof(ICreatorToolHostService)
                && contractType != typeof(ICreatorToolHostRouter))
            {
                throw new ArgumentException("Unsupported creator tool router contract.", nameof(contractType));
            }
            return new OwnerFacade(this, ownerModId, lifetime);
        }

        public void OnSceneChanged() => _ = CloseActive(CreatorToolCloseReason.SceneChanged);

        public void Dispose()
        {
            HostRegistration[] activeRegistrations;
            IInputAction? action;
            HostRegistration? current;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                current = active;
                active = null;
                action = toggleAction;
                toggleAction = null;
                activeRegistrations = registrations.ToArray();
                registrations.Clear();
            }
            if (current != null) SafeClose(current, CreatorToolCloseReason.ProviderStopping);
            for (var index = activeRegistrations.Length - 1; index >= 0; index--) activeRegistrations[index].MarkDisposed();
            action?.Dispose();
        }

        private OperationResult<ICreatorToolHostRegistration> RegisterHost(
            string ownerId,
            IModLifetime? ownerLifetime,
            CreatorToolHostRegistrationRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (!CreatorIds.IsLocalId(request.LocalId))
            {
                return OperationResult<ICreatorToolHostRegistration>.Failure(ModErrorCode.InvalidArgument, "Host local id is not portable.");
            }
            if (request.DisplayName.Length > 128)
            {
                return OperationResult<ICreatorToolHostRegistration>.Failure(ModErrorCode.InvalidArgument, "Host display name exceeds 128 characters.");
            }
            if (request.ToggleBinding.Length > 32)
            {
                return OperationResult<ICreatorToolHostRegistration>.Failure(ModErrorCode.InvalidArgument, "Host toggle binding exceeds 32 characters.");
            }
            lock (gate)
            {
                if (disposed) return OperationResult<ICreatorToolHostRegistration>.Failure(ModErrorCode.InvalidState, "The creator tool router is disposed.");
                if (ownerLifetime?.IsStopping == true) return OperationResult<ICreatorToolHostRegistration>.Failure(ModErrorCode.Cancelled, "The host mod is stopping.");
                var hostId = CreatorIds.Qualify(ownerId, request.LocalId);
                if (registrations.Any(registration => string.Equals(registration.Descriptor.HostId, hostId, StringComparison.OrdinalIgnoreCase)))
                {
                    return OperationResult<ICreatorToolHostRegistration>.Failure(ModErrorCode.Conflict, "That source already registered the host id.");
                }
                var descriptor = new CreatorToolHostDescriptor(hostId, ownerId, request.LocalId, request.DisplayName, request.Priority);
                var rebound = AddToggleBindingLocked(request.ToggleBinding);
                if (!rebound.Succeeded)
                {
                    return OperationResult<ICreatorToolHostRegistration>.Failure(rebound.ErrorCode, rebound.ErrorMessage);
                }
                var registration = new HostRegistration(
                    this,
                    descriptor,
                    request.Host,
                    ownerLifetime,
                    string.IsNullOrWhiteSpace(request.ToggleBinding) ? string.Empty : request.ToggleBinding);
                registrations.Add(registration);
                return OperationResult<ICreatorToolHostRegistration>.Success(registration);
            }
        }

        private void Unregister(HostRegistration registration)
        {
            var close = false;
            lock (gate)
            {
                registrations.Remove(registration);
                RemoveToggleBindingLocked(registration.ToggleBinding);
                if (ReferenceEquals(active, registration))
                {
                    active = null;
                    close = true;
                }
            }
            if (close) SafeClose(registration, CreatorToolCloseReason.HostUnavailable);
        }

        private CreatorToolOpenContext CreateContext()
        {
            return scenes.TryGetActive(out var scene) && scene != null
                ? new CreatorToolOpenContext(scene.Name)
                : new CreatorToolOpenContext(string.Empty);
        }

        private OperationResult<bool> SafeClose(HostRegistration registration, CreatorToolCloseReason reason)
        {
            try
            {
                return registration.Host.Close(reason);
            }
            catch (Exception exception)
            {
                logger.Error(exception, "Creator host '" + registration.Descriptor.HostId + "' threw while closing.");
                return OperationResult<bool>.Failure(ModErrorCode.External, "The creator workbench host failed while closing.");
            }
        }

        private sealed class HostRegistration : ICreatorToolHostRegistration
        {
            private readonly CreatorToolHostRouter router;
            private readonly IModLifetime? ownerLifetime;
            private bool alive = true;

            public HostRegistration(
                CreatorToolHostRouter router,
                CreatorToolHostDescriptor descriptor,
                ICreatorToolHost host,
                IModLifetime? ownerLifetime,
                string toggleBinding)
            {
                this.router = router;
                Descriptor = descriptor;
                Host = host;
                this.ownerLifetime = ownerLifetime;
                ToggleBinding = toggleBinding;
            }

            public CreatorToolHostDescriptor Descriptor { get; }
            public ICreatorToolHost Host { get; }
            internal string ToggleBinding { get; }
            public bool IsAlive => alive && ownerLifetime?.IsStopping != true;
            public void Dispose()
            {
                if (!alive) return;
                alive = false;
                router.Unregister(this);
            }
            public void MarkDisposed() => alive = false;
        }

    }
}
