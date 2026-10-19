using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using static TopiaForge.ModManager.Tests.MultiplayerModelAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The intent scope's mechanics in Core (WS3.4), with each of its ways to run a record: reusing a cached execution
    /// context, as .NET can; the logical call context, as the game's Mono runs it, emulated here since .NET has none; and
    /// setting and restoring an async-local, the fallback. A scope ends with its callback; awaits carry it; worker threads
    /// never observe it while an await that returns to the main thread does; nested scopes replace and restore; failures
    /// restore; the probe that trusts a runtime's logical call context refuses one that does not keep captured values; and
    /// the steady state allocates nothing in either way that should.
    /// </summary>
    internal static class MultiplayerIntentScopeTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
        private static readonly ContextCallback Noop = _ => { };

        internal static void Run()
        {
            True(IntentScope.RuntimeCanReuseContexts, ".NET runs one captured execution context more than once");
            True(!IntentScope.RuntimeHasLogicalCallContext && IntentLogicalStore.TryBind() == null, ".NET has no logical call context");
            Equal(IntentScopeMode.ReusedContext, IntentScope.RuntimeMode, ".NET scopes reuse contexts");
            Equal(IntentScopeMode.ReusedContext, new IntentScope().Mode, "and a scope made without a mode does too");
            TheModeFollowsTheRuntime();
            foreach (var (name, make) in Ways())
            {
                AScopeEndsWithItsCallback(make, name);
                AwaitsKeepTheIntent(make, name);
                WorkerThreadsNeverObserveIt(make, name);
                NestedScopesReplaceAndRestore(make, name);
                FailuresRestoreTheScope(make, name);
                OtherThreadsOpenNothing(make, name);
            }

            TheLogicalCallContextIsLeftEmptyAtRest();
            OnlyALogicalCallContextThatKeepsCapturesIsTrusted();
            TheStoreBindsCorlibsPublicMembersByName();
            TheSteadyStateAllocatesNothing();
            Console.WriteLine("MultiplayerIntentScopeTests passed.");
        }

        internal static IntentRecord Record(string modId, IntentSource source = IntentSource.UiCallback, long epoch = 1) =>
            new IntentRecord(source, modId, null, epoch);

        private static IEnumerable<(string Name, Func<IntentScope> Make)> Ways()
        {
            yield return ("reusing contexts", () => new IntentScope(IntentScopeMode.ReusedContext));
            yield return ("the logical call context", () => new IntentScope(IntentScopeMode.LogicalCallContext, new EmulatedLogicalCallContext().Store));
            yield return ("setting and restoring", () => new IntentScope(IntentScopeMode.AsyncLocal));
        }

        private static void TheModeFollowsTheRuntime()
        {
            Equal(IntentScopeMode.ReusedContext, IntentScope.ModeFor(reusesContexts: true, hasLogicalCallContext: true), "a runtime that reuses contexts reuses them");
            Equal(IntentScopeMode.LogicalCallContext, IntentScope.ModeFor(reusesContexts: false, hasLogicalCallContext: true),
                "one that runs a captured context once, as the game's Mono does, keeps the record in its logical call context");
            Equal(IntentScopeMode.AsyncLocal, IntentScope.ModeFor(reusesContexts: false, hasLogicalCallContext: false), "and one with neither sets and restores");
            Throws<NotSupportedException>(() => new IntentScope(IntentScopeMode.LogicalCallContext), "a way the runtime lacks is refused, not faked");
            Throws<ArgumentOutOfRangeException>(() => new IntentScope((IntentScopeMode)3), "an undefined way is refused");
            var scope = new IntentScope(IntentScopeMode.LogicalCallContext, new EmulatedLogicalCallContext().Store);
            Equal(IntentScopeMode.LogicalCallContext, scope.Mode, "a scope given a store keeps its record there");
        }

        private static void AScopeEndsWithItsCallback(Func<IntentScope> make, string name)
        {
            var scope = make();
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

        private static void AwaitsKeepTheIntent(Func<IntentScope> make, string name)
        {
            var scope = make();
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

        private static void WorkerThreadsNeverObserveIt(Func<IntentScope> make, string name)
        {
            var scope = make();
            var record = Record("io.example.builder");
            using var pump = new MainThreadPump();
            Task<(IntentRecord? Flowing, IntentRecord? Carried)>? onWorker = null;
            Task<(IntentRecord? Worker, IntentRecord? Back)>? roundTrip = null;
            scope.Run(record, _ =>
            {
                onWorker = Task.Run(() => (scope.Flowing, scope.FlowingOnAnyThread));
                roundTrip = AwaitWorker();
            }, null);
            pump.RunUntil(() => onWorker!.IsCompleted && roundTrip!.IsCompleted, Timeout);
            True(onWorker!.Result.Flowing == null, name + ": Task.Run's thread observes no intent (WorkerThread)");
            True(ReferenceEquals(onWorker.Result.Carried, record), name + ": though its context carries it, for what it hands back (WorkerHandBack)");
            var (worker, back) = roundTrip!.Result;
            True(worker == null, name + ": nor does the work an await hands it");
            True(ReferenceEquals(back, record), name + ": and the continuation back on the main thread acts on it again");
            True(Task.Run(() => scope.FlowingOnAnyThread).GetAwaiter().GetResult() == null, name + ": a worker started at rest carries none");

            async Task<(IntentRecord?, IntentRecord?)> AwaitWorker()
            {
                var seen = await Task.Run(() => scope.Flowing);
                return (seen, scope.Flowing);
            }
        }

        private static void NestedScopesReplaceAndRestore(Func<IntentScope> make, string name)
        {
            var scope = make();
            var outer = Record("io.example.builder", IntentSource.InputActiveFrame);
            var inner = Record("io.example.builder", IntentSource.OwnerCommand);
            var other = Record("io.example.neighbour", IntentSource.UiCallback);
            var trace = new List<string>();
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

        private static void FailuresRestoreTheScope(Func<IntentScope> make, string name)
        {
            var scope = make();
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

        private static void OtherThreadsOpenNothing(Func<IntentScope> make, string name)
        {
            var scope = make();
            var record = Record("io.example.builder");
            var outcome = Task.Run(() =>
            {
                IntentRecord? seen = record;
                scope.Run(record, _ => seen = scope.Flowing, null);
                var token = scope.Enter(record);
                var flowing = scope.FlowingOnAnyThread;
                token.Dispose();
                return (seen, token.Restore, flowing, scope.IsOwnerThread);
            }).GetAwaiter().GetResult();
            True(outcome.seen == null && outcome.Restore == null && outcome.flowing == null && !outcome.IsOwnerThread,
                name + ": a worker thread runs its callback as it is, and enters nothing");
            True(scope.Flowing == null, name + ": and the main thread is unchanged");
        }

        private static void TheLogicalCallContextIsLeftEmptyAtRest()
        {
            var context = new EmulatedLogicalCallContext();
            var scope = new IntentScope(IntentScopeMode.LogicalCallContext, context.Store);
            var outer = Record("io.example.builder", IntentSource.InputActiveFrame);
            scope.Run(outer, _ =>
            {
                True(context.Slots == 1, "a scope keeps its record in one slot of the logical call context");
                using (scope.Enter(Record("io.example.builder", IntentSource.OwnerCommand))) True(context.Slots == 1, "and a nested one reuses that slot");
                scope.Run(null, __ => True(context.Slots == 0, "a scope that runs without intent empties the slot"), null);
            }, null);
            True(context.Slots == 0, "a scope that ends removes its slot rather than setting it to null, so a capture at rest copies nothing");
            var token = scope.Enter(outer);
            token.Dispose();
            True(context.Slots == 0, "and so does a token disposed back to none");
        }

        private static void OnlyALogicalCallContextThatKeepsCapturesIsTrusted()
        {
            True(IntentLogicalStore.KeepsCapturedValues(new EmulatedLogicalCallContext().Store), "a context that copies what captures held is trusted");
            var shared = new SharedSlots();
            True(!IntentLogicalStore.KeepsCapturedValues(shared.Store), "one whose captures see later changes is not: an await would lose the record or see a later one");
            True(shared.Count == 0, "and the check leaves the slot it used empty");
            var stuck = new EmulatedLogicalCallContext(ignoreFree: true);
            True(!IntentLogicalStore.KeepsCapturedValues(stuck.Store), "nor is one that cannot empty a slot in place");
            True(!IntentLogicalStore.KeepsCapturedValues(new IntentLogicalStore(_ => null, (_, _) => { }, _ => { })), "nor one that loses what is set");
            True(!TrustedApart(new EmptiedInPlace().Store),
                "nor one that empties a slot in place under the captures taken before, where an await would lose the record as its scope ends");
            True(!TrustedApart(new EscapingWrites().Store),
                "nor one where a value set while a captured context runs escapes into the code that ran it");
            True(!TrustedApart(new EmptiedLater().Store),
                "nor one that empties a slot only when the thread next changes context, where an await right after a scope ends would carry its record on");

            // Each of the next two passes every check but one, so each of the probe's emptied-slot checks is needed on .NET too.
            True(!TrustedApart(new StaleUntilSwitch().Store),
                "nor one whose thread reads a slot it emptied until it next changes context, where code right after a scope ends would act on its record");
            True(!TrustedApart(new ComesBack().Store),
                "nor one that hands an emptied slot's value back once the thread has changed context, where a scope's record would return in a later frame");
            True(!IntentLogicalStore.KeepsCapturedValues(new IntentLogicalStore(_ => throw new NotSupportedException("refused"), (_, _) => { }, _ => { })),
                "nor one that refuses access, and the check never throws");
            Throws<ArgumentNullException>(() => IntentLogicalStore.KeepsCapturedValues(null!), "the check needs a store");
        }

        /// <summary>Checks a store in a context of its own, so whatever a broken store leaves set never reaches the harness's thread.</summary>
        private static bool TrustedApart(IntentLogicalStore store)
        {
            var trusted = false;
            ExecutionContext.Run(ExecutionContext.Capture()!, _ => trusted = IntentLogicalStore.KeepsCapturedValues(store), null);
            return trusted;
        }

        /// <summary>
        /// The game's Mono offers its logical call context through three public static members of corlib's
        /// <c>System.Runtime.Remoting.Messaging.CallContext</c>, which .NET lacks, so the binding is checked here against a
        /// stand-in with the same names and shapes: a renamed or reshaped member would otherwise only show on Mono.
        /// </summary>
        private static void TheStoreBindsCorlibsPublicMembersByName()
        {
            var store = IntentLogicalStore.TryBind(typeof(StandInCallContext));
            True(store != null, "a type with CallContext's public LogicalGetData, LogicalSetData and FreeNamedDataSlot binds, by name");
            var scope = new IntentScope(IntentScopeMode.LogicalCallContext, store);
            var record = Record("io.example.builder");
            IntentRecord? seen = null;
            scope.Run(record, _ => seen = scope.Flowing, null);
            True(ReferenceEquals(seen, record) && scope.Flowing == null, "and a scope runs its record through what it bound");
            True(IntentLogicalStore.TryBind(typeof(HiddenCallContext)) == null, "members that are not public are never bound");
            True(IntentLogicalStore.TryBind(typeof(object)) == null && IntentLogicalStore.TryBind(null) == null, "a type without them binds nothing");
        }

        private static void TheSteadyStateAllocatesNothing()
        {
            var record = Record("io.example.builder", IntentSource.InputActiveFrame);
            var reused = Measure(new IntentScope(IntentScopeMode.ReusedContext), record);
            True(reused == 0, "running one record again and again reuses its context and allocates nothing (" + reused + " bytes over 10,000 runs)");
            var inPlace = new InPlaceSlot();
            var logical = Measure(new IntentScope(IntentScopeMode.LogicalCallContext, inPlace.Store), record);
            True(logical == 0 && inPlace.Value == null,
                "over a logical call context that changes in place, as Mono's does, the scope itself allocates nothing (" + logical + " bytes over 10,000 runs)");
            var set = Measure(new IntentScope(IntentScopeMode.AsyncLocal), record);
            True(set > 0, "setting and restoring an async-local, the fallback, allocates the runtime's map of them each time");
            Console.WriteLine("Intent scope cost on .NET: 0 bytes per run reusing contexts; " + (logical / 10_000.0) +
                              " over an in-place logical call context; " + (set / 10_000.0) + " setting and restoring an async-local.");
        }

        private static long Measure(IntentScope scope, IntentRecord record)
        {
            Runs(scope, record, 1_000);
            var fewest = long.MaxValue;
            for (var pass = 0; pass < 3 && fewest != 0; pass++) fewest = Math.Min(fewest, Runs(scope, record, 10_000));
            return fewest;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static long Runs(IntentScope scope, IntentRecord record, int count)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < count; index++) scope.Run(record, Noop, null);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        /// <summary>
        /// The logical call context as Mono's corlib keeps it, emulated on .NET, which has none: each execution context holds
        /// its own slots, a capture keeps what they held then, and no change reaches a context captured before it.
        /// </summary>
        internal sealed class EmulatedLogicalCallContext
        {
            private readonly AsyncLocal<Dictionary<string, object>?> slots = new AsyncLocal<Dictionary<string, object>?>();
            private readonly bool ignoreFree;

            internal EmulatedLogicalCallContext(bool ignoreFree = false)
            {
                this.ignoreFree = ignoreFree;
                Store = new IntentLogicalStore(Get, Set, Free);
            }

            internal IntentLogicalStore Store { get; }

            /// <summary>Gets how many slots the calling code's context holds.</summary>
            internal int Slots => slots.Value?.Count ?? 0;

            private object? Get(string slot) => slots.Value != null && slots.Value.TryGetValue(slot, out var value) ? value : null;

            private void Set(string slot, object value)
            {
                var next = slots.Value == null ? new Dictionary<string, object>() : new Dictionary<string, object>(slots.Value);
                next[slot] = value ?? throw new ArgumentNullException(nameof(value));
                slots.Value = next;
            }

            private void Free(string slot)
            {
                if (ignoreFree || slots.Value == null || !slots.Value.ContainsKey(slot)) return;
                var next = new Dictionary<string, object>(slots.Value);
                next.Remove(slot);
                slots.Value = next.Count == 0 ? null : next;
            }
        }

        /// <summary>Corlib's <c>CallContext</c> members, by name and shape, over captured values as Mono keeps them.</summary>
        private static class StandInCallContext
        {
            private static readonly EmulatedLogicalCallContext Context = new EmulatedLogicalCallContext();

            public static object? LogicalGetData(string name) => Context.Store.Get(name);

            public static void LogicalSetData(string name, object data) => Context.Store.Set(name, data);

            public static void FreeNamedDataSlot(string name) => Context.Store.Free(name);
        }

        /// <summary>The same working members, kept internal: the store binds a runtime's public API only.</summary>
        private static class HiddenCallContext
        {
            private static readonly EmulatedLogicalCallContext Context = new EmulatedLogicalCallContext();

            internal static object? LogicalGetData(string name) => Context.Store.Get(name);

            internal static void LogicalSetData(string name, object data) => Context.Store.Set(name, data);

            internal static void FreeNamedDataSlot(string name) => Context.Store.Free(name);
        }

        /// <summary>
        /// Copies its slots when one is set but empties one in place, so a context captured before the slot was emptied
        /// loses its value, as a runtime whose captures share the slots they were taken with would.
        /// </summary>
        private sealed class EmptiedInPlace
        {
            private readonly AsyncLocal<Dictionary<string, object>?> slots = new AsyncLocal<Dictionary<string, object>?>();

            internal EmptiedInPlace()
            {
                Store = new IntentLogicalStore(
                    slot => slots.Value != null && slots.Value.TryGetValue(slot, out var value) ? value : null,
                    (slot, value) => slots.Value = new Dictionary<string, object>(slots.Value ?? new Dictionary<string, object>()) { [slot] = value },
                    slot => slots.Value?.Remove(slot));
            }

            internal IntentLogicalStore Store { get; }
        }

        /// <summary>
        /// Keeps captured values as the logical call context does, but also holds what is set on the thread itself, so a
        /// value set while a captured context runs escapes into the code that ran it.
        /// </summary>
        private sealed class EscapingWrites
        {
            [ThreadStatic]
            private static Dictionary<string, object>? onThread;

            private readonly EmulatedLogicalCallContext flowing = new EmulatedLogicalCallContext();

            internal EscapingWrites()
            {
                Store = new IntentLogicalStore(Get, Set, Free);
            }

            internal IntentLogicalStore Store { get; }

            private static Dictionary<string, object> ThreadSlots => onThread ??= new Dictionary<string, object>();

            private object? Get(string slot) => ThreadSlots.TryGetValue(slot, out var value) ? value : flowing.Store.Get(slot);

            private void Set(string slot, object value)
            {
                flowing.Store.Set(slot, value);
                ThreadSlots[slot] = value;
            }

            private void Free(string slot)
            {
                flowing.Store.Free(slot);
                ThreadSlots.Remove(slot);
            }
        }

        /// <summary>
        /// Empties a slot only once the thread next changes context, so right after a scope ends the slot still holds its
        /// value, and a capture taken then would carry it into a later frame.
        /// </summary>
        private sealed class EmptiedLater
        {
            private readonly AsyncLocal<Dictionary<string, object>?> slots = new AsyncLocal<Dictionary<string, object>?>();
            private readonly AsyncLocal<object?> generation;
            private readonly HashSet<string> pending = new HashSet<string>();
            private bool switched;

            internal EmptiedLater()
            {
                generation = new AsyncLocal<object?>(change => switched |= change.ThreadContextChanged);
                Store = new IntentLogicalStore(Get, Set, slot => pending.Add(slot));
            }

            internal IntentLogicalStore Store { get; }

            private object? Get(string slot)
            {
                Settle();
                return slots.Value != null && slots.Value.TryGetValue(slot, out var value) ? value : null;
            }

            private void Set(string slot, object value)
            {
                Settle();
                slots.Value = new Dictionary<string, object>(slots.Value ?? new Dictionary<string, object>()) { [slot] = value };
                generation.Value = new object();
            }

            private void Settle()
            {
                if (!switched || pending.Count == 0) return;
                switched = false;
                var next = new Dictionary<string, object>(slots.Value ?? new Dictionary<string, object>());
                foreach (var slot in pending) next.Remove(slot);
                pending.Clear();
                slots.Value = next.Count == 0 ? null : next;
            }
        }

        /// <summary>
        /// Empties a slot in its context, so captures keep what they should, but the thread goes on reading the emptied value
        /// until it next changes context. On .NET a capture taken right after is the live context, so only the check that an
        /// emptied slot reads empty refuses it.
        /// </summary>
        private sealed class StaleUntilSwitch
        {
            private readonly AsyncLocal<Dictionary<string, object>?> slots;
            private readonly Dictionary<string, object> stale = new Dictionary<string, object>();

            internal StaleUntilSwitch()
            {
                slots = new AsyncLocal<Dictionary<string, object>?>(change =>
                {
                    if (change.ThreadContextChanged) stale.Clear();
                });
                Store = new IntentLogicalStore(Get, Set, Free);
            }

            internal IntentLogicalStore Store { get; }

            private object? Get(string slot) =>
                stale.TryGetValue(slot, out var held) ? held : slots.Value != null && slots.Value.TryGetValue(slot, out var value) ? value : null;

            private void Set(string slot, object value)
            {
                stale.Remove(slot);
                slots.Value = new Dictionary<string, object>(slots.Value ?? new Dictionary<string, object>()) { [slot] = value };
            }

            private void Free(string slot)
            {
                if (slots.Value == null || !slots.Value.TryGetValue(slot, out var value)) return;
                stale[slot] = value;
                var next = new Dictionary<string, object>(slots.Value);
                next.Remove(slot);
                slots.Value = next.Count == 0 ? null : next;
            }
        }

        /// <summary>
        /// Empties a slot in place, but hands its old value back once the thread has changed context, until something sets
        /// it again. Only the check that a context captured after the slot was emptied holds none refuses it on .NET.
        /// </summary>
        private sealed class ComesBack
        {
            private readonly AsyncLocal<Dictionary<string, object>?> slots;
            private readonly Dictionary<string, object> freed = new Dictionary<string, object>();
            private bool switched;

            internal ComesBack()
            {
                slots = new AsyncLocal<Dictionary<string, object>?>(change => switched |= change.ThreadContextChanged);
                Store = new IntentLogicalStore(Get, Set, Free);
            }

            internal IntentLogicalStore Store { get; }

            private object? Get(string slot) =>
                switched && freed.TryGetValue(slot, out var back) ? back : slots.Value != null && slots.Value.TryGetValue(slot, out var value) ? value : null;

            private void Set(string slot, object value)
            {
                freed.Remove(slot);
                slots.Value = new Dictionary<string, object>(slots.Value ?? new Dictionary<string, object>()) { [slot] = value };
            }

            private void Free(string slot)
            {
                if (slots.Value == null || !slots.Value.TryGetValue(slot, out var value)) return;
                freed[slot] = value;
                var next = new Dictionary<string, object>(slots.Value);
                next.Remove(slot);
                slots.Value = next.Count == 0 ? null : next;
                switched = false;
            }
        }

        /// <summary>Slots every context shares, changed in place: captures see later changes, as no logical call context may.</summary>
        private sealed class SharedSlots
        {
            private readonly Dictionary<string, object> slots = new Dictionary<string, object>();

            internal SharedSlots()
            {
                Store = new IntentLogicalStore(slot => slots.TryGetValue(slot, out var value) ? value : null, (slot, value) => slots[slot] = value, slot => slots.Remove(slot));
            }

            internal IntentLogicalStore Store { get; }

            internal int Count => slots.Count;
        }

        /// <summary>One slot changed in place, allocating nothing: what Mono's logical call context costs the scope on the main thread.</summary>
        private sealed class InPlaceSlot
        {
            internal InPlaceSlot()
            {
                Store = new IntentLogicalStore(_ => Value, (_, value) => Value = value, _ => Value = null);
            }

            internal IntentLogicalStore Store { get; }

            internal object? Value { get; private set; }
        }
    }
}
