using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using static TopiaForge.ModManager.Tests.MultiplayerModelAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The intent scope's mechanics in Core (WS3.4), with both of its ways to run a record: reusing a cached execution
    /// context, as .NET can, and setting and restoring the record, as the game's Mono must. A scope ends with its callback;
    /// awaits carry it; worker threads never observe it while an await that returns to the main thread does; nested scopes
    /// replace and restore; failures restore; and the steady state of the reusable way allocates nothing.
    /// </summary>
    internal static class MultiplayerIntentScopeTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        internal static void Run()
        {
            True(IntentScope.RuntimeCanReuseContexts, ".NET runs one captured execution context more than once");
            foreach (var reuse in new[] { true, false })
            {
                var name = reuse ? "reusing contexts" : "setting and restoring";
                AScopeEndsWithItsCallback(reuse, name);
                AwaitsKeepTheIntent(reuse, name);
                WorkerThreadsNeverObserveIt(reuse, name);
                NestedScopesReplaceAndRestore(reuse, name);
                FailuresRestoreTheScope(reuse, name);
                OtherThreadsOpenNothing(reuse, name);
            }

            TheSteadyStateAllocatesNothing();
            Console.WriteLine("MultiplayerIntentScopeTests passed.");
        }

        internal static IntentRecord Record(string modId, IntentSource source = IntentSource.UiCallback, long epoch = 1) =>
            new IntentRecord(source, modId, null, epoch);

        private static void AScopeEndsWithItsCallback(bool reuse, string name)
        {
            var scope = new IntentScope(reuse);
            Equal(reuse, scope.ReusesContexts, name + ": the strategy is the one asked for");
            var record = Record("io.example.builder");
            True(scope.Flowing == null, name + ": nothing flows outside a scope");
            IntentRecord? seen = null;
            scope.Run(record, _ => seen = scope.Flowing, null);
            True(ReferenceEquals(seen, record), name + ": the callback acts on the record");
            True(scope.Flowing == null, name + ": and it ends with the callback (LaterFrame)");

            using (scope.Enter(record))
            {
                True(ReferenceEquals(scope.Flowing, record), name + ": an entered record flows to the code that follows");
            }

            True(scope.Flowing == null, name + ": until its token is disposed");
            var token = scope.Enter(record);
            True(token.Restore != null, name + ": a token that changed something says what it restores");
            scope.Restore(token.Restore);
            True(scope.Flowing == null, name + ": restoring by that object, as the kit's hook does, ends it too");
            scope.Restore(null);
            True(scope.Flowing == null, name + ": restoring nothing changes nothing");
        }

        private static void AwaitsKeepTheIntent(bool reuse, string name)
        {
            var scope = new IntentScope(reuse);
            var record = Record("io.example.builder");
            using var pump = new MainThreadPump();
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<(IntentRecord? Yielded, IntentRecord? Released, IntentRecord? Again)>? flow = null;
            scope.Run(record, _ => flow = Continue(), null);
            True(scope.Flowing == null && flow != null && !flow.IsCompleted, name + ": the scope ends while its continuation waits");

            // The await's work completes at the top level, where no intent flows; the continuation still acts on it.
            pump.RunPending();
            True(scope.Flowing == null, name + ": pumping the frame's work leaves the top level without intent");
            gate.SetResult(true);
            pump.RunUntil(() => flow!.IsCompleted, Timeout);
            var (yielded, released, again) = flow!.GetAwaiter().GetResult();
            True(ReferenceEquals(yielded, record), name + ": after a yield the continuation acts on the intent (AwaitContinuation)");
            True(ReferenceEquals(released, record), name + ": and after an await completed from scripted code");
            True(ReferenceEquals(again, record), name + ": and after every await that follows");
            True(scope.Flowing == null, name + ": while the top level still has none");

            async Task<(IntentRecord?, IntentRecord?, IntentRecord?)> Continue()
            {
                await Task.Yield();
                var afterYield = scope.Flowing;
                await gate.Task;
                var afterGate = scope.Flowing;
                await Task.Yield();
                return (afterYield, afterGate, scope.Flowing);
            }
        }

        private static void WorkerThreadsNeverObserveIt(bool reuse, string name)
        {
            var scope = new IntentScope(reuse);
            var record = Record("io.example.builder");
            using var pump = new MainThreadPump();
            Task<IntentRecord?>? onWorker = null;
            Task<(IntentRecord? Worker, IntentRecord? Back)>? roundTrip = null;
            scope.Run(record, _ =>
            {
                onWorker = Task.Run(() => scope.Flowing);
                roundTrip = AwaitWorker();
            }, null);
            pump.RunUntil(() => onWorker!.IsCompleted && roundTrip!.IsCompleted, Timeout);
            True(onWorker!.Result == null, name + ": Task.Run's thread observes no intent (WorkerThread)");
            var (worker, back) = roundTrip!.Result;
            True(worker == null, name + ": nor does the work an await hands it");
            True(ReferenceEquals(back, record), name + ": and the continuation back on the main thread acts on it again");

            async Task<(IntentRecord?, IntentRecord?)> AwaitWorker()
            {
                var seen = await Task.Run(() => scope.Flowing);
                return (seen, scope.Flowing);
            }
        }

        private static void NestedScopesReplaceAndRestore(bool reuse, string name)
        {
            var scope = new IntentScope(reuse);
            var outer = Record("io.example.builder", IntentSource.InputActiveFrame);
            var inner = Record("io.example.builder", IntentSource.OwnerCommand);
            var other = Record("io.example.neighbour", IntentSource.UiCallback);
            var trace = new System.Collections.Generic.List<string>();
            scope.Run(outer, _ =>
            {
                trace.Add(Describe(scope.Flowing));
                scope.Run(inner, __ =>
                {
                    trace.Add(Describe(scope.Flowing));
                    scope.Run(null, ___ => trace.Add(Describe(scope.Flowing)), null);
                    trace.Add(Describe(scope.Flowing));
                    using (scope.Enter(other)) trace.Add(Describe(scope.Flowing));
                    trace.Add(Describe(scope.Flowing));
                }, null);
                trace.Add(Describe(scope.Flowing));
                scope.Run(outer, __ => trace.Add(Describe(scope.Flowing)), null);
                using (scope.Enter(outer)) trace.Add(Describe(scope.Flowing));
                trace.Add(Describe(scope.Flowing));
            }, null);
            trace.Add(Describe(scope.Flowing));
            Sequence(new[]
            {
                "InputActiveFrame", "OwnerCommand", "none", "OwnerCommand", "UiCallback", "OwnerCommand",
                "InputActiveFrame", "InputActiveFrame", "InputActiveFrame", "InputActiveFrame", "none"
            }, trace, name + ": an inner scope wins for its duration and the outer one returns (NestedScope)");

            static string Describe(IntentRecord? record) => record == null ? "none" : record.Source.ToString();
        }

        private static void FailuresRestoreTheScope(bool reuse, string name)
        {
            var scope = new IntentScope(reuse);
            var outer = Record("io.example.builder", IntentSource.InteractionHandler);
            var inner = Record("io.example.builder", IntentSource.UiCallback);
            using (scope.Enter(outer))
            {
                Throws<InvalidOperationException>(() => scope.Run(inner, _ => throw new InvalidOperationException("broken handler"), null), name + ": the failure reaches the caller");
                True(ReferenceEquals(scope.Flowing, outer), name + ": and the outer intent flows again after it");
                try
                {
                    using (scope.Enter(inner)) throw new InvalidOperationException("broken");
                }
                catch (InvalidOperationException)
                {
                }

                True(ReferenceEquals(scope.Flowing, outer), name + ": an entered scope that fails restores too");
            }

            True(scope.Flowing == null, name + ": and nothing is left behind");
            Throws<ArgumentNullException>(() => scope.Run(inner, null!, null), name + ": a scope runs a callback");
        }

        private static void OtherThreadsOpenNothing(bool reuse, string name)
        {
            var scope = new IntentScope(reuse);
            var record = Record("io.example.builder");
            var outcome = Task.Run(() =>
            {
                IntentRecord? seen = record;
                scope.Run(record, _ => seen = scope.Flowing, null);
                var token = scope.Enter(record);
                var flowing = scope.Flowing;
                token.Dispose();
                return (seen, token.Restore, flowing, scope.IsOwnerThread);
            }).GetAwaiter().GetResult();
            True(outcome.seen == null && outcome.Restore == null && outcome.flowing == null && !outcome.IsOwnerThread,
                name + ": a worker thread runs its callback as it is, and enters nothing");
            True(scope.Flowing == null, name + ": and the main thread is unchanged");
        }

        private static void TheSteadyStateAllocatesNothing()
        {
            var reusing = new IntentScope(reuseContexts: true);
            var record = Record("io.example.builder", IntentSource.InputActiveFrame);
            var reused = Measure(reusing, record);
            True(reused == 0, "running one record again and again reuses its context and allocates nothing (" + reused + " bytes over 10,000 runs)");
            var setting = new IntentScope(reuseContexts: false);
            var set = Measure(setting, record);
            True(set > 0, "setting and restoring the record, as Mono must, allocates the runtime's async-local map each time");
            Console.WriteLine("Intent scope cost on .NET: 0 bytes per run reusing contexts; " + (set / 10_000.0) + " bytes per run setting and restoring.");
        }

        private static long Measure(IntentScope scope, IntentRecord record)
        {
            Runs(scope, record, 1_000);
            var fewest = long.MaxValue;
            for (var pass = 0; pass < 3 && fewest != 0; pass++) fewest = Math.Min(fewest, Runs(scope, record, 10_000));
            return fewest;
        }

        private static readonly ContextCallback Noop = _ => { };

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static long Runs(IntentScope scope, IntentRecord record, int count)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < count; index++) scope.Run(record, Noop, null);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }
}
