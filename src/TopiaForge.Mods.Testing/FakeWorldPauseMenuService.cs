using System;
using System.Collections.Generic;
using System.Threading;

namespace TopiaForge.Mods.Testing
{
    /// <summary>
    /// Deterministic <see cref="IWorldPauseMenuService"/> fake for testing gamemode pause-menu actions and
    /// exit interception without a running game.
    /// </summary>
    /// <remarks>
    /// Registrations are owned by the supplied lifetime, so <c>AssertNoLeaks</c> catches a gamemode that forgets
    /// to release its pause action. Use <see cref="Invoke"/> to fire a registered action as the player would, and
    /// <see cref="InvokeExit"/> to drive the vanilla exit-to-menu path through a registered interceptor.
    /// </remarks>
    public sealed class FakeWorldPauseMenuService : IWorldPauseMenuService
    {
        private readonly FakeModLifetime lifetime;
        private readonly Dictionary<string, PauseRegistration> actions =
            new Dictionary<string, PauseRegistration>(StringComparer.Ordinal);
        private Func<WorldPauseExitContext, WorldPauseExitDecision>? exitInterceptor;
        private bool available = true;

        /// <summary>Creates a fake pause-menu service owned by a mod lifetime.</summary>
        /// <param name="lifetime">The fake lifetime that owns every registration this service hands out.</param>
        public FakeWorldPauseMenuService(FakeModLifetime lifetime)
        {
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        }

        /// <summary>
        /// Gets or sets whether the game's pause UI is resolved. Always false once the owning lifetime is stopping,
        /// so a mod cannot register during teardown.
        /// </summary>
        public bool IsAvailable
        {
            get => available && !lifetime.IsStopping;
            set => available = value;
        }

        /// <summary>
        /// Gets or sets whether <see cref="InterceptExit"/> succeeds. Set false to model a host that resolved the
        /// pause UI but not its exit button.
        /// </summary>
        public bool SupportsExitInterception { get; set; } = true;

        /// <summary>Gets the number of pause actions currently registered.</summary>
        public int ActiveActionCount => actions.Count;

        /// <summary>Gets whether an exit interceptor is currently registered.</summary>
        public bool HasExitInterceptor => exitInterceptor != null;

        /// <inheritdoc />
        public OperationResult<IDisposable> RegisterAction(WorldPauseAction action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (!IsAvailable)
            {
                return OperationResult<IDisposable>.Failure(
                    ModErrorCode.Unavailable,
                    "The pause menu is unavailable.");
            }

            if (actions.ContainsKey(action.Id))
            {
                return OperationResult<IDisposable>.Failure(
                    ModErrorCode.Conflict,
                    "A pause action already uses '" + action.Id + "'.");
            }

            var registration = new PauseRegistration(
                action.Id,
                action.Callback,
                id => actions.Remove(id));
            actions.Add(action.Id, registration);
            try
            {
                registration.AttachLifetimeLease(lifetime.Track(registration));
                return OperationResult<IDisposable>.Success(registration);
            }
            catch (ObjectDisposedException)
            {
                registration.Dispose();
                return OperationResult<IDisposable>.Failure(
                    ModErrorCode.Cancelled,
                    "The pause action owner is stopping.");
            }
        }

        /// <inheritdoc />
        public OperationResult<IDisposable> InterceptExit(
            Func<WorldPauseExitContext, WorldPauseExitDecision> interceptor)
        {
            if (interceptor == null)
            {
                throw new ArgumentNullException(nameof(interceptor));
            }

            if (!SupportsExitInterception || !IsAvailable)
            {
                return OperationResult<IDisposable>.Failure(
                    ModErrorCode.Unavailable,
                    "This host cannot intercept the vanilla exit action.");
            }

            if (exitInterceptor != null)
            {
                return OperationResult<IDisposable>.Failure(
                    ModErrorCode.Conflict,
                    "An exit interceptor is already registered.");
            }

            exitInterceptor = interceptor;
            var registration = new InterceptorRegistration(() => exitInterceptor = null);
            try
            {
                registration.AttachLifetimeLease(lifetime.Track(registration));
                return OperationResult<IDisposable>.Success(registration);
            }
            catch (ObjectDisposedException)
            {
                registration.Dispose();
                return OperationResult<IDisposable>.Failure(
                    ModErrorCode.Cancelled,
                    "The exit interceptor owner is stopping.");
            }
        }

        /// <summary>Fires a registered pause action as the player would.</summary>
        /// <param name="id">The action id passed to <see cref="RegisterAction"/>.</param>
        /// <returns>True when an active action with that id ran.</returns>
        public bool Invoke(string id)
        {
            return id != null
                && actions.TryGetValue(id, out var registration)
                && registration.Invoke();
        }

        /// <summary>Drives the vanilla exit-to-menu path through the registered interceptor.</summary>
        /// <param name="session">The session that is exiting.</param>
        /// <returns>
        /// The interceptor's decision, or <see cref="WorldPauseExitDecision.EndSessionAndExit"/> when no interceptor
        /// is registered or the interceptor throws — matching the provider's fail-safe contract.
        /// </returns>
        public WorldPauseExitDecision InvokeExit(WorldSession session)
        {
            var interceptor = exitInterceptor;
            if (interceptor == null)
            {
                return WorldPauseExitDecision.EndSessionAndExit;
            }

            try
            {
                return interceptor(new WorldPauseExitContext(session));
            }
            catch
            {
                return WorldPauseExitDecision.EndSessionAndExit;
            }
        }

        private sealed class InterceptorRegistration : IDisposable
        {
            private Action? release;
            private IDisposable? lifetimeLease;

            public InterceptorRegistration(Action release)
            {
                this.release = release;
            }

            public void AttachLifetimeLease(IDisposable lease)
            {
                lifetimeLease = lease;
            }

            public void Dispose()
            {
                var releaseNow = Interlocked.Exchange(ref release, null);
                try
                {
                    releaseNow?.Invoke();
                }
                finally
                {
                    Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
                }
            }
        }

        private sealed class PauseRegistration : IDisposable
        {
            private readonly string id;
            private Action? callback;
            private Action<string>? release;
            private IDisposable? lifetimeLease;

            public PauseRegistration(string id, Action callback, Action<string> release)
            {
                this.id = id;
                this.callback = callback;
                this.release = release;
            }

            public void AttachLifetimeLease(IDisposable lease)
            {
                lifetimeLease = lease;
            }

            public bool Invoke()
            {
                var active = callback;
                if (active == null)
                {
                    return false;
                }

                active();
                return true;
            }

            public void Dispose()
            {
                callback = null;
                var releaseNow = Interlocked.Exchange(ref release, null);
                try
                {
                    releaseNow?.Invoke(id);
                }
                finally
                {
                    Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
                }
            }
        }
    }
}
