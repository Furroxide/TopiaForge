using System;
using System.Collections.Generic;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class TestingKitTests
    {
        private static void TestLifecycleAndLeaks()
        {
            var order = new List<string>();
            var context = new FakeModContext();
            var runner = new ModLifecycleRunner(new ProbeMod(order), context);
            runner.Load();
            Assert(runner.IsLoaded && context.Input.ActiveActionCount == 1 &&
                   context.Events.ActiveSubscriptionCount == 1,
                "runner attaches a complete context before OnLoad");
            runner.Unload();
            Assert(string.Join(",", order) == "load,unload,second,first",
                "runner calls OnUnload before reverse-order lifetime cleanup");
            context.AssertNoLeaks();
            Assert(runner.IsFinished, "runner records completed lifecycle state");
        }

        private static void TestDisposeNeverHidesTheRealFailure()
        {
            // A `using var runner = ...` block disposes while an assertion failure is already unwinding the
            // stack. If Dispose rethrew the cleanup error it would replace the failure the test just found,
            // so the author would debug the wrong problem. Dispose records instead.
            var caught = string.Empty;
            var runner = new ModLifecycleRunner(new ThrowingUnloadMod(), new FakeModContext());
            try
            {
                try
                {
                    runner.Load();
                    throw new InvalidOperationException("the real assertion failure");
                }
                finally
                {
                    runner.Dispose();
                }
            }
            catch (Exception exception)
            {
                caught = exception.Message;
            }

            Assert(caught == "the real assertion failure",
                "Dispose must not replace the failure that was already unwinding the stack");
            Assert(runner.CleanupFailures.Count == 1 &&
                   runner.CleanupFailures[0].Message == "expected unload failure",
                "Dispose records the cleanup failure it swallowed");
            Assert(runner.IsFinished && !runner.IsLoaded,
                "Dispose still completes the lifecycle after a failing OnUnload");

            // Explicit Unload keeps the throwing contract, so a test that wants to assert on cleanup can.
            var thrown = false;
            var explicitRunner = new ModLifecycleRunner(new ThrowingUnloadMod(), new FakeModContext());
            explicitRunner.Load();
            try
            {
                explicitRunner.Unload();
            }
            catch (InvalidOperationException)
            {
                thrown = true;
            }

            Assert(thrown, "explicit Unload still surfaces a failing OnUnload");
            Assert(explicitRunner.CleanupFailures.Count == 0,
                "Unload reports through the exception, not through CleanupFailures");
        }
    }
}
