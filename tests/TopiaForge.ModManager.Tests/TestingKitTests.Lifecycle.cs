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

    }
}
