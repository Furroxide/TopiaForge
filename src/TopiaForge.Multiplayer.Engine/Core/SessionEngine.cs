using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>
    /// One process's view of one multiplayer session line: registrations, canonical or predicted values, objects,
    /// participants and the protocol to its peers. Every member runs on the thread that created the engine; see
    /// docs/internal/MultiplayerSessionEngine.md for the rules it implements.
    /// </summary>
    internal sealed partial class SessionEngine
    {
        internal const string ProtocolVersion = MultiplayerModule.ProtocolVersion;

        private readonly EngineOptions options;
        private readonly int ownerThreadId;
        private long originMicros;
        private readonly TransactionScope scope = new TransactionScope();
        private readonly ConcurrentQueue<Action> inbox = new ConcurrentQueue<Action>();
        private readonly List<Action<MultiplayerSessionSnapshot>> sessionSubscribers = new List<Action<MultiplayerSessionSnapshot>>();
        private readonly List<Action<MultiplayerTickContext>> tickSubscribers = new List<Action<MultiplayerTickContext>>();
        private readonly Queue<ObjectChangeEvent> objectNotifications = new Queue<ObjectChangeEvent>();
        private readonly Queue<StateRecord> changedStates = new Queue<StateRecord>();
        private readonly Queue<PresentationDelivery> presentationDeliveries = new Queue<PresentationDelivery>();
        private readonly Queue<Action> completions = new Queue<Action>();
        private Action<MultiplayerSessionSnapshot>[] sessionSubscriberArray = Array.Empty<Action<MultiplayerSessionSnapshot>>();
        private Action<MultiplayerTickContext>[] tickSubscriberArray = Array.Empty<Action<MultiplayerTickContext>>();
        private CancellationTokenSource sessionCancellation = new CancellationTokenSource();
        private MultiplayerSessionId sessionId;
        private SessionSeed seed;
        private ulong epoch;
        private MultiplayerSessionState state;
        private MultiplayerSessionEndReason endReason;
        private ParticipantId? localParticipantId;
        private ParticipantId? hostParticipantId;
        private ulong tick;
        private long nowMicros;
        private bool ended;
        private bool settling;
        private bool pumping;
        private MultiplayerSessionSnapshot? snapshot;
        private volatile MultiplayerSessionSnapshot? publishedSnapshot;
        private bool snapshotDirty = true;
        private bool sessionNotificationPending;
        private IReadOnlyList<MultiplayerParticipant>? participantView;

        private SessionEngine(
            EngineOptions options,
            EngineRole role,
            MultiplayerProcessKind processKind,
            MultiplayerSessionId sessionId,
            SessionSeed seed,
            MultiplayerSessionState state,
            long startMicros)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            options.Validate();
            if (!Enum.IsDefined(typeof(MultiplayerProcessKind), processKind)) throw new ArgumentOutOfRangeException(nameof(processKind));
            if (role == EngineRole.Client && processKind != MultiplayerProcessKind.Interactive)
            {
                throw new ArgumentException("A client process must be interactive.", nameof(processKind));
            }

            Role = role;
            ProcessKind = processKind;
            this.sessionId = sessionId;
            this.seed = seed;
            this.state = state;
            ownerThreadId = Thread.CurrentThread.ManagedThreadId;
            originMicros = startMicros;
            nowMicros = startMicros;
            Clock = new FixedStepClock(options.StepRateHz);
        }

        /// <summary>Gets the engine's role; a process moves between solo hosting and joining, see <c>SessionEngine.Roles.cs</c>.</summary>
        internal EngineRole Role { get; private set; }

        internal MultiplayerProcessKind ProcessKind { get; }

        internal FixedStepClock Clock { get; }

        internal EngineOptions Options => options;

        internal bool IsEnded => ended;

        internal bool HasServerSide => Role == EngineRole.Host;

        internal bool HasPresentation => ProcessKind == MultiplayerProcessKind.Interactive;

        internal NetworkTick CurrentTick => new NetworkTick(tick);

        internal SessionSeed Seed => seed;

        internal long NowMicros => nowMicros;

        internal CancellationToken CurrentSessionToken => sessionCancellation.Token;

        internal ParticipantId? LocalParticipantId => localParticipantId;

        internal bool IsOwnerThread => Thread.CurrentThread.ManagedThreadId == ownerThreadId;

        /// <summary>
        /// Gets the latest session snapshot. The owner thread rebuilds it lazily; other threads see the last snapshot
        /// the owner thread built.
        /// </summary>
        internal MultiplayerSessionSnapshot Snapshot
        {
            get
            {
                if (!IsOwnerThread) return publishedSnapshot ?? throw new InvalidOperationException("The session snapshot is not ready yet.");
                if (snapshot == null || snapshotDirty)
                {
                    snapshot = BuildSnapshot();
                    publishedSnapshot = snapshot;
                    snapshotDirty = false;
                }

                return snapshot;
            }
        }

        /// <summary>Creates an engine that holds world authority: solo, a listen host, or the rig's dedicated server.</summary>
        internal static SessionEngine CreateHost(
            EngineOptions options,
            MultiplayerProcessKind processKind,
            MultiplayerSessionId sessionId,
            SessionSeed seed,
            ParticipantId? hostParticipant,
            string hostDisplayName,
            long startMicros)
        {
            if (hostParticipant.HasValue && !ProtocolLimits.IsValidIdentity(hostDisplayName))
            {
                throw new ArgumentException("The host player needs a bounded display name.", nameof(hostDisplayName));
            }

            var engine = new SessionEngine(
                options,
                EngineRole.Host,
                processKind,
                sessionId,
                seed,
                MultiplayerSessionState.Ready,
                startMicros);
            engine.InitializeHost(hostParticipant, hostDisplayName);
            return engine;
        }

        /// <summary>Creates an interactive client that joins a host when given a link.</summary>
        internal static SessionEngine CreateClient(EngineOptions options, MultiplayerSessionId placeholderSessionId, long startMicros) =>
            new SessionEngine(
                options,
                EngineRole.Client,
                MultiplayerProcessKind.Interactive,
                placeholderSessionId,
                default,
                MultiplayerSessionState.Connecting,
                startMicros);

        /// <summary>
        /// Creates an owner-scoped facade; every facade of an engine shares its snapshot and clock. A facade bound to a
        /// mod lifetime closes when that mod stops.
        /// </summary>
        internal EngineSessionFacade CreateFacade(string ownerModId, IModLifetime? lifetime = null)
        {
            if (!ProtocolLimits.IsValidIdentity(ownerModId)) throw new ArgumentException("An owner mod id is required.", nameof(ownerModId));
            return new EngineSessionFacade(this, ownerModId, lifetime);
        }

        /// <summary>Advances the engine to <paramref name="now"/>: link traffic, fixed steps, outbound flush, callbacks.</summary>
        internal void Pump(long now)
        {
            RequireOwnerThread();
            if (scope.IsOpen || settling || pumping)
            {
                throw new InvalidOperationException("The engine cannot be pumped from inside a transaction, a callback or another pump.");
            }

            pumping = true;
            try
            {
                if (now > nowMicros) nowMicros = now;
                DrainInbox();
                if (!ended)
                {
                    if (Role == EngineRole.Host) PumpHost();
                    else PumpClient();
                }
            }
            finally
            {
                pumping = false;
            }

            Settle();
        }

        /// <summary>Gets whether the engine holds work a pump would act on: unsent frames or undelivered callbacks.</summary>
        internal bool HasPendingWork =>
            !inbox.IsEmpty ||
            presentationDeliveries.Count > 0 ||
            (Role == EngineRole.Host ? HostHasPendingWork : ClientHasPendingWork);

        /// <summary>Ends this process's participation: a host ends the session for everyone, a client leaves.</summary>
        internal void Shutdown(MultiplayerSessionEndReason reason)
        {
            RequireOwnerThread();
            if (scope.IsOpen) throw new InvalidOperationException("The session cannot end inside a transaction.");
            if (ended) return;
            if (Role == EngineRole.Host) ShutdownHost(reason);
            else ShutdownClient();
            EnterEnded(reason);
            Settle();
        }

        internal void RequireOwnerThread()
        {
            if (!IsOwnerThread) throw new InvalidOperationException("Multiplayer session calls must run on the thread that owns the session.");
        }

        /// <summary>Runs <paramref name="action"/> now on the owner thread, or at the next settle point from any other.</summary>
        internal void Post(Action action)
        {
            if (IsOwnerThread)
            {
                action();
                return;
            }

            inbox.Enqueue(action);
        }

        internal void InvokeCallback<T>(Action<T> callback, T argument, string what)
        {
            try
            {
                callback(argument);
            }
            catch (Exception exception) when (options.CallbackFailures == EngineCallbackFailureMode.Report)
            {
                Report(new EngineDiagnostic(EngineDiagnosticKind.CallbackFailed, "A " + what + " threw.", null, exception));
            }
        }

        internal void ReportCodecFailure(string what, string message) =>
            Report(new EngineDiagnostic(EngineDiagnosticKind.CodecFailed, "Unable to decode " + what + ": " + message));

        internal void Report(EngineDiagnostic diagnostic) => options.Diagnostics?.Report(diagnostic);

        internal IDisposable SubscribeSession(Action<MultiplayerSessionSnapshot> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            RequireOwnerThread();
            if (ended) throw new ObjectDisposedException(nameof(IMultiplayerSession));
            sessionSubscribers.Add(handler);
            sessionSubscriberArray = sessionSubscribers.ToArray();
            return new EngineLease(() => Post(() =>
            {
                if (sessionSubscribers.Remove(handler)) sessionSubscriberArray = sessionSubscribers.ToArray();
            }));
        }

        internal IDisposable SubscribeTick(Action<MultiplayerTickContext> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            RequireOwnerThread();
            if (ended) throw new ObjectDisposedException(nameof(IMultiplayerSession));
            tickSubscribers.Add(handler);
            tickSubscriberArray = tickSubscribers.ToArray();
            return new EngineLease(() => Post(() =>
            {
                if (tickSubscribers.Remove(handler)) tickSubscriberArray = tickSubscribers.ToArray();
            }));
        }

        /// <summary>Ends an outermost operation: drains callbacks unless a transaction or a drain is already running.</summary>
        internal void EndOperation()
        {
            if (!scope.IsOpen && !settling) Settle();
        }

        /// <summary>
        /// Runs <paramref name="action"/> at the next settle point. From the owner thread outside any engine operation,
        /// that is now; inside one, or from another thread, it waits so the operation finishes first.
        /// </summary>
        internal void Defer(Action action)
        {
            inbox.Enqueue(action);
            if (IsOwnerThread && !scope.IsOpen && !settling && !pumping) Settle();
        }

        internal void MarkSessionChanged(bool participantsChanged = false)
        {
            snapshotDirty = true;
            sessionNotificationPending = true;
            if (participantsChanged) participantView = null;
        }

        internal void MarkStateChanged(StateRecord record)
        {
            if (record.NotificationQueued) return;
            record.NotificationQueued = true;
            changedStates.Enqueue(record);
        }

        /// <summary>Queues a canonical object notification carrying the record's confirmed values.</summary>
        internal void QueueObjectChange(ObjectRecord record, ReplicatedObjectChangeKind kind) =>
            objectNotifications.Enqueue(new ObjectChangeEvent(record, kind, record.ConfirmedOwner, record.ConfirmedBytes, record.ConfirmedVersion));

        /// <summary>
        /// Drains queued work in a fixed order: object changes, state changes, the session snapshot, presentation,
        /// confirmations: until nothing is left. Callbacks that call back in queue more work behind the current item.
        /// </summary>
        internal void Settle()
        {
            if (settling) return;
            settling = true;
            try
            {
                while (true)
                {
                    if (DrainInbox()) continue;
                    if (Role == EngineRole.Client && reconcileRequested && !ClientFlushOpen)
                    {
                        reconcileRequested = false;
                        Reconcile();
                        continue;
                    }

                    if (objectNotifications.Count > 0)
                    {
                        DispatchObjectChange(objectNotifications.Dequeue());
                        continue;
                    }

                    if (changedStates.Count > 0)
                    {
                        var record = changedStates.Dequeue();
                        record.NotificationQueued = false;
                        if (!record.IsActive || !record.VisibleValueChanged) continue;
                        record.NotifiedBytes = record.CurrentBytes;
                        record.NotifiedVersion = record.CurrentVersion;
                        record.NotifySubscribers(record.NotifiedBytes);
                        continue;
                    }

                    if (sessionNotificationPending)
                    {
                        sessionNotificationPending = false;
                        NotifySession();
                        continue;
                    }

                    if (presentationDeliveries.Count > 0)
                    {
                        DeliverPresentation(presentationDeliveries.Dequeue());
                        continue;
                    }

                    if (completions.Count > 0)
                    {
                        completions.Dequeue()();
                        continue;
                    }

                    break;
                }
            }
            finally
            {
                settling = false;
            }
        }

        private bool DrainInbox()
        {
            if (inbox.IsEmpty) return false;
            while (inbox.TryDequeue(out var action)) action();
            return true;
        }

        private void NotifySession()
        {
            var subscribers = sessionSubscriberArray;
            if (subscribers.Length == 0) return;
            var current = Snapshot;
            foreach (var subscriber in subscribers)
            {
                if (!sessionSubscribers.Contains(subscriber)) continue;
                InvokeCallback(subscriber, current, "session observer");
            }
        }

        private void DispatchObjectChange(ObjectChangeEvent change)
        {
            if (!objectSubscribers.TryGetValue(change.Record.Type.Key, out var subscribers) || subscribers.Count == 0) return;
            foreach (var subscriber in subscribers.ToArray())
            {
                if (!subscribers.Contains(subscriber)) continue;
                change.Record.DispatchChange(change, subscriber);
            }
        }

        private void DeliverPresentation(PresentationDelivery delivery)
        {
            if (state != MultiplayerSessionState.Ready || !HasPresentation) return;
            if (!presentations.TryGetValue(delivery.Key, out var binding) || !binding.IsActive) return;
            binding.Deliver(delivery.Bytes);
        }

        private MultiplayerSessionSnapshot BuildSnapshot()
        {
            var sides = Role == EngineRole.Client
                ? MultiplayerExecutionSide.Client
                : ProcessKind == MultiplayerProcessKind.Interactive
                    ? MultiplayerExecutionSide.Client | MultiplayerExecutionSide.Server
                    : MultiplayerExecutionSide.Server;
            var participants = participantView ??= BuildParticipantView();
            ParticipantId? local = null;
            ParticipantId? host = null;
            foreach (var participant in participants)
            {
                if (participant.IsLocal) local = participant.Id;
                if (participant.IsHost) host = participant.Id;
            }

            return new MultiplayerSessionSnapshot(
                sessionId,
                state,
                ProcessKind,
                sides,
                local,
                participants,
                new NetworkTick(tick),
                seed,
                host,
                state == MultiplayerSessionState.Ended ? endReason : MultiplayerSessionEndReason.None);
        }

        private IReadOnlyList<MultiplayerParticipant> BuildParticipantView()
        {
            var entries = Role == EngineRole.Host ? HostParticipantEntries() : clientParticipants;
            var view = new List<MultiplayerParticipant>(entries.Count);
            var canBeLocal = ProcessKind == MultiplayerProcessKind.Interactive && localParticipantId.HasValue;
            foreach (var entry in entries)
            {
                var id = new ParticipantId(entry.Id);
                var isLocal = canBeLocal && id.Equals(localParticipantId!.Value);
                var isHost = entry.IsHost && hostParticipantId.HasValue && id.Equals(hostParticipantId.Value);
                view.Add(new MultiplayerParticipant(
                    id,
                    entry.DisplayName,
                    isLocal,
                    entry.IsConnected,
                    isHost,
                    entry.Quality,
                    entry.PingMilliseconds,
                    entry.PlayerKey == null ? default : new MultiplayerPlayerKey(entry.PlayerKey)));
            }

            return view.AsReadOnly();
        }

        private void EnterEnded(MultiplayerSessionEndReason reason)
        {
            ended = true;
            state = MultiplayerSessionState.Ended;
            endReason = reason;
            RetireSessionToken(sessionCancellation);
            MarkSessionChanged();
        }

        /// <summary>
        /// Cancels a retired session token first thing at the next settle point. By then the replacement or end is
        /// complete, so cancellation callbacks observe a coherent session, and they run under the callback policy.
        /// </summary>
        private void RetireSessionToken(CancellationTokenSource retired) => inbox.Enqueue(() =>
        {
            try
            {
                retired.Cancel();
            }
            catch (AggregateException exception) when (options.CallbackFailures == EngineCallbackFailureMode.Report)
            {
                Report(new EngineDiagnostic(EngineDiagnosticKind.CallbackFailed, "A session-token callback threw.", null, exception));
            }
        });

        private readonly struct PresentationDelivery
        {
            internal PresentationDelivery(WireKey key, byte[] bytes)
            {
                Key = key;
                Bytes = bytes;
            }

            internal WireKey Key { get; }
            internal byte[] Bytes { get; }
        }
    }

    /// <summary>Maps fixed steps to engine microseconds without accumulating rounding error at rates like 60 Hz.</summary>
    internal sealed class FixedStepClock
    {
        internal FixedStepClock(int rateHz)
        {
            if (rateHz < 1) throw new ArgumentOutOfRangeException(nameof(rateHz));
            RateHz = rateHz;
            StepSeconds = 1.0 / rateHz;
        }

        internal int RateHz { get; }

        internal double StepSeconds { get; }

        /// <summary>Gets the earliest elapsed time at which step <paramref name="step"/> is due.</summary>
        internal long StepStartMicros(ulong step) => (long)(((step * 1_000_000UL) + (ulong)RateHz - 1) / (ulong)RateHz);

        /// <summary>Gets the number of whole steps that fit in <paramref name="elapsedMicros"/>.</summary>
        internal ulong StepsIn(long elapsedMicros)
        {
            if (elapsedMicros <= 0) return 0;
            var seconds = (ulong)(elapsedMicros / 1_000_000);
            var remainder = (ulong)(elapsedMicros % 1_000_000);
            return (seconds * (ulong)RateHz) + (remainder * (ulong)RateHz / 1_000_000);
        }
    }
}
