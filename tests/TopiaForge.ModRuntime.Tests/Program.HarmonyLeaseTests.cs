using System;
using System.Reflection;
using System.Threading.Tasks;
using TopiaForge.ModManager;
using TopiaForge.Mods.Interop.Unity;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        private static void TestOwnerHarmonyLeaseCleanup()
        {
            var lifetime = new OwnerModLifetime();
            var firstBackend = new RecordingHarmonyPatchBackend();
            var secondBackend = new RecordingHarmonyPatchBackend();
            var first = OwnerHarmonyLease.CreateForTesting(
                "Example.Mod", "Primary Patches", lifetime, firstBackend);
            var second = OwnerHarmonyLease.CreateForTesting(
                "Example.Mod", "Primary Patches", lifetime, secondBackend);
            Assert(first.HarmonyId.StartsWith(
                    "topiaforge.example.mod.harmony.primary-patches.",
                    StringComparison.Ordinal),
                "Harmony ids should be derived from normalized owner and purpose metadata");
            Assert(!string.Equals(first.HarmonyId, second.HarmonyId, StringComparison.Ordinal),
                "each Harmony lease should receive a process-unique owner id");

            var target = typeof(Program).GetMethod(
                nameof(HarmonyLeaseTarget),
                BindingFlags.NonPublic | BindingFlags.Static)!;
            var prefix = typeof(Program).GetMethod(
                nameof(HarmonyLeasePrefix),
                BindingFlags.NonPublic | BindingFlags.Static)!;
            first.Patch(target, prefix: prefix);
            Assert(firstBackend.PatchCount == 1
                && ReferenceEquals(firstBackend.Original, target)
                && ReferenceEquals(firstBackend.Prefix, prefix),
                "the lease should route patch application through its private owner backend");

            second.Dispose();
            Assert(second.IsDisposed && secondBackend.UnpatchCount == 1,
                "early Harmony lease disposal should unpatch once and be observable");
            second.Dispose();
            var repeatedWorkerDisposeFailure = Task.Run(() =>
            {
                try
                {
                    second.Dispose();
                    return (Exception?)null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            }).GetAwaiter().GetResult();
            Assert(repeatedWorkerDisposeFailure == null,
                "an already-disposed Harmony lease should remain idempotent when generic worker cleanup repeats disposal");

            var affinityLifetime = new OwnerModLifetime();
            var affinityBackend = new RecordingHarmonyPatchBackend();
            var affinityLease = OwnerHarmonyLease.CreateForTesting(
                "Example.Mod", "Thread Affinity", affinityLifetime, affinityBackend);
            var workerPatchFailure = Task.Run(() =>
            {
                try
                {
                    affinityLease.Patch(target, prefix: prefix);
                    return (Exception?)null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            }).GetAwaiter().GetResult();
            Assert(workerPatchFailure is InvalidOperationException
                && workerPatchFailure.Message.StartsWith("TFSDK100:", StringComparison.Ordinal),
                "first patch application should reject worker-thread access with the SDK thread diagnostic");

            var workerFirstDisposeFailure = Task.Run(() =>
            {
                try
                {
                    affinityLease.Dispose();
                    return (Exception?)null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            }).GetAwaiter().GetResult();
            Assert(workerFirstDisposeFailure is InvalidOperationException
                && workerFirstDisposeFailure.Message.StartsWith("TFSDK100:", StringComparison.Ordinal)
                && !affinityLease.IsDisposed,
                "first Harmony teardown should stay on the game thread and remain retryable after rejection");
            affinityLease.Dispose();
            affinityLifetime.Dispose();
            lifetime.Dispose();

            Assert(first.IsDisposed && firstBackend.UnpatchCount == 1,
                "mod lifetime teardown must remove every patch owned by the lease exactly once");

            var rejectedDisposedAccess = false;
            try
            {
                first.Patch(target, prefix: prefix);
            }
            catch (ObjectDisposedException)
            {
                rejectedDisposedAccess = true;
            }

            Assert(rejectedDisposedAccess,
                "a disposed lease must not acquire untracked patches");
            Assert(firstBackend.PatchCount == 1,
                "a rejected post-dispose patch must not reach the backend after the teardown sweep");

            var retryLifetime = new OwnerModLifetime();
            var retryBackend = new RecordingHarmonyPatchBackend { UnpatchFailuresRemaining = 1 };
            var retryLease = OwnerHarmonyLease.CreateForTesting(
                "Example.Mod", "Retry Teardown", retryLifetime, retryBackend);
            var eagerFailure = false;
            try
            {
                retryLease.Dispose();
            }
            catch (InvalidOperationException)
            {
                eagerFailure = true;
            }

            Assert(eagerFailure && !retryLease.IsDisposed && retryBackend.UnpatchCount == 1,
                "a failed eager teardown should leave the owner lease retryable");
            retryLifetime.Dispose();
            Assert(retryLease.IsDisposed && retryBackend.UnpatchCount == 2,
                "lifetime cleanup should retry and complete a failed eager teardown");

            var rejectedEmptyNormalizedPurpose = false;
            try
            {
                using var invalidLifetime = new OwnerModLifetime();
                OwnerHarmonyLease.CreateForTesting(
                    "Example.Mod",
                    "!!!",
                    invalidLifetime,
                    new RecordingHarmonyPatchBackend());
            }
            catch (ArgumentException)
            {
                rejectedEmptyNormalizedPurpose = true;
            }

            Assert(rejectedEmptyNormalizedPurpose,
                "Harmony lease purposes that normalize to an empty id segment must be rejected");

            var rejectedEmptyNormalizedOwner = false;
            try
            {
                using var invalidLifetime = new OwnerModLifetime();
                OwnerHarmonyLease.CreateForTesting(
                    "!!!",
                    "Primary Patches",
                    invalidLifetime,
                    new RecordingHarmonyPatchBackend());
            }
            catch (ArgumentException)
            {
                rejectedEmptyNormalizedOwner = true;
            }

            Assert(rejectedEmptyNormalizedOwner,
                "Harmony lease owners that normalize to an empty id segment must be rejected");
        }

        private static int HarmonyLeaseTarget() => 1;

        private static bool HarmonyLeasePrefix(ref int __result)
        {
            __result = 2;
            return false;
        }

        private sealed class RecordingHarmonyPatchBackend : IHarmonyPatchBackend
        {
            public int PatchCount { get; private set; }
            public int UnpatchCount { get; private set; }
            public int UnpatchFailuresRemaining { get; set; }
            public MethodBase? Original { get; private set; }
            public MethodInfo? Prefix { get; private set; }

            public void Patch(
                MethodBase original,
                MethodInfo? prefix,
                MethodInfo? postfix,
                MethodInfo? transpiler,
                MethodInfo? finalizer)
            {
                _ = postfix;
                _ = transpiler;
                _ = finalizer;
                PatchCount++;
                Original = original;
                Prefix = prefix;
            }

            public void UnpatchSelf()
            {
                UnpatchCount++;
                if (UnpatchFailuresRemaining > 0)
                {
                    UnpatchFailuresRemaining--;
                    throw new InvalidOperationException("expected synthetic unpatch failure");
                }
            }
        }
    }
}
