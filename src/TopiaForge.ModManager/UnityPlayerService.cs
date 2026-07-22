using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.ModManager
{
    internal sealed class OwnerPlayerService : ILocalPlayerService
    {
        private readonly IModLifetime lifetime;
        private readonly UnityPlayerBackend backend;

        public OwnerPlayerService(IModLifetime lifetime, UnityPlayerBackend backend)
        {
            this.lifetime = lifetime;
            this.backend = backend;
        }

        public bool TryGetSnapshot(out PlayerSnapshot? snapshot)
        {
            UnityMainThreadGuard.AssertCurrent();
            return backend.TryGetSnapshot(out snapshot);
        }

        public bool TryGetHealth(out PlayerHealthSnapshot? health)
        {
            UnityMainThreadGuard.AssertCurrent();
            return backend.TryGetHealth(out health);
        }

        public OperationResult<PlayerHealthSnapshot> Damage(PlayerDamageRequest request)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (request == null) throw new ArgumentNullException(nameof(request));
            return backend.ChangeHealth(-request.Amount, request.Source);
        }

        public OperationResult<PlayerHealthSnapshot> Heal(float amount, string source)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (amount <= 0f || float.IsNaN(amount) || float.IsInfinity(amount))
            {
                throw new ArgumentOutOfRangeException(nameof(amount));
            }

            if (string.IsNullOrWhiteSpace(source))
            {
                throw new ArgumentException("A diagnostic healing source is required.", nameof(source));
            }

            return backend.ChangeHealth(amount, source);
        }

        public OperationResult<IPlayerControlLease> AcquireControl(string reason)
        {
            UnityMainThreadGuard.AssertCurrent();
            var result = backend.AcquireControl(reason);
            if (result.TryGetValue(out var lease))
            {
                try
                {
                    return OperationResult<IPlayerControlLease>.Success(
                        new OwnerPlayerControlLease(lease, lifetime.Track(lease)));
                }
                catch (ObjectDisposedException)
                {
                    lease.Dispose();
                    return OperationResult<IPlayerControlLease>.Failure(
                        ModErrorCode.Cancelled,
                        "The mod stopped before player control could be acquired.");
                }
            }

            return result;
        }

        private sealed class OwnerPlayerControlLease : IPlayerControlLease
        {
            private readonly IPlayerControlLease inner;
            private IDisposable? lifetimeLease;

            public OwnerPlayerControlLease(IPlayerControlLease inner, IDisposable lifetimeLease)
            {
                this.inner = inner;
                this.lifetimeLease = lifetimeLease;
            }

            public bool IsActive => lifetimeLease != null && inner.IsActive;
            public string Reason => inner.Reason;

            public void Dispose()
            {
                Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
            }
        }

    }

    internal sealed class UnityPlayerBackend : IDisposable
    {
        private readonly object sync = new object();
        private Behaviour? controlledBehaviour;
        private bool originalEnabled;
        private int leases;
        private bool disposed;

        public bool TryGetSnapshot(out PlayerSnapshot? snapshot)
        {
            UnityMainThreadGuard.AssertCurrent();
            snapshot = null;
            var camera = ResolveCamera();
            if (camera == null)
            {
                return false;
            }

            var position = ResolvePlayerTransform()?.position ?? camera.transform.position;
            snapshot = new PlayerSnapshot(
                UnityPhysicsBackend.FromUnity(position),
                new TopiaForge.Mods.Ray(
                    UnityPhysicsBackend.FromUnity(camera.transform.position),
                    UnityPhysicsBackend.FromUnity(camera.transform.forward)));
            return true;
        }

        public bool TryGetHealth(out PlayerHealthSnapshot? health)
        {
            UnityMainThreadGuard.AssertCurrent();
            health = null;
            var component = ResolveHealthComponent();
            if (component == null
                || !TryReadFloat(component, "health", out var current)
                || !TryReadFloat(component, "maxHealth", out var maximum)
                || maximum <= 0f)
            {
                return false;
            }

            health = new PlayerHealthSnapshot(current, maximum);
            return true;
        }

        public OperationResult<PlayerHealthSnapshot> ChangeHealth(float delta, string source)
        {
            UnityMainThreadGuard.AssertCurrent();
            var component = ResolveHealthComponent();
            if (component == null)
            {
                return OperationResult<PlayerHealthSnapshot>.Failure(
                    ModErrorCode.Unavailable,
                    "Robotopia player health is unavailable in the current scene.");
            }

            try
            {
                var method = component.GetType().GetMethods(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(candidate =>
                    {
                        if (!string.Equals(candidate.Name, "ChangeHealth", StringComparison.Ordinal))
                        {
                            return false;
                        }

                        var parameters = candidate.GetParameters();
                        return parameters.Length == 2
                            && parameters[0].ParameterType == typeof(float)
                            && parameters[1].ParameterType == typeof(string);
                    });
                if (method == null)
                {
                    return OperationResult<PlayerHealthSnapshot>.Failure(
                        ModErrorCode.Unavailable,
                        "The current game build does not expose the supported player-health operation.");
                }

                method.Invoke(component, new object[] { delta, source });
                return TryGetHealth(out var health) && health != null
                    ? OperationResult<PlayerHealthSnapshot>.Success(health)
                    : OperationResult<PlayerHealthSnapshot>.Failure(
                        ModErrorCode.External,
                        "Player health changed but its resulting state could not be read.");
            }
            catch (TargetInvocationException exception)
            {
                return OperationResult<PlayerHealthSnapshot>.Failure(
                    ModErrorCode.External,
                    exception.InnerException?.Message ?? exception.Message);
            }
            catch (Exception exception)
            {
                return OperationResult<PlayerHealthSnapshot>.Failure(ModErrorCode.External, exception.Message);
            }
        }

        public OperationResult<IPlayerControlLease> AcquireControl(string reason)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (string.IsNullOrWhiteSpace(reason))
            {
                return OperationResult<IPlayerControlLease>.Failure(
                    ModErrorCode.InvalidArgument,
                    "A diagnostic reason is required when acquiring player controls.");
            }

            lock (sync)
            {
                if (disposed)
                {
                    return OperationResult<IPlayerControlLease>.Failure(
                        ModErrorCode.InvalidState,
                        "The player service is shutting down.");
                }

                var behaviour = ResolvePlayerControls();
                if (behaviour == null)
                {
                    return OperationResult<IPlayerControlLease>.Failure(
                        ModErrorCode.Unavailable,
                        "Robotopia player controls are not available in the current scene.");
                }

                if (leases == 0 || controlledBehaviour == null)
                {
                    controlledBehaviour = behaviour;
                    originalEnabled = behaviour.enabled;
                    behaviour.enabled = false;
                }
                else if (!ReferenceEquals(controlledBehaviour, behaviour))
                {
                    return OperationResult<IPlayerControlLease>.Failure(
                        ModErrorCode.Conflict,
                        "The active player controller changed while another control lease is held.");
                }

                leases++;
                return OperationResult<IPlayerControlLease>.Success(new PlayerControlLease(this, reason));
            }
        }

        public void Dispose()
        {
            UnityMainThreadGuard.AssertCurrent();
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                leases = 0;
                RestoreControls();
            }
        }

        private void Release()
        {
            lock (sync)
            {
                if (leases > 0)
                {
                    leases--;
                }

                if (leases == 0)
                {
                    RestoreControls();
                }
            }
        }

        private void RestoreControls()
        {
            if (controlledBehaviour != null)
            {
                controlledBehaviour.enabled = originalEnabled;
            }

            controlledBehaviour = null;
        }

        private static Camera? ResolveCamera()
        {
            var camera = Camera.main;
            if (camera != null && camera.isActiveAndEnabled)
            {
                return camera;
            }

            return Camera.allCameras.FirstOrDefault(candidate => candidate != null && candidate.isActiveAndEnabled);
        }

        private static Transform? ResolvePlayerTransform()
        {
            var player = ResolvePlayerController();
            if (player is Component component && component != null)
            {
                return component.transform;
            }

            return null;
        }

        private static Behaviour? ResolvePlayerControls()
        {
            var player = ResolvePlayerController();
            if (player == null)
            {
                return null;
            }

            try
            {
                var property = player.GetType().GetProperty(
                    "FPSController",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return property?.GetValue(player, null) as Behaviour;
            }
            catch
            {
                return null;
            }
        }

        private static Component? ResolveHealthComponent()
        {
            if (!(ResolvePlayerController() is Component player) || player == null)
            {
                return null;
            }

            return player.GetComponents<Component>()
                .Concat(player.GetComponentsInChildren<Component>(true))
                .Concat(player.GetComponentsInParent<Component>(true))
                .FirstOrDefault(candidate => candidate != null
                    && string.Equals(candidate.GetType().Name, "Health", StringComparison.Ordinal));
        }

        private static bool TryReadFloat(Component component, string memberName, out float value)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            try
            {
                var type = component.GetType();
                var raw = type.GetField(memberName, flags)?.GetValue(component)
                    ?? type.GetProperty(memberName, flags)?.GetValue(component, null);
                if (raw is float number && !float.IsNaN(number) && !float.IsInfinity(number))
                {
                    value = number;
                    return true;
                }
            }
            catch
            {
                // Game build drift is reported as an unavailable safe capability.
            }

            value = 0f;
            return false;
        }

        private static object? ResolvePlayerController()
        {
            try
            {
                var type = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetType("PlayerController", throwOnError: false))
                    .FirstOrDefault(candidate => candidate != null);
                var findPlayer = type?.GetMethod("FindPlayer", BindingFlags.Public | BindingFlags.Static);
                return findPlayer?.Invoke(null, Array.Empty<object>());
            }
            catch
            {
                return null;
            }
        }

        private sealed class PlayerControlLease : IPlayerControlLease
        {
            private UnityPlayerBackend? owner;

            public PlayerControlLease(UnityPlayerBackend owner, string reason)
            {
                this.owner = owner;
                Reason = reason;
            }

            public bool IsActive => owner != null;
            public string Reason { get; }

            public void Dispose()
            {
                UnityMainThreadGuard.AssertCurrent();
                Interlocked.Exchange(ref owner, null)?.Release();
            }
        }
    }
}
