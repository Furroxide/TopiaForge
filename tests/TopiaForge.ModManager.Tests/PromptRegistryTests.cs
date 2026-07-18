using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Prompts;

namespace TopiaForge.ModManager.Tests
{
    internal static class PromptRegistryTests
    {
        public static void Run()
        {
            TestRegisterAndReplace();
            TestHandleDispose();
            TestValidationAndRegistryDispose();
            TestConflictOwnsAnImmutableOverrideSnapshot();
            Console.WriteLine("All prompt registry tests passed.");
        }

        private static void TestRegisterAndReplace()
        {
            var registry = new PromptOverrideRegistry("alpha.mod");
            var firstResult = registry.Register(new PromptOverrideRequest("robot.greeting", "first", priority: 1));
            Assert(firstResult.TryGetValue(out var first), "a valid override should register");
            Assert(first!.Override.SourceId == "alpha.mod", "the owner-bound provider supplies the source id");

            var secondResult = registry.Register(new PromptOverrideRequest("robot.greeting", "second", priority: 5));
            Assert(secondResult.TryGetValue(out var second), "a replacement should register");
            Assert(first.IsDisposed && !second!.IsDisposed, "same-source replacement should retire the prior lease");
            Assert(registry.Overrides.Count == 1 && registry.Overrides[0].ReplacementText == "second",
                "same-source replacement should leave one active override");
            Assert(registry.TryGetEffectiveOverride("robot.greeting", out var effective)
                && ReferenceEquals(effective, second!.Override), "the active replacement should resolve");

            first.Dispose();
            Assert(registry.Overrides.Count == 1, "disposing a retired lease must not remove its replacement");
        }

        private static void TestHandleDispose()
        {
            var registry = new PromptOverrideRegistry("alpha.mod");
            var result = registry.Register(new PromptOverrideRequest("robot.greeting", "hello"));
            Assert(result.TryGetValue(out var handle), "a valid override should register");

            handle!.Dispose();
            handle.Dispose();
            Assert(handle.IsDisposed, "prompt leases should dispose idempotently");
            Assert(registry.Overrides.Count == 0 && !registry.TryGetEffectiveOverride("robot.greeting", out _),
                "disposing a lease should remove its override");
        }

        private static void TestValidationAndRegistryDispose()
        {
            var registry = new PromptOverrideRegistry("alpha.mod");
            var invalid = registry.Register(new PromptOverrideRequest("", "hello"));
            Assert(!invalid.Succeeded && invalid.ErrorCode == ModErrorCode.InvalidArgument,
                "blank prompt ids should fail with InvalidArgument");

            var registered = registry.Register(new PromptOverrideRequest("robot.greeting", "hello"));
            Assert(registered.TryGetValue(out var handle), "a valid override should register before disposal");
            registry.Dispose();
            Assert(handle!.IsDisposed && registry.Overrides.Count == 0,
                "provider disposal should retire every active lease");

            var late = registry.Register(new PromptOverrideRequest("robot.late", "late"));
            Assert(!late.Succeeded && late.ErrorCode == ModErrorCode.InvalidState,
                "registration after provider disposal should fail with InvalidState");
        }

        private static void TestConflictOwnsAnImmutableOverrideSnapshot()
        {
            var first = new PromptOverride("alpha.mod", "robot.greeting", "first", priority: 1);
            var source = new List<PromptOverride> { first };
            var conflict = new PromptConflict("robot.greeting", source, first);

            source.Add(new PromptOverride("beta.mod", "robot.greeting", "second", priority: 2));

            Assert(conflict.Overrides.Count == 1 && ReferenceEquals(conflict.Overrides[0], first),
                "prompt conflicts must snapshot caller-owned override collections");
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
