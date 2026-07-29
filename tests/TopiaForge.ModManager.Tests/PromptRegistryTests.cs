using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
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
            TestWellKnownDirectivePriorityConflictAndOwnerCleanup();
            TestConcurrentWellKnownDirectiveLookup();
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

        private static void TestWellKnownDirectivePriorityConflictAndOwnerCleanup()
        {
            var registry = new PromptOverrideRegistry("provider");
            var factory = (IOwnerBoundExtensionFactory)registry;
            var lowLifetime = new FakeModLifetime();
            var highLifetime = new FakeModLifetime();
            var low = (IPromptOverrideRegistry)factory.CreateOwnerFacade(
                typeof(IPromptOverrideRegistry),
                "low.mod",
                lowLifetime);
            var high = (IPromptOverrideRegistry)factory.CreateOwnerFacade(
                typeof(IPromptOverrideRegistry),
                "high.mod",
                highLifetime);

            Assert(low.Register(new PromptOverrideRequest(
                    WellKnownPromptIds.GlobalRobotDirective,
                    "low",
                    priority: 10)).Succeeded,
                "the lower-priority global directive should register");
            Assert(high.Register(new PromptOverrideRequest(
                    WellKnownPromptIds.GlobalRobotDirective,
                    "high",
                    priority: 1000)).Succeeded,
                "the higher-priority global directive should register");
            Assert(registry.TryGetEffectiveOverride(WellKnownPromptIds.GlobalRobotDirective, out var effective)
                && effective!.ReplacementText == "high",
                "the global directive slot should use ordinary deterministic priority resolution");
            var conflicts = registry.GetConflicts();
            Assert(conflicts.Count == 1
                && conflicts[0].PromptId == WellKnownPromptIds.GlobalRobotDirective
                && conflicts[0].Overrides.Count == 2,
                "competing global robot directives should remain visible to diagnostics");

            highLifetime.Dispose();
            Assert(registry.TryGetEffectiveOverride(WellKnownPromptIds.GlobalRobotDirective, out effective)
                && effective!.ReplacementText == "low",
                "unloading the winning owner should reveal the remaining directive");
            lowLifetime.Dispose();
            Assert(!registry.TryGetEffectiveOverride(WellKnownPromptIds.GlobalRobotDirective, out _),
                "unloading the final owner should clear the global directive");
            registry.Dispose();
        }

        private static void TestConcurrentWellKnownDirectiveLookup()
        {
            var registry = new PromptOverrideRegistry("concurrent.mod");
            Assert(registry.Register(new PromptOverrideRequest(
                    WellKnownPromptIds.GlobalRobotDirective,
                    "directive",
                    priority: 1000)).Succeeded,
                "the concurrent lookup directive should register");

            Parallel.For(0, 128, _ =>
            {
                if (!registry.TryGetEffectiveOverride(WellKnownPromptIds.GlobalRobotDirective, out var value)
                    || value == null
                    || value.ReplacementText != "directive")
                {
                    throw new InvalidOperationException("concurrent global directive lookup returned inconsistent state");
                }
            });

            registry.Dispose();
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
