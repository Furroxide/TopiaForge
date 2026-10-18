using System;
using System.Threading;

namespace TopiaForge.ModManager.Core.Multiplayer
{
    /// <summary>
    /// One recorded player intent: what caused it, the mod whose code acts on it, whose intent it is, and the realm epoch
    /// it was recorded in. It names a player by participant id only, never by an address. Immutable, apart from a cache
    /// the scope keeps on it; the runtime reuses one record per mod and source for an epoch, so opening a scope for it
    /// again allocates nothing.
    /// </summary>
    internal sealed class IntentRecord
    {
        /// <param name="source">What caused it; never <see cref="IntentSource.None"/>, which is the absence of a record.</param>
        /// <param name="modId">The mod whose code acts on it: only that mod's code observes it.</param>
        /// <param name="participantId">
        /// Null for this process's own player; otherwise the id the host gave the participant it acts for (WS3.7), 1 to 128
        /// printable characters.
        /// </param>
        /// <param name="epoch">The realm epoch it belongs to; once the realm changes it is never observed again.</param>
        internal IntentRecord(IntentSource source, string modId, string? participantId, long epoch)
        {
            if (!IntentRules.IsPlayerIntent(source)) throw new ArgumentOutOfRangeException(nameof(source), "A record is player intent; scripted code has none.");
            if (string.IsNullOrEmpty(modId)) throw new ArgumentException("A record names the mod whose code acts on it.", nameof(modId));
            if (participantId != null && !IntentFlowRules.IsParticipantId(participantId))
            {
                throw new ArgumentException("A participant id is 1 to 128 printable characters.", nameof(participantId));
            }

            Source = source;
            ModId = modId;
            ParticipantId = participantId;
            Epoch = epoch;
        }

        /// <summary>Gets what caused the intent.</summary>
        internal IntentSource Source { get; }

        /// <summary>Gets the mod whose code acts on it.</summary>
        internal string ModId { get; }

        /// <summary>Gets the participant it acts for, or null for this process's own player.</summary>
        internal string? ParticipantId { get; }

        /// <summary>Gets whether this process's own player is the one acting.</summary>
        internal bool IsLocalPlayer => ParticipantId == null;

        /// <summary>Gets the realm epoch the record belongs to.</summary>
        internal long Epoch { get; }

        // The scope's cache: the execution context that runs this record over an outer one (main thread only).
        internal ExecutionContext? CachedOuter { get; set; }

        internal ExecutionContext? CachedInner { get; set; }

        /// <summary>Names the source and whose intent it is, never a participant's id: logs must not carry one.</summary>
        public override string ToString() => Source + (IsLocalPlayer ? " of the local player" : " of a participant");
    }

    /// <summary>
    /// The intent scope (WS3.4): an <see cref="AsyncLocal{T}"/> record of the player intent the current code acts on. It
    /// flows with the execution context into every continuation that code starts, and is observed only on the thread that
    /// made the scope, the game's main thread: every other thread reads none. Opening a scope always restores what was
    /// there before once its code returns, so a scope never outlives the callback it wraps. The rules for who observes what
    /// are in <see cref="IntentFlowRules"/>.
    /// </summary>
    /// <remarks>
    /// Running a record allocates nothing in the steady state where the runtime can run one captured execution context more
    /// than once (.NET): the scope keeps, on each record, the context that is the caller's plus the record, and runs that.
    /// Where a captured context runs once only (the .NET Framework model, which the game's Mono follows), it sets the
    /// record, runs, and restores, and each of those changes copies the runtime's small dictionary of async-local values.
    /// </remarks>
    internal sealed class IntentScope
    {
        private static readonly bool RuntimeReusesContexts = ProbeReusableContexts();
        private static readonly object RestoreNone = new object();
        private static readonly ContextCallback Build = BuildInner;
        private readonly AsyncLocal<IntentRecord?> current = new AsyncLocal<IntentRecord?>();
        private readonly int ownerThread;

        /// <summary>Creates a scope owned by the calling thread, using reusable contexts where the runtime has them.</summary>
        internal IntentScope()
            : this(reuseContexts: true)
        {
        }

        /// <param name="reuseContexts">False runs every record by setting and restoring it, as the game's Mono must.</param>
        internal IntentScope(bool reuseContexts)
        {
            ReusesContexts = reuseContexts && RuntimeReusesContexts;
            ownerThread = Environment.CurrentManagedThreadId;
        }

        /// <summary>Gets whether this runtime can run one captured execution context more than once.</summary>
        internal static bool RuntimeCanReuseContexts => RuntimeReusesContexts;

        /// <summary>Gets whether running a record reuses a cached execution context rather than setting and restoring it.</summary>
        internal bool ReusesContexts { get; }

        /// <summary>Gets whether the calling thread is the one the scope belongs to.</summary>
        internal bool IsOwnerThread => Environment.CurrentManagedThreadId == ownerThread;

        /// <summary>
        /// Gets the record flowing to the calling code, or null outside every scope and on any other thread than the owner.
        /// It does not check the reader's mod or the epoch; <see cref="IntentFlowRules.Observe"/> does.
        /// </summary>
        internal IntentRecord? Flowing => IsOwnerThread ? current.Value : null;

        /// <summary>
        /// Gets the record the execution context carries to the calling code on whichever thread it runs, for work handed
        /// back to the main thread (<see cref="IntentFlow.WorkerHandBack"/>): a worker the intent's own code started never
        /// observes it, but what it schedules through the SDK scheduler captures it. Nothing else reads it.
        /// </summary>
        internal IntentRecord? FlowingOnAnyThread => current.Value;

        /// <summary>
        /// Runs <paramref name="callback"/> with <paramref name="record"/> flowing to it and to every continuation it
        /// starts, then restores what flowed before. Null runs it with no intent. Off the owner thread it runs as it is.
        /// </summary>
        internal void Run(IntentRecord? record, ContextCallback callback, object? state)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            if (!IsOwnerThread)
            {
                callback(state);
                return;
            }

            var flowing = current.Value;
            if (ReferenceEquals(flowing, record))
            {
                callback(state);
                return;
            }

            if (record != null && ReusesContexts)
            {
                var outer = ExecutionContext.Capture();
                if (outer != null)
                {
                    ExecutionContext.Run(InnerFor(record, outer), callback, state);
                    return;
                }
            }

            current.Value = record;
            try
            {
                callback(state);
            }
            finally
            {
                current.Value = flowing;
            }
        }

        /// <summary>
        /// Makes <paramref name="record"/> flow to the code that follows, on the owner thread, until the token is disposed;
        /// null makes no intent flow. Tokens are disposed in the reverse order they were taken, on the same thread. Off the
        /// owner thread, or when the record already flows, nothing changes and the token restores nothing.
        /// </summary>
        internal Token Enter(IntentRecord? record)
        {
            if (!IsOwnerThread) return default;
            var flowing = current.Value;
            if (ReferenceEquals(flowing, record)) return default;
            current.Value = record;
            return new Token(this, (object?)flowing ?? RestoreNone);
        }

        /// <summary>
        /// Restores what a token's <see cref="Token.Restore"/> names: the kit's hook carries it as an object, so it never
        /// boxes a token. Null restores nothing.
        /// </summary>
        internal void Restore(object? restore)
        {
            if (restore == null || !IsOwnerThread) return;
            current.Value = ReferenceEquals(restore, RestoreNone) ? null : (IntentRecord)restore;
        }

        private ExecutionContext InnerFor(IntentRecord record, ExecutionContext outer)
        {
            if (ReferenceEquals(record.CachedOuter, outer) && record.CachedInner != null) return record.CachedInner;
            var builder = new InnerBuilder(current, record);
            ExecutionContext.Run(outer, Build, builder);
            var inner = builder.Inner ?? throw new InvalidOperationException("The intent scope could not capture its context.");
            record.CachedOuter = outer;
            record.CachedInner = inner;
            return inner;
        }

        private static void BuildInner(object? state)
        {
            var builder = (InnerBuilder)state!;
            builder.Local.Value = builder.Record;
            builder.Inner = ExecutionContext.Capture();
        }

        /// <summary>
        /// Whether a captured execution context can run twice. .NET allows it; the .NET Framework model, which Mono follows,
        /// refuses a context that has already run.
        /// </summary>
        private static bool ProbeReusableContexts()
        {
            try
            {
                var probe = new AsyncLocal<object?>();
                var marker = new object();
                ExecutionContext? captured = null;
                var outer = ExecutionContext.Capture();
                if (outer == null) return false;
                ExecutionContext.Run(outer, _ =>
                {
                    probe.Value = marker;
                    captured = ExecutionContext.Capture();
                }, null);
                if (captured == null) return false;
                var seen = 0;
                ContextCallback count = _ => { if (ReferenceEquals(probe.Value, marker)) seen++; };
                ExecutionContext.Run(captured, count, null);
                ExecutionContext.Run(captured, count, null);
                return seen == 2;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>What a scope entered; disposing it restores what flowed before. The default token restores nothing.</summary>
        internal readonly struct Token : IDisposable
        {
            private readonly IntentScope? scope;

            internal Token(IntentScope scope, object restore)
            {
                this.scope = scope;
                Restore = restore;
            }

            /// <summary>Gets what disposing restores, for the kit's hook to carry; null when nothing changed.</summary>
            internal object? Restore { get; }

            public void Dispose() => scope?.Restore(Restore);
        }

        private sealed class InnerBuilder
        {
            internal InnerBuilder(AsyncLocal<IntentRecord?> local, IntentRecord record)
            {
                Local = local;
                Record = record;
            }

            internal AsyncLocal<IntentRecord?> Local { get; }

            internal IntentRecord Record { get; }

            internal ExecutionContext? Inner { get; set; }
        }
    }
}
