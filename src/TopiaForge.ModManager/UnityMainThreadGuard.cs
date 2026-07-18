using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace TopiaForge.ModManager
{
    /// <summary>Prevents engine-facing SDK adapters from ever touching Unity off the game thread.</summary>
    internal static class UnityMainThreadGuard
    {
        private static int mainThreadId;

        public static void CaptureCurrentThread()
        {
            var current = Thread.CurrentThread.ManagedThreadId;
            var captured = Interlocked.CompareExchange(ref mainThreadId, current, 0);
            if (captured != 0 && captured != current)
            {
                throw new InvalidOperationException(
                    "TFSDK100: TopiaForge's game-thread owner was initialized from more than one thread.");
            }
        }

        public static void AssertCurrent([CallerMemberName] string operation = "SDK operation")
        {
            var expected = Volatile.Read(ref mainThreadId);
            if (expected == 0)
            {
                throw new InvalidOperationException(
                    "TFSDK100: The TopiaForge game-thread owner has not been initialized.");
            }

            if (Thread.CurrentThread.ManagedThreadId != expected)
            {
                throw new InvalidOperationException(
                    "TFSDK100: " + operation + " must be called on the Robotopia game thread. " +
                    "Use Context.Scheduler.NextFrame when continuing work from a background task.");
            }
        }
    }
}
