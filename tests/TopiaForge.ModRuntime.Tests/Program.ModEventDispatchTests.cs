using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        private static void TestModEventDispatch(string root)
        {
            TestSnapshotMutationSemantics(root);
            TestLateSubscriptionRejectedWithoutPublication(root);
            TestConsecutiveFailureCircuit(root);
            TestSuccessfulCallbackResetsFailureStreak(root);
            TestIntermittentFailureLoggingIsThrottled(root);
            TestLoggerFailureRemainsIsolated(root);
            TestHotLoopDispatchAllocatesNothing(root);
        }

        private static void TestLateSubscriptionRejectedWithoutPublication(string root)
        {
            var context = CreateEventTestContext(root, "late-registration", new RecordingModLogger());
            var calls = 0;
            context.DisposeLifetime();

            var rejected = false;
            try
            {
                context.Events.SubscribeUpdate(_ => calls++);
            }
            catch (ObjectDisposedException)
            {
                rejected = true;
            }

            context.RaiseUpdate(0.016f);
            Assert(rejected && calls == 0,
                "a subscription rejected during lifetime shutdown must never become dispatch-visible");
        }

        private static void TestSnapshotMutationSemantics(string root)
        {
            var context = CreateEventTestContext(root, "snapshot", new RecordingModLogger());
            try
            {
                var order = new List<string>();
                IDisposable? second = null;
                var thirdSubscribed = false;
                context.Events.SubscribeUpdate(_ =>
                {
                    order.Add("first");
                    second!.Dispose();
                    if (!thirdSubscribed)
                    {
                        thirdSubscribed = true;
                        context.Events.SubscribeUpdate(__ => order.Add("third"));
                    }
                });
                second = context.Events.SubscribeUpdate(_ => order.Add("second"));

                context.RaiseUpdate(0.016f);
                Assert(order.SequenceEqual(new[] { "first", "second" }),
                    "subscription changes during dispatch should apply after the current ordered snapshot");

                order.Clear();
                context.RaiseUpdate(0.016f);
                Assert(order.SequenceEqual(new[] { "first", "third" }),
                    "the rebuilt snapshot should preserve surviving subscription order");
            }
            finally
            {
                context.DisposeLifetime();
            }
        }

        private static void TestConsecutiveFailureCircuit(string root)
        {
            var logger = new RecordingModLogger();
            var context = CreateEventTestContext(root, "failure-circuit", logger);
            try
            {
                var failing = new AlwaysThrowingSubscriber();
                var healthyCalls = 0;
                var failingLease = context.Events.SubscribeUpdate(failing.OnUpdate);
                context.Events.SubscribeUpdate(_ => healthyCalls++);

                for (var index = 0; index < 8; index++)
                {
                    context.RaiseUpdate(0.016f);
                }

                Assert(failing.Calls == 3,
                    "a subscriber should be disabled after exactly three consecutive failures");
                Assert(healthyCalls == 8,
                    "a disabled subscriber must not starve later subscribers");
                Assert(logger.Errors.Count == 2,
                    "only the first failure and circuit-open transition should be logged");
                Assert(logger.Errors[0].Contains(nameof(AlwaysThrowingSubscriber.OnUpdate), StringComparison.Ordinal)
                    && logger.Errors[0].Contains("Update", StringComparison.Ordinal)
                    && logger.Errors[1].Contains("disabled", StringComparison.OrdinalIgnoreCase)
                    && logger.Errors[1].Contains("suppressed", StringComparison.OrdinalIgnoreCase),
                    "failure diagnostics should identify the handler, phase, and suppression state");

                failingLease.Dispose();
                context.Events.SubscribeUpdate(failing.OnUpdate);
                context.RaiseUpdate(0.016f);
                Assert(failing.Calls == 4 && logger.Errors.Count == 3,
                    "disposing and recreating a subscription should deterministically reset its circuit");
            }
            finally
            {
                context.DisposeLifetime();
            }
        }

        private static void TestSuccessfulCallbackResetsFailureStreak(string root)
        {
            var logger = new RecordingModLogger();
            var context = CreateEventTestContext(root, "failure-reset", logger);
            try
            {
                var subscriber = new PatternSubscriber(
                    true, true, false,
                    true, true, false,
                    true, true, true);
                context.Events.SubscribeUpdate(subscriber.OnUpdate);

                for (var index = 0; index < 12; index++)
                {
                    context.RaiseUpdate(0.016f);
                }

                Assert(subscriber.Calls == 9,
                    "successful callbacks should reset the streak before a later three-failure circuit opens");
                Assert(logger.Errors.Count == 2
                    && logger.Errors[^1].Contains("disabled", StringComparison.OrdinalIgnoreCase),
                    "success should reset the circuit streak without rearming noisy diagnostics before sustained recovery");
            }
            finally
            {
                context.DisposeLifetime();
            }
        }

        private static void TestIntermittentFailureLoggingIsThrottled(string root)
        {
            var logger = new RecordingModLogger();
            var context = CreateEventTestContext(root, "failure-throttle", logger);
            try
            {
                var alternating = new AlternatingSubscriber();
                context.Events.SubscribeUpdate(alternating.OnUpdate);
                for (var index = 0; index < 1_000; index++)
                {
                    context.RaiseUpdate(0.016f);
                }

                Assert(alternating.Calls == 1_000 && logger.Errors.Count == 1,
                    "alternating failures and successes must not produce an unbounded log stream");

                logger.Errors.Clear();
                var controlled = new ControlledSubscriber { ShouldThrow = true };
                context.Events.SubscribeFixedUpdate(controlled.OnFixedUpdate);
                var sample = new GameTimeSample(GameLoopPhase.Fixed, 0.02f, 0.02f, 1d, 1);
                context.RaiseFixedUpdate(sample);
                controlled.ShouldThrow = false;
                for (var index = 0; index < 60; index++)
                {
                    context.RaiseFixedUpdate(sample);
                }
                controlled.ShouldThrow = true;
                context.RaiseFixedUpdate(sample);

                Assert(logger.Errors.Count == 2,
                    "sixty consecutive healthy callbacks should rearm one diagnostic for a later failure episode");
            }
            finally
            {
                context.DisposeLifetime();
            }
        }

        private static void TestLoggerFailureRemainsIsolated(string root)
        {
            var context = CreateEventTestContext(root, "logger-isolation", new ThrowingModLogger());
            try
            {
                var laterCalls = 0;
                context.Events.SubscribeUpdate(_ => throw new InvalidOperationException("expected callback failure"));
                context.Events.SubscribeUpdate(_ => laterCalls++);
                context.RaiseUpdate(0.016f);

                Assert(laterCalls == 1,
                    "a broken diagnostic sink must not turn an isolated callback failure into dispatch failure");
            }
            finally
            {
                context.DisposeLifetime();
            }
        }

        private static void TestHotLoopDispatchAllocatesNothing(string root)
        {
            var context = CreateEventTestContext(root, "allocation", new RecordingModLogger());
            try
            {
                var probe = new AllocationProbe();
                context.Events.SubscribeUpdate(probe.OnUpdate);
                context.Events.SubscribeFixedUpdate(probe.OnFixedUpdate);
                context.Events.SubscribeLateUpdate(probe.OnLateUpdate);
                var fixedSample = new GameTimeSample(GameLoopPhase.Fixed, 0.02f, 0.02f, 1d, 1);
                var lateSample = new GameTimeSample(GameLoopPhase.Late, 0.016f, 0.016f, 1d, 1);

                for (var index = 0; index < 32; index++)
                {
                    context.RaiseUpdate(0.016f);
                    context.RaiseFixedUpdate(fixedSample);
                    context.RaiseLateUpdate(lateSample);
                }

                _ = GC.GetAllocatedBytesForCurrentThread();
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var index = 0; index < 10_000; index++)
                {
                    context.RaiseUpdate(0.016f);
                    context.RaiseFixedUpdate(fixedSample);
                    context.RaiseLateUpdate(lateSample);
                }
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                if (allocated != 0)
                {
                    throw new InvalidOperationException(
                        "ModRuntime integration test failed: hot-loop event dispatch allocated " + allocated + " bytes");
                }

                Assert(probe.UpdateCalls == 10_032
                    && probe.FixedCalls == 10_032
                    && probe.LateCalls == 10_032,
                    "allocation measurement should still invoke every healthy hot-loop subscriber");
            }
            finally
            {
                context.DisposeLifetime();
            }
        }

        private static ModContext CreateEventTestContext(string root, string name, IModLogger logger)
        {
            var testRoot = Path.Combine(root, "event-dispatch-" + name);
            var packagePath = Path.Combine(testRoot, "package");
            Directory.CreateDirectory(packagePath);
            var paths = new ManagerPaths(testRoot);
            paths.EnsureCreated();
            return new ModContext(
                new ModManifest
                {
                    SchemaVersion = 5,
                    Id = "tests.events." + name,
                    Name = "Event dispatch " + name,
                    Version = "1.0.0",
                    EntryAssembly = "Events.dll",
                    EntryType = "Tests.Events"
                },
                paths,
                packagePath,
                logger,
                new ModServiceRegistry(),
                new RuntimeInfo("0.0.2309"));
        }

        private sealed class AlwaysThrowingSubscriber
        {
            public int Calls { get; private set; }

            public void OnUpdate(float deltaTime)
            {
                _ = deltaTime;
                Calls++;
                throw new InvalidOperationException("expected repeated failure");
            }
        }

        private sealed class PatternSubscriber
        {
            private readonly bool[] failures;

            public PatternSubscriber(params bool[] fail)
            {
                failures = fail;
            }

            public int Calls { get; private set; }

            public void OnUpdate(float deltaTime)
            {
                _ = deltaTime;
                var fail = failures[Calls];
                Calls++;
                if (fail)
                {
                    throw new InvalidOperationException("expected patterned failure");
                }
            }
        }

        private sealed class AlternatingSubscriber
        {
            public int Calls { get; private set; }

            public void OnUpdate(float deltaTime)
            {
                _ = deltaTime;
                Calls++;
                if ((Calls & 1) != 0)
                {
                    throw new InvalidOperationException("expected intermittent failure");
                }
            }
        }

        private sealed class ControlledSubscriber
        {
            public bool ShouldThrow { get; set; }

            public void OnFixedUpdate(GameTimeSample sample)
            {
                _ = sample;
                if (ShouldThrow)
                {
                    throw new InvalidOperationException("expected controlled failure");
                }
            }
        }

        private sealed class AllocationProbe
        {
            public int UpdateCalls { get; private set; }
            public int FixedCalls { get; private set; }
            public int LateCalls { get; private set; }

            public void OnUpdate(float deltaTime)
            {
                _ = deltaTime;
                UpdateCalls++;
            }

            public void OnFixedUpdate(GameTimeSample sample)
            {
                _ = sample;
                FixedCalls++;
            }

            public void OnLateUpdate(GameTimeSample sample)
            {
                _ = sample;
                LateCalls++;
            }
        }

        private sealed class RecordingModLogger : IModLogger
        {
            public List<string> Errors { get; } = new List<string>();

            public void Debug(string message) { }
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message) => Errors.Add(message);
            public void Error(Exception exception, string message) =>
                Errors.Add(message + ": " + exception.Message);
        }

        private sealed class ThrowingModLogger : IModLogger
        {
            public void Debug(string message) { }
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message) => throw new InvalidOperationException("broken logger");
            public void Error(Exception exception, string message) =>
                throw new InvalidOperationException("broken logger", exception);
        }
    }
}
