using System;
using System.Linq;
using TopiaForge.Mods;
using TopiaForge.Prompts;

namespace TopiaForge.ModManager.Tests
{
    internal static class PromptRegistryTests
    {
        public static void Run()
        {
            TestRegisterAndEffectiveOverride();
            TestReplaceSameOwnerPrompt();
            TestHandleDispose();
            TestUnregisterOwner();
            TestDisposeRetiresAllHandles();
            Console.WriteLine("All prompt registry tests passed.");
        }

        private static void TestRegisterAndEffectiveOverride()
        {
            var registry = new PromptOverrideRegistry();
            registry.Register(new PromptOverrideRequest("alpha.mod", "robot.greeting", "alpha", priority: 1));
            registry.Register(new PromptOverrideRequest("beta.mod", "robot.greeting", "beta", priority: 5));

            Assert(registry.TryGetEffectiveOverride("robot.greeting", out var effective), "effective override should resolve");
            Assert(effective!.ModId == "beta.mod" && effective.ReplacementText == "beta", "highest-priority override should win");

            var conflict = registry.GetConflicts().Single();
            Assert(conflict.PromptId == "robot.greeting", "conflict should keep the prompt id");
            Assert(conflict.EffectiveOverride!.ModId == "beta.mod", "conflict should expose the effective override");
            Assert(conflict.Overrides.Count == 2 && conflict.Overrides[0].ModId == "beta.mod", "conflict overrides should be ordered by priority");
        }

        private static void TestReplaceSameOwnerPrompt()
        {
            var registry = new PromptOverrideRegistry();
            var first = registry.Register(new PromptOverrideRequest("alpha.mod", "robot.greeting", "first"));
            var second = registry.Register(new PromptOverrideRequest("alpha.mod", "robot.greeting", "second"));

            Assert(first.IsDisposed, "replacing the same owner/prompt should retire the old handle");
            Assert(!second.IsDisposed, "replacement handle should remain active");
            Assert(registry.Overrides.Count == 1 && registry.Overrides[0].ReplacementText == "second", "replacement should keep one active override");

            first.Dispose();
            Assert(registry.Overrides.Count == 1, "disposing a retired handle should not remove the replacement");
        }

        private static void TestHandleDispose()
        {
            var registry = new PromptOverrideRegistry();
            var handle = registry.Register(new PromptOverrideRequest("alpha.mod", "robot.greeting", "hello"));
            Assert(registry.Overrides.Count == 1, "registered override should be visible");

            handle.Dispose();
            Assert(handle.IsDisposed, "handle should report disposed");
            Assert(registry.Overrides.Count == 0, "disposing a handle should unregister the override");
            Assert(!registry.TryGetEffectiveOverride("robot.greeting", out _), "disposed override should not resolve");
        }

        private static void TestUnregisterOwner()
        {
            var registry = new PromptOverrideRegistry();
            var alpha = registry.Register(new PromptOverrideRequest("alpha.mod", "robot.greeting", "alpha"));
            registry.Register(new PromptOverrideRequest("beta.mod", "robot.greeting", "beta"));

            registry.UnregisterOwner("alpha.mod");

            Assert(alpha.IsDisposed, "owner cleanup should retire handles for that owner");
            Assert(registry.Overrides.Count == 1 && registry.Overrides[0].ModId == "beta.mod", "owner cleanup should leave other owners alone");
            Assert(registry.GetConflicts().Count == 0, "conflict should clear after one owner is removed");
        }

        private static void TestDisposeRetiresAllHandles()
        {
            var registry = new PromptOverrideRegistry();
            var first = registry.Register(new PromptOverrideRequest("alpha.mod", "robot.greeting", "alpha"));
            var second = registry.Register(new PromptOverrideRequest("beta.mod", "robot.farewell", "beta"));

            registry.Dispose();

            Assert(first.IsDisposed && second.IsDisposed, "registry disposal should retire every owner handle");
            Assert(registry.Overrides.Count == 0, "registry disposal should clear all overrides");
            var threw = false;
            try
            {
                registry.Register(new PromptOverrideRequest("late.mod", "robot.late", "late"));
            }
            catch (ObjectDisposedException)
            {
                threw = true;
            }

            Assert(threw, "registering against an unloaded registry should fail explicitly");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
