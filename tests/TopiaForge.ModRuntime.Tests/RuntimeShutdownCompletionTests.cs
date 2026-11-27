using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager;
using TopiaForge.Mods;

namespace TopiaForge.ModRuntime.Tests
{
    internal static class RuntimeShutdownCompletionTests
    {
        internal static void Run()
        {
            foreach (var fault in new[] { false, true })
            {
                using var dispatcher = new HostDispatcher();
                var shutdown = new TaskCompletionSource<OperationResult<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
                var thread = Thread.CurrentThread.ManagedThreadId;
                var calls = 0;
                var reports = 0;
                var observedSuccess = false;
                var cleanupThread = 0;
                var completed = RuntimeShutdownCompletion.Observe(dispatcher, shutdown.Task, result =>
                {
                    calls++;
                    observedSuccess = result.Succeeded;
                    cleanupThread = Thread.CurrentThread.ManagedThreadId;
                }, _ => { reports++; throw new InvalidOperationException("diagnostic sink stopped"); });
                Assert(calls == 0 && !completed.IsCompleted && dispatcher.HasPendingWork,
                    "The process host retains final cleanup while admitted runtime work drains.");
                Task.Run(() =>
                {
                    if (fault) shutdown.SetException(new InvalidOperationException("runtime drain fault"));
                    else shutdown.SetResult(OperationResult<bool>.Success(true));
                }).GetAwaiter().GetResult();
                Pump(dispatcher, completed);
                Assert(calls == 1 && observedSuccess == !fault && cleanupThread == thread,
                    "Exactly one final cleanup runs on the host after either drain outcome.");
                Assert(reports == (fault ? 1 : 0) && completed.IsCompletedSuccessfully,
                    "Faulted drain and throwing diagnostic callbacks remain observed.");
            }
            using (var dispatcher = new HostDispatcher())
            {
                var reports = 0;
                var completed = RuntimeShutdownCompletion.Observe(dispatcher,
                    Task.FromResult(OperationResult<bool>.Success(true)),
                    _ => throw new InvalidOperationException("final cleanup failed"), _ => reports++);
                Pump(dispatcher, completed);
                Assert(reports == 1 && completed.IsCompletedSuccessfully, "Final cleanup failures remain observed after the Unity callback returns.");
            }
            Console.WriteLine("Runtime shutdown completion tests passed.");
        }
        private static void Pump(HostDispatcher dispatcher, Task task)
        {
            var deadline = Stopwatch.StartNew();
            while ((!task.IsCompleted || dispatcher.HasPendingWork) && deadline.Elapsed < TimeSpan.FromSeconds(5))
            { dispatcher.Drain(); Thread.Yield(); }
            Assert(task.IsCompleted, "Host shutdown observer did not complete.");
        }
        private static void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }
}
