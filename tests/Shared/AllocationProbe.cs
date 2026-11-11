using System;
using System.Runtime;
using System.Runtime.CompilerServices;

namespace TopiaForge.TestSupport
{
    // One way to measure what settled calls allocate, shared by every harness and by the probes compiled from source.
    internal static class AllocationProbe
    {
        /// <summary>
        /// What <paramref name="frames"/> calls of <paramref name="frame"/> allocate on this thread, after
        /// <paramref name="warmups"/> unmeasured calls. The calls run in a method of their own that is compiled once,
        /// before anything is measured, so the runtime never replaces its loop part way through. A first pass that
        /// allocates nothing settles it. Otherwise two more passes run and the larger of them counts: work the runtime
        /// does once on this thread while code first runs, such as compiling a method the warm-up did not reach, is
        /// forgiven, while anything that allocates again is not. Each pass that allocates is written to standard error
        /// with its first allocating call and the number of methods the runtime compiled on this thread during it, so a
        /// failure names what it saw.
        /// </summary>
        internal static long Measure(string name, Action frame, int frames, int warmups = 0)
        {
            if (frames <= 0) throw new ArgumentOutOfRangeException(nameof(frames));
            Calls(frame, warmups, out _, out _);
            if (Pass(name, frame, frames, 1) == 0) return 0;
            var second = Pass(name, frame, frames, 2);
            return Math.Max(second, Pass(name, frame, frames, 3));
        }

        private static long Pass(string name, Action frame, int frames, int pass)
        {
            var compiled = JitInfo.GetCompiledMethodCount(currentThread: true);
            var bytes = Calls(frame, frames, out var first, out var firstBytes);
            compiled = JitInfo.GetCompiledMethodCount(currentThread: true) - compiled;
            if (bytes != 0)
                Console.Error.WriteLine("Allocation probe \"" + name + "\": pass " + pass + " allocated " + bytes + " bytes over "
                    + frames + " calls, first " + firstBytes + " bytes at call " + first + "; the runtime compiled " + compiled
                    + " methods on this thread during the pass.");
            return bytes;
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        private static long Calls(Action frame, int count, out int first, out long firstBytes)
        {
            first = 0;
            firstBytes = 0;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < count; index++)
            {
                frame();
                if (first != 0) continue;
                firstBytes = GC.GetAllocatedBytesForCurrentThread() - before;
                if (firstBytes != 0) first = index + 1;
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }
}
