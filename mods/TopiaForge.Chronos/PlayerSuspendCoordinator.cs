using System;
using TopiaForge.Mods;

namespace TopiaForge.Chronos
{
    // Routes Chronos suspension through the shared player-control lease service. That service composes every mod's
    // control requests and restores the controller's captured state only after the final owner releases it.
    internal sealed class PlayerSuspendCoordinator : IDisposable
    {
        private const float RetrySeconds = 0.5f;

        private readonly ILocalPlayerService player;
        private readonly IModLogger logger;
        private IPlayerControlLease? lease;
        private string pendingUsage = "time freeze";
        private float retryTimer;
        private bool acquireFailureLogged;

        public PlayerSuspendCoordinator(ILocalPlayerService player, IModLogger logger)
        {
            this.player = player ?? throw new ArgumentNullException(nameof(player));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public bool IsSuspended => lease != null && lease.IsActive;

        public void Suspend(string usage)
        {
            pendingUsage = NormalizeUsage(usage);
            if (IsSuspended)
            {
                return;
            }

            TryAcquire();
        }

        public void Tick(float unscaledDeltaTime)
        {
            if (IsSuspended)
            {
                retryTimer = 0f;
                return;
            }

            retryTimer = Math.Max(0f, retryTimer - Math.Max(0f, unscaledDeltaTime));
            if (retryTimer <= 0f)
            {
                TryAcquire();
            }
        }

        private void TryAcquire()
        {
            lease?.Dispose();
            lease = null;
            var result = player.AcquireControl("Chronos freeze: " + pendingUsage);
            if (result.TryGetValue(out var acquired))
            {
                lease = acquired;
                retryTimer = 0f;
                acquireFailureLogged = false;
                return;
            }

            retryTimer = RetrySeconds;

            if (!acquireFailureLogged)
            {
                acquireFailureLogged = true;
                logger.Warn("Chronos could not suspend player controls: " + result.ErrorMessage);
            }
        }

        public void Release()
        {
            var captured = lease;
            lease = null;
            retryTimer = 0f;
            acquireFailureLogged = false;
            captured?.Dispose();
        }

        public void Dispose() => Release();

        private static string NormalizeUsage(string usage) =>
            string.IsNullOrWhiteSpace(usage) ? "time freeze" : usage.Trim();
    }
}
