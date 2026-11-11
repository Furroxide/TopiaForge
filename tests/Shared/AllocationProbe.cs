using System;
using System.Runtime;
using System.Runtime.CompilerServices;

namespace TopiaForge.TestSupport
{
    // One way to measure what settled calls allocate, shared by every harness and by the probes compiled from source.
    internal static class AllocationProbe
    {
        private const int Passes = 3;

        /// <summary>
        /// What <paramref name="frames"/> calls of <paramref name="frame"/> allocate on this thread, after
        /// <paramref name="warmups"/> unmeasured calls. The calls run in a method of their own, up to three times, and the
        /// fewest bytes count. Work the runtime does on this thread while code first runs, such as compiling a method or
        /// moving a long loop to optimized code part way through, happens once, while an allocation in the frame shows in
        /// every pass. Each pass that allocates is printed with its first allocating call and the number of methods the
        /// runtime compiled on this thread during it, so a failure names what it saw.
        /// </summary>
        internal static long Measure(string name, Action frame, int frames, int warmups = 0)
        {
            Calls(frame, warmups, out _);
            var least = long.MaxValue;
            for (var pass = 1; pass <= Passes && least != 0; pass++)
            {
                var compiled = JitInfo.GetCompiledMethodCount(currentThread: true);
                var bytes = Calls(frame, frames, out var first);
                compiled = JitInfo.GetCompiledMethodCount(currentThread: true) - compiled;
                least = Math.Min(least, bytes);
                if (bytes != 0)
                    Console.WriteLine("Allocation probe \"" + name + "\": pass " + pass + " of " + Passes + " allocated " + bytes
                        + " bytes over " + frames + " calls, first at call " + first + "; the runtime compiled " + compiled
                        + " methods on this thread during the pass.");
            }

            return least;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static long Calls(Action frame, int count, out int first)
        {
            first = 0;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < count; index++)
            {
                frame();
                if (first == 0 && GC.GetAllocatedBytesForCurrentThread() != before) first = index + 1;
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }
}
