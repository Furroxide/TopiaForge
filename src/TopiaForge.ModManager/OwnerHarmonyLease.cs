using System;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using TopiaForge.Mods;
using TopiaForge.Mods.Interop.Unity;

namespace TopiaForge.ModManager
{
    internal interface IHarmonyPatchBackend
    {
        void Patch(
            MethodBase original,
            MethodInfo? prefix,
            MethodInfo? postfix,
            MethodInfo? transpiler,
            MethodInfo? finalizer);

        void UnpatchSelf();
    }

    /// <summary>Runtime-owned Harmony identity and teardown for one mod patch group.</summary>
    internal sealed class OwnerHarmonyLease : IHarmonyLease
    {
        private static long nextInstance;
        private readonly object sync = new object();
        private readonly IHarmonyPatchBackend backend;
        private IDisposable? lifetimeLease;
        private int disposeState;

        private OwnerHarmonyLease(
            string ownerModId,
            string purpose,
            IHarmonyPatchBackend? backend = null)
        {
            HarmonyId = CreateHarmonyId(ownerModId, purpose);
            this.backend = backend ?? new HarmonyPatchBackend(HarmonyId);
        }

        internal string HarmonyId { get; }

        public void Patch(
            MethodBase original,
            MethodInfo? prefix = null,
            MethodInfo? postfix = null,
            MethodInfo? transpiler = null,
            MethodInfo? finalizer = null)
        {
            if (original == null)
            {
                throw new ArgumentNullException(nameof(original));
            }

            UnityMainThreadGuard.AssertCurrent();
            lock (sync)
            {
                ThrowIfDisposed();
                backend.Patch(original, prefix, postfix, transpiler, finalizer);
            }
        }

        public bool IsDisposed => Volatile.Read(ref disposeState) == 2;

        public static OwnerHarmonyLease Create(string ownerModId, string purpose, IModLifetime lifetime)
        {
            return CreateCore(ownerModId, purpose, lifetime, backend: null);
        }

        internal static OwnerHarmonyLease CreateForTesting(
            string ownerModId,
            string purpose,
            IModLifetime lifetime,
            IHarmonyPatchBackend backend)
        {
            if (backend == null)
            {
                throw new ArgumentNullException(nameof(backend));
            }

            return CreateCore(ownerModId, purpose, lifetime, backend);
        }

        private static OwnerHarmonyLease CreateCore(
            string ownerModId,
            string purpose,
            IModLifetime lifetime,
            IHarmonyPatchBackend? backend)
        {
            Validate(ownerModId, purpose, lifetime);
            var lease = new OwnerHarmonyLease(ownerModId, purpose, backend);
            lease.AttachLifetimeLease(lifetime.Track(lease));
            return lease;
        }

        public void Dispose()
        {
            // Once teardown completed, IDisposable idempotence does not require re-entering the Unity/Harmony
            // boundary. This also makes an already-disposed lease safe to release from generic worker cleanup.
            if (Volatile.Read(ref disposeState) == 2)
            {
                return;
            }

            UnityMainThreadGuard.AssertCurrent();
            IDisposable? tracking;
            lock (sync)
            {
                if (disposeState != 0)
                {
                    return;
                }

                disposeState = 1;
                try
                {
                    // Patch() uses the same lock, so no patch can pass its active check and land after this sweep.
                    backend.UnpatchSelf();
                    Volatile.Write(ref disposeState, 2);
                    tracking = Interlocked.Exchange(ref lifetimeLease, null);
                }
                catch
                {
                    // Keep the lease retryable: an eager teardown failure can be attempted again by lifetime cleanup.
                    Volatile.Write(ref disposeState, 0);
                    throw;
                }
            }

            tracking?.Dispose();
        }

        private static string CreateHarmonyId(string ownerModId, string purpose)
        {
            var sequence = Interlocked.Increment(ref nextInstance);
            return "topiaforge." + Normalize(ownerModId) + ".harmony." + Normalize(purpose) + "." +
                sequence.ToString(CultureInfo.InvariantCulture);
        }

        private static void Validate(string ownerModId, string purpose, IModLifetime lifetime)
        {
            if (string.IsNullOrWhiteSpace(ownerModId))
            {
                throw new ArgumentException("An owner mod id is required.", nameof(ownerModId));
            }

            if (string.IsNullOrWhiteSpace(purpose))
            {
                throw new ArgumentException("A Harmony patch purpose is required.", nameof(purpose));
            }

            if (purpose.Length > 64)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(purpose),
                    "A Harmony patch purpose cannot exceed 64 characters.");
            }

            if (Normalize(purpose).Length == 0)
            {
                throw new ArgumentException(
                    "A Harmony patch purpose must contain a letter, number, dot, underscore, or hyphen.",
                    nameof(purpose));
            }

            if (lifetime == null)
            {
                throw new ArgumentNullException(nameof(lifetime));
            }
        }

        private void AttachLifetimeLease(IDisposable lease)
        {
            if (lease == null)
            {
                throw new ArgumentNullException(nameof(lease));
            }

            if (Interlocked.CompareExchange(ref lifetimeLease, lease, null) != null)
            {
                lease.Dispose();
                throw new InvalidOperationException("A Harmony lease cannot be attached to more than one mod lifetime.");
            }

            if (IsDisposed)
            {
                Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref disposeState) != 0)
            {
                throw new ObjectDisposedException(nameof(OwnerHarmonyLease));
            }
        }

        private static string Normalize(string value)
        {
            var result = new StringBuilder(value.Length);
            var previousWasSeparator = false;
            foreach (var character in value)
            {
                var normalized = char.ToLowerInvariant(character);
                if (char.IsLetterOrDigit(normalized) || normalized == '.' || normalized == '_' || normalized == '-')
                {
                    result.Append(normalized);
                    previousWasSeparator = false;
                }
                else if (!previousWasSeparator)
                {
                    result.Append('-');
                    previousWasSeparator = true;
                }
            }

            return result.ToString().Trim('-', '.');
        }

        private sealed class HarmonyPatchBackend : IHarmonyPatchBackend
        {
            private readonly Harmony harmony;

            public HarmonyPatchBackend(string harmonyId)
            {
                harmony = new Harmony(harmonyId);
            }

            public void Patch(
                MethodBase original,
                MethodInfo? prefix,
                MethodInfo? postfix,
                MethodInfo? transpiler,
                MethodInfo? finalizer)
            {
                harmony.Patch(
                    original,
                    prefix == null ? null : new HarmonyMethod(prefix),
                    postfix == null ? null : new HarmonyMethod(postfix),
                    transpiler == null ? null : new HarmonyMethod(transpiler),
                    finalizer == null ? null : new HarmonyMethod(finalizer),
                    ilmanipulator: null);
            }

            public void UnpatchSelf()
            {
                harmony.UnpatchSelf();
            }
        }
    }
}
