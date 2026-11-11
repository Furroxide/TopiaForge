using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace TopiaForge.Mods.Testing
{
    internal readonly struct FakeGameThread
    {
        private readonly int threadId;

        private FakeGameThread(int threadId)
        {
            this.threadId = threadId;
        }

        internal bool IsEnabled => threadId != 0;

        internal static FakeGameThread Capture() => new FakeGameThread(Thread.CurrentThread.ManagedThreadId);

        internal void AssertCurrent([CallerMemberName] string operation = "SDK operation")
        {
            if (IsEnabled && Thread.CurrentThread.ManagedThreadId != threadId)
            {
                throw new InvalidOperationException(
                    "TFSDK100: " + operation + " must be called on the thread that created this thread-checked context. " +
                    "Use Context.Scheduler.NextFrame when continuing work from a background task.");
            }
        }
    }
}
