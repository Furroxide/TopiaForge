using System;
using System.Collections.Generic;
using System.Linq;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Identifies the physical/logical role represented by a multiplayer test node.</summary>
    public enum MultiplayerTestRole
    {
        /// <summary>One interactive process contains both logical sides without a transport hop.</summary>
        Standalone = 0,

        /// <summary>An interactive process contains only the logical client side.</summary>
        RemoteClient = 1,

        /// <summary>An interactive process contains both the logical client and server sides.</summary>
        ListenServer = 2,

        /// <summary>A headless process contains only the logical server side.</summary>
        DedicatedServer = 3
    }

    /// <summary>Deterministic packet conditions applied by <see cref="MultiplayerTestRig"/>.</summary>
    public sealed class MultiplayerNetworkConditions
    {
        /// <summary>Creates deterministic packet conditions.</summary>
        /// <param name="latencyTicks">Base one-way latency in virtual network ticks.</param>
        /// <param name="dropEvery">Drops the first attempt of every Nth packet; zero disables loss.</param>
        /// <param name="duplicateEvery">Duplicates every Nth packet; zero disables duplication.</param>
        /// <param name="reorderEvery">Delays every Nth packet by one extra tick; zero disables reordering.</param>
        /// <param name="retryDelayTicks">Delay before a deliberately lost reliable packet is retried.</param>
        public MultiplayerNetworkConditions(
            int latencyTicks = 0,
            int dropEvery = 0,
            int duplicateEvery = 0,
            int reorderEvery = 0,
            int retryDelayTicks = 1)
        {
            if (latencyTicks < 0) throw new ArgumentOutOfRangeException(nameof(latencyTicks));
            if (dropEvery < 0) throw new ArgumentOutOfRangeException(nameof(dropEvery));
            if (duplicateEvery < 0) throw new ArgumentOutOfRangeException(nameof(duplicateEvery));
            if (reorderEvery < 0) throw new ArgumentOutOfRangeException(nameof(reorderEvery));
            if (retryDelayTicks < 1) throw new ArgumentOutOfRangeException(nameof(retryDelayTicks));
            LatencyTicks = latencyTicks;
            DropEvery = dropEvery;
            DuplicateEvery = duplicateEvery;
            ReorderEvery = reorderEvery;
            RetryDelayTicks = retryDelayTicks;
        }

        /// <summary>Gets base one-way latency in virtual network ticks.</summary>
        public int LatencyTicks { get; }

        /// <summary>Gets the packet-loss cadence, or zero when disabled.</summary>
        public int DropEvery { get; }

        /// <summary>Gets the packet-duplication cadence, or zero when disabled.</summary>
        public int DuplicateEvery { get; }

        /// <summary>Gets the packet-reordering cadence, or zero when disabled.</summary>
        public int ReorderEvery { get; }

        /// <summary>Gets the reliable retry delay after deliberate packet loss.</summary>
        public int RetryDelayTicks { get; }
    }

    /// <summary>A process-sized participant in a deterministic multiplayer test.</summary>
    public sealed class MultiplayerTestNode
    {
        internal MultiplayerTestNode(MultiplayerTestRole role, MultiplayerTestSession session)
        {
            Role = role;
            SessionImplementation = session;
        }

        /// <summary>Gets the physical/logical test role.</summary>
        public MultiplayerTestRole Role { get; }

        /// <summary>Gets the same transport-neutral session interface exposed to a mod.</summary>
        public IMultiplayerSession Session => SessionImplementation;

        /// <summary>Gets the local participant, or null for a headless dedicated server.</summary>
        public ParticipantId? ParticipantId => Session.Snapshot.LocalParticipantId;

        /// <summary>Gets whether admission and the initial canonical snapshot are complete.</summary>
        public bool IsReady => Session.Snapshot.State == MultiplayerSessionState.Ready;

        internal MultiplayerTestSession SessionImplementation { get; }
    }

    /// <summary>
    /// Runs deterministic standalone, listen-server, and dedicated-server multiplayer simulations without a
    /// live transport. Tests advance virtual ticks explicitly, so latency and packet faults are reproducible.
    /// </summary>
    public sealed class MultiplayerTestRig : IDisposable
    {
        /// <summary>Gets the deterministic number of network ticks in one virtual rate-limit second.</summary>
        public const ulong TicksPerSecond = 60;

        private readonly List<MultiplayerTestNode> nodes = new List<MultiplayerTestNode>();
        private readonly List<ScheduledDelivery> deliveries = new List<ScheduledDelivery>();
        private readonly Dictionary<ParticipantId, ParticipantRecord> participants =
            new Dictionary<ParticipantId, ParticipantRecord>();
        private ulong packetOrdinal;
        private ulong deliveryOrdinal;
        private ulong currentTick;
        private ulong nextObjectId;
        private ulong nextPresentationSequence;
        private bool disposed;

        private MultiplayerTestRig(MultiplayerTestRole serverRole, MultiplayerNetworkConditions conditions)
        {
            Conditions = conditions ?? throw new ArgumentNullException(nameof(conditions));
            var isDedicated = serverRole == MultiplayerTestRole.DedicatedServer;
            ParticipantId? localId = isDedicated ? (ParticipantId?)null : new ParticipantId("host");
            if (localId.HasValue)
            {
                participants.Add(localId.Value, new ParticipantRecord(localId.Value, "Host", true));
            }

            var sides = isDedicated
                ? MultiplayerExecutionSide.Server
                : MultiplayerExecutionSide.Client | MultiplayerExecutionSide.Server;
            var kind = isDedicated ? MultiplayerProcessKind.Headless : MultiplayerProcessKind.Interactive;
            var session = new MultiplayerTestSession(
                this,
                localId,
                kind,
                sides,
                MultiplayerSessionState.Ready,
                ParticipantSnapshot(localId));
            Server = new MultiplayerTestNode(serverRole, session);
            nodes.Add(Server);
            RefreshParticipants();
        }

        /// <summary>Gets the deterministic transport conditions.</summary>
        public MultiplayerNetworkConditions Conditions { get; }

        /// <summary>Gets the process containing the canonical logical server.</summary>
        public MultiplayerTestNode Server { get; }

        /// <summary>Gets all current process-sized nodes in creation order.</summary>
        public IReadOnlyList<MultiplayerTestNode> Nodes => nodes;

        /// <summary>Gets the current canonical virtual network tick.</summary>
        public NetworkTick Tick => new NetworkTick(currentTick);

        /// <summary>Creates an interactive standalone loopback test.</summary>
        public static MultiplayerTestRig CreateStandalone(MultiplayerNetworkConditions? conditions = null) =>
            new MultiplayerTestRig(
                MultiplayerTestRole.Standalone,
                conditions ?? new MultiplayerNetworkConditions());

        /// <summary>Creates an interactive listen server to which remote clients may be added.</summary>
        public static MultiplayerTestRig CreateListenServer(MultiplayerNetworkConditions? conditions = null) =>
            new MultiplayerTestRig(
                MultiplayerTestRole.ListenServer,
                conditions ?? new MultiplayerNetworkConditions());

        /// <summary>Creates a headless dedicated server to which remote clients may be added.</summary>
        public static MultiplayerTestRig CreateDedicatedServer(MultiplayerNetworkConditions? conditions = null) =>
            new MultiplayerTestRig(
                MultiplayerTestRole.DedicatedServer,
                conditions ?? new MultiplayerNetworkConditions());

        /// <summary>
        /// Adds an admitted remote client in <see cref="MultiplayerSessionState.Synchronizing"/>. Its canonical
        /// state/object snapshot is applied, then readiness is announced, when the transport is advanced.
        /// </summary>
        public MultiplayerTestNode AddRemoteClient(string participantId, string? displayName = null)
        {
            ThrowIfDisposed();
            var id = new ParticipantId(participantId);
            if (participants.ContainsKey(id))
            {
                throw new InvalidOperationException("Participant '" + id + "' is already admitted.");
            }

            participants.Add(id, new ParticipantRecord(id, displayName ?? participantId, true));
            var session = new MultiplayerTestSession(
                this,
                id,
                MultiplayerProcessKind.Interactive,
                MultiplayerExecutionSide.Client,
                MultiplayerSessionState.Synchronizing,
                ParticipantSnapshot(id));
            var node = new MultiplayerTestNode(MultiplayerTestRole.RemoteClient, session);
            nodes.Add(node);
            RefreshParticipants();
            SendReliable(session, () => Synchronize(node));
            return node;
        }

        /// <summary>
        /// Deterministically disconnects a remote client. Pending work is cancelled, participant snapshots are
        /// refreshed, and canonical ownership held by that participant is released.
        /// </summary>
        public void Disconnect(MultiplayerTestNode client)
        {
            ValidateRemoteClient(client);
            var participantId = client.SessionImplementation.AssignedLocalParticipantId!.Value;
            var record = participants[participantId];
            if (!record.Connected) return;
            record.Connected = false;
            client.SessionImplementation.DisconnectTransport(ParticipantSnapshot(null));
            ServerSession.ReleaseParticipantOwnership(participantId);
            RefreshParticipants();
        }

        /// <summary>
        /// Re-enters snapshot synchronization for an existing remote client. Pending predicted work is cancelled;
        /// the latest canonical snapshot is installed before the next <c>Ready</c> notification.
        /// </summary>
        public void Reconnect(MultiplayerTestNode client)
        {
            ValidateRemoteClient(client);
            var participantId = client.SessionImplementation.AssignedLocalParticipantId!.Value;
            participants[participantId].Connected = true;
            client.SessionImplementation.BeginSynchronization(ParticipantSnapshot(participantId));
            RefreshParticipants();
            SendReliable(client.SessionImplementation, () => Synchronize(client));
        }

        /// <summary>
        /// Replaces the current simulated match while preserving each node's stable session facade and generated
        /// registrations. Session-scoped state resets to declared defaults and connected clients resynchronize.
        /// </summary>
        public void StartNewSession(string sessionId)
        {
            ThrowIfDisposed();
            var id = new MultiplayerSessionId(sessionId);
            if (id.Equals(Server.Session.Snapshot.Id))
                throw new ArgumentException("The replacement session id must differ from the current session id.", nameof(sessionId));

            deliveries.Clear();
            nextObjectId = 0;
            nextPresentationSequence = 0;
            foreach (var node in nodes)
            {
                var assigned = node.SessionImplementation.AssignedLocalParticipantId;
                var connected = !assigned.HasValue ||
                    participants.TryGetValue(assigned.Value, out var participant) && participant.Connected;
                var local = connected ? assigned : null;
                var state = ReferenceEquals(node, Server)
                    ? MultiplayerSessionState.Ready
                    : connected
                        ? MultiplayerSessionState.Synchronizing
                        : MultiplayerSessionState.Connecting;
                node.SessionImplementation.BeginNewSession(id, local, ParticipantSnapshot(local), state);
            }

            foreach (var node in nodes.Where(item => item.Role == MultiplayerTestRole.RemoteClient && item.IsReady == false))
            {
                var assigned = node.SessionImplementation.AssignedLocalParticipantId;
                if (assigned.HasValue && participants[assigned.Value].Connected)
                {
                    SendReliable(node.SessionImplementation, () => Synchronize(node));
                }
            }
        }

        /// <summary>Advances the virtual transport and canonical clock by an exact number of ticks.</summary>
        public void Advance(int ticks = 1)
        {
            ThrowIfDisposed();
            if (ticks < 1) throw new ArgumentOutOfRangeException(nameof(ticks));
            for (var index = 0; index < ticks; index++)
            {
                currentTick++;
                foreach (var node in nodes) node.SessionImplementation.SetTick(new NetworkTick(currentTick));
                DeliverDuePackets();
            }
        }

        /// <summary>Advances deterministic virtual time, including rate-limit windows, without using wall-clock time.</summary>
        public void AdvanceTime(TimeSpan duration)
        {
            ThrowIfDisposed();
            if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
            var exactTicks = duration.TotalSeconds * TicksPerSecond;
            if (exactTicks > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(duration));
            Advance(Math.Max(1, checked((int)Math.Ceiling(exactTicks))));
        }

        /// <summary>Advances until all currently scheduled reliable traffic settles.</summary>
        public void Flush(int maximumTicks = 256)
        {
            ThrowIfDisposed();
            if (maximumTicks < 1) throw new ArgumentOutOfRangeException(nameof(maximumTicks));
            var count = 0;
            while (deliveries.Count > 0 && count++ < maximumTicks) Advance();
            if (deliveries.Count > 0)
            {
                throw new InvalidOperationException("The multiplayer test transport did not settle within the requested tick budget.");
            }
        }

        /// <summary>Finds a replicated object handle on one node after its spawn/snapshot packet arrives.</summary>
        public IReplicatedObject<TState, TInput> GetObject<TState, TInput>(
            MultiplayerTestNode node,
            NetworkObjectId id)
            where TState : class
            where TInput : class
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            ThrowIfDisposed();
            if (!nodes.Contains(node)) throw new ArgumentException("The node does not belong to this rig.", nameof(node));
            return node.SessionImplementation.GetObject<TState, TInput>(id);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            deliveries.Clear();
            foreach (var node in nodes) node.SessionImplementation.End();
            nodes.Clear();
            participants.Clear();
        }

        internal void SendReliable(Action delivery)
        {
            if (delivery == null) throw new ArgumentNullException(nameof(delivery));
            packetOrdinal++;
            var ordinal = packetOrdinal;
            var baseDelay = Math.Max(1, Conditions.LatencyTicks);
            if (Conditions.ReorderEvery > 0 && ordinal % (ulong)Conditions.ReorderEvery == 0) baseDelay++;
            if (Conditions.DropEvery > 0 && ordinal % (ulong)Conditions.DropEvery == 0)
            {
                Schedule(delivery, baseDelay + Conditions.RetryDelayTicks);
            }
            else
            {
                Schedule(delivery, baseDelay);
            }

            if (Conditions.DuplicateEvery > 0 && ordinal % (ulong)Conditions.DuplicateEvery == 0)
            {
                Schedule(delivery, baseDelay + 1);
            }
        }

        internal void SendReliable(MultiplayerTestSession target, Action delivery)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (delivery == null) throw new ArgumentNullException(nameof(delivery));
            var generation = target.ConnectionGeneration;
            SendReliable(() =>
            {
                if (target.CanReceiveTransport(generation)) delivery();
            });
        }

        internal IEnumerable<MultiplayerTestSession> ClientSessions =>
            nodes.Select(item => item.SessionImplementation).Where(item => item.HasClientSide);

        internal MultiplayerTestSession ServerSession => Server.SessionImplementation;

        internal NetworkObjectId AllocateObjectId() =>
            new NetworkObjectId("test-object-" + (++nextObjectId).ToString(System.Globalization.CultureInfo.InvariantCulture));

        internal bool IsParticipant(ParticipantId id) =>
            participants.TryGetValue(id, out var participant) && participant.Connected;

        internal void BroadcastState(TestStateSnapshot snapshot, MultiplayerTestSession? except = null)
        {
            foreach (var session in ClientSessions)
            {
                if (ReferenceEquals(session, except) || ReferenceEquals(session, ServerSession)) continue;
                var target = session;
                var copy = snapshot.Copy();
                SendReliable(target, () => target.ApplyCanonicalStates(
                    new[] { copy },
                    TestStateSnapshotScope.Delta));
            }
        }

        internal void BroadcastObject(TestObjectSnapshot snapshot, MultiplayerTestSession? except = null)
        {
            foreach (var session in ClientSessions)
            {
                if (ReferenceEquals(session, except) || ReferenceEquals(session, ServerSession)) continue;
                var target = session;
                var copy = snapshot.Copy();
                SendReliable(target, () => target.ApplyObjectSnapshot(copy));
            }
        }

        internal void DispatchPresentation(BufferedTestPresentation item, ParticipantId sender)
        {
            var sequenced = item.Sequence == 0
                ? item.WithSequence(++nextPresentationSequence)
                : item.Copy();
            foreach (var target in ClientSessions)
            {
                if (!target.Snapshot.HasPresentation || target.Snapshot.State != MultiplayerSessionState.Ready) continue;
                var localId = target.Snapshot.LocalParticipantId;
                var isSender = localId.HasValue && localId.Value.Equals(sender);
                if (sequenced.Audience == MultiplayerAudience.Sender && !isSender) continue;
                if (sequenced.Audience == MultiplayerAudience.Others && isSender) continue;
                var captured = sequenced.Copy();
                SendReliable(target, () => target.DeliverPresentation(captured));
            }
        }

        private void Synchronize(MultiplayerTestNode node)
        {
            if (disposed) return;
            var states = ServerSession.CaptureCanonicalStates();
            var objects = ServerSession.CaptureCanonicalObjects();
            node.SessionImplementation.ApplySynchronization(states, objects);
        }

        private void RefreshParticipants()
        {
            foreach (var node in nodes)
            {
                var local = node.SessionImplementation.Snapshot.LocalParticipantId;
                node.SessionImplementation.SetParticipants(ParticipantSnapshot(local));
            }
        }

        private MultiplayerParticipant[] ParticipantSnapshot(ParticipantId? local) =>
            participants.Values
                .Select(item => new MultiplayerParticipant(
                    item.Id,
                    item.DisplayName,
                    local.HasValue && local.Value.Equals(item.Id),
                    item.Connected))
                .ToArray();

        private void Schedule(Action action, int delay)
        {
            var due = currentTick + (ulong)delay;
            deliveries.Add(new ScheduledDelivery(due, ++deliveryOrdinal, action));
        }

        private void DeliverDuePackets()
        {
            while (true)
            {
                var next = deliveries
                    .Where(item => item.DueTick <= currentTick)
                    .OrderBy(item => item.DueTick)
                    .ThenBy(item => item.Ordinal)
                    .FirstOrDefault();
                if (next == null) return;
                deliveries.Remove(next);
                next.Deliver();
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(MultiplayerTestRig));
        }

        private void ValidateRemoteClient(MultiplayerTestNode client)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            ThrowIfDisposed();
            if (client.Role != MultiplayerTestRole.RemoteClient || !nodes.Contains(client))
            {
                throw new ArgumentException("Only a remote client owned by this rig can change connection state.", nameof(client));
            }
        }

        private sealed class ParticipantRecord
        {
            internal ParticipantRecord(ParticipantId id, string displayName, bool connected)
            {
                Id = id;
                DisplayName = displayName;
                Connected = connected;
            }

            internal ParticipantId Id { get; }
            internal string DisplayName { get; }
            internal bool Connected { get; set; }
        }

        private sealed class ScheduledDelivery
        {
            internal ScheduledDelivery(ulong dueTick, ulong ordinal, Action deliver)
            {
                DueTick = dueTick;
                Ordinal = ordinal;
                Deliver = deliver;
            }

            internal ulong DueTick { get; }
            internal ulong Ordinal { get; }
            internal Action Deliver { get; }
        }
    }
}
