using System;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>
    /// What the runtime asks of the engine beyond its system channels: readiness holds and how long a host waits for them,
    /// the sources that name entities and avatars, the local player's name, and a flush at the end of the frame. See
    /// docs/internal/MultiplayerRuntimeIntegration.md.
    /// </summary>
    internal sealed partial class SessionEngine
    {
        private int readinessHolds;
        private bool readinessAnchored;
        private long readinessHeldSinceMicros;
        private long readinessProgressMicros;
        private long sessionReplacementsReceived;
        private IInternalEntityIdentitySource? entityIdentitySource;
        private IInternalAvatarSource? avatarSource;

        /// <summary>Gets the current session's id without building a snapshot, so a per-frame check allocates nothing.</summary>
        internal MultiplayerSessionId CurrentSessionId => sessionId;

        /// <summary>Gets the session's state without building a snapshot.</summary>
        internal MultiplayerSessionState SessionState => state;

        /// <summary>Gets how many readiness holds are open.</summary>
        internal int ReadinessHolds => readinessHolds;

        /// <summary>Gets whether this client has the host's snapshot and waits only for its own readiness holds.</summary>
        internal bool IsAwaitingLocalReadiness => Role == EngineRole.Client && channel != null && channel.ReadinessHeld;

        /// <summary>
        /// Gets when readiness holds first kept this client short of Ready since it was last Ready, in engine time: the
        /// first snapshot they held, whatever replacements followed. Null when no hold has applied since.
        /// </summary>
        internal long? ReadinessHeldSinceMicros => readinessAnchored ? readinessHeldSinceMicros : (long?)null;

        /// <summary>Gets the latest progress while held: the snapshot, a system message from the host, or a progress report.</summary>
        internal long ReadinessProgressMicros => readinessProgressMicros;

        /// <summary>Gets how many times a host replaced the session under this client, so a join can tell a new snapshot is due.</summary>
        internal long SessionReplacementsReceived => sessionReplacementsReceived;

        /// <summary>
        /// Holds this player short of Ready until the returned hold is disposed. A client that has the host's snapshot
        /// waits, Synchronizing and without acknowledging, until the last hold goes. A host's holds change nothing.
        /// </summary>
        internal IInternalReadinessHold HoldReadiness()
        {
            RequireOwnerThread();
            readinessHolds++;
            return new ReadinessHold(this);
        }

        /// <summary>Gets whether <paramref name="participant"/> holds the session and acknowledged it; a client knows only itself.</summary>
        internal bool IsParticipantReady(ParticipantId participant)
        {
            RequireOwnerThread();
            if (Role == EngineRole.Client)
            {
                return localParticipantId.HasValue && localParticipantId.Value.Equals(participant) && state == MultiplayerSessionState.Ready;
            }

            var record = FindParticipant(participant);
            if (record == null || !record.IsConnected) return false;
            return record.Identity == null ? state == MultiplayerSessionState.Ready : record.Connection?.Phase == HostConnectionPhase.Ready;
        }

        /// <summary>Sets the source that names network objects; one at a time.</summary>
        internal IDisposable SetEntityIdentitySource(IInternalEntityIdentitySource source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            RequireOwnerThread();
            if (entityIdentitySource != null) throw new InvalidOperationException("An entity identity source is already set.");
            entityIdentitySource = source;
            return new EngineLease(() => Post(() =>
            {
                if (ReferenceEquals(entityIdentitySource, source)) entityIdentitySource = null;
            }));
        }

        /// <summary>Sets the source of avatars and poses; one at a time.</summary>
        internal IDisposable SetAvatarSource(IInternalAvatarSource source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            RequireOwnerThread();
            if (avatarSource != null) throw new InvalidOperationException("An avatar source is already set.");
            avatarSource = source;
            return new EngineLease(() => Post(() =>
            {
                if (ReferenceEquals(avatarSource, source)) avatarSource = null;
            }));
        }

        internal bool TryGetNetworkObjectId(IEntity entity, out NetworkObjectId id)
        {
            id = default;
            var source = entityIdentitySource;
            if (source == null || !IsOwnerThread) return false;
            try
            {
                return source.TryGetNetworkObjectId(entity, out id);
            }
            catch (Exception exception) when (options.CallbackFailures == EngineCallbackFailureMode.Report)
            {
                Report(new EngineDiagnostic(EngineDiagnosticKind.CallbackFailed, "The entity identity source threw.", null, exception));
                id = default;
                return false;
            }
        }

        internal bool TryGetEntity(NetworkObjectId id, out IEntity? entity)
        {
            entity = null;
            var source = entityIdentitySource;
            if (source == null || !IsOwnerThread) return false;
            try
            {
                return source.TryGetEntity(id, out entity);
            }
            catch (Exception exception) when (options.CallbackFailures == EngineCallbackFailureMode.Report)
            {
                Report(new EngineDiagnostic(EngineDiagnosticKind.CallbackFailed, "The entity identity source threw.", null, exception));
                entity = null;
                return false;
            }
        }

        internal bool TryGetAvatar(ParticipantId participant, out IEntity? avatar)
        {
            avatar = null;
            var source = avatarSource;
            if (source == null || !IsOwnerThread) return false;
            try
            {
                return source.TryGetAvatar(participant, out avatar);
            }
            catch (Exception exception) when (options.CallbackFailures == EngineCallbackFailureMode.Report)
            {
                Report(new EngineDiagnostic(EngineDiagnosticKind.CallbackFailed, "The avatar source threw.", participant, exception));
                avatar = null;
                return false;
            }
        }

        internal bool TryGetPose(ParticipantId participant, out MultiplayerPose pose)
        {
            pose = default;
            var source = avatarSource;
            if (source == null || !IsOwnerThread) return false;
            try
            {
                return source.TryGetPose(participant, out pose);
            }
            catch (Exception exception) when (options.CallbackFailures == EngineCallbackFailureMode.Report)
            {
                Report(new EngineDiagnostic(EngineDiagnosticKind.CallbackFailed, "The avatar source threw.", participant, exception));
                pose = default;
                return false;
            }
        }

        /// <summary>Renames the local player of a hosting engine; every joined player hears it at the next flush.</summary>
        internal void SetLocalDisplayName(string displayName)
        {
            if (!ProtocolLimits.IsValidIdentity(displayName)) throw new ArgumentException("A bounded display name is required.", nameof(displayName));
            RequireOwnerThread();
            if (scope.IsOpen) throw new InvalidOperationException("The local player cannot be renamed inside a transaction.");
            if (Role != EngineRole.Host || !localParticipantId.HasValue) return;
            var record = FindParticipant(localParticipantId.Value);
            if (record == null || string.Equals(record.DisplayName, displayName, StringComparison.Ordinal)) return;
            record.DisplayName = displayName;
            MarkParticipantsChanged();
            EndOperation();
        }

        /// <summary>
        /// Sends what is queued without receiving or stepping: the end of the frame, after the runtime's own sends. A host
        /// keeps its downstream cadence; only what is due goes out.
        /// </summary>
        internal void FlushOutbound()
        {
            RequireOwnerThread();
            if (scope.IsOpen || settling || pumping)
            {
                throw new InvalidOperationException("The engine cannot flush from inside a transaction, a callback or a pump.");
            }

            if (ended) return;
            pumping = true;
            try
            {
                if (Role == EngineRole.Host) FlushHost();
                else FlushClient();
            }
            finally
            {
                pumping = false;
            }

            Settle();
        }

        /// <summary>
        /// Ends a joined connection that took the snapshot but never acknowledged it within the host's bound, after a join
        /// or a session replacement, so a player that stalls cannot hold its slot for ever. Returns whether it ended.
        /// </summary>
        private bool ExpireUnreadyConnection(HostConnection connection)
        {
            if (connection.Phase != HostConnectionPhase.Accepted || nowMicros - connection.SnapshotSentAtMicros <= options.ReadyTimeoutMicros) return false;
            EndConnection(connection, MultiplayerSessionEndReason.TimedOut, "The player did not finish joining in time.");
            return true;
        }

        /// <summary>
        /// Ends a Ready player whose window on a system channel stayed full for <see cref="InternalSystemLimits.StallSeconds"/>,
        /// with the host waiting to send and the player crediting nothing, as a protocol violation. Its full window holds
        /// back nobody else, but a player that never takes anything has left the session in all but name. Before the player
        /// is Ready its stream can wait behind the snapshot on a slow link, so the stall counts from then; the host's Ready
        /// bound covers the time before. Returns whether it ended.
        /// </summary>
        private bool ExpireStalledConnection(HostConnection connection)
        {
            if (connection.Phase != HostConnectionPhase.Ready) return false;
            var blockedSince = connection.Streams.BlockedSinceMicros();
            if (blockedSince == null) return false;
            var since = Math.Max(blockedSince.Value, connection.ReadyAtMicros);
            if (nowMicros - since <= InternalSystemLimits.StallSeconds * 1_000_000L) return false;
            Report(new EngineDiagnostic(
                EngineDiagnosticKind.ProtocolViolation,
                "A client stopped taking system messages: a channel's window stayed full.",
                connection.Participant?.Id));
            EndConnection(connection, MultiplayerSessionEndReason.ProtocolMismatch, "The client violated the session protocol.");
            return true;
        }

        /// <summary>Records that the runtime is still preparing, which a held client counts as progress.</summary>
        private void ReportReadinessProgress()
        {
            RequireOwnerThread();
            if (Role == EngineRole.Client && channel != null && channel.ReadinessHeld) readinessProgressMicros = nowMicros;
        }

        private void ReleaseReadiness()
        {
            if (readinessHolds == 0) return;
            readinessHolds--;
            var current = channel;
            if (readinessHolds > 0 || Role != EngineRole.Client || current == null || !current.ReadinessHeld) return;
            current.ReadinessHeld = false;
            EnterClientReady(current);
            EndOperation();
        }

        /// <summary>
        /// The snapshot is complete while holds are open: the client waits, and the snapshot counts as progress. The holds'
        /// total counts from the first time this happened since the player was last Ready, so a host that replaces the
        /// session again and again cannot start it over.
        /// </summary>
        private void EnterReadinessHeld(ClientChannel current)
        {
            current.ReadinessHeld = true;
            if (!readinessAnchored)
            {
                readinessAnchored = true;
                readinessHeldSinceMicros = nowMicros;
            }

            readinessProgressMicros = nowMicros;
        }

        /// <summary>The client holds the session: it reports Ready and acknowledges it at the next flush.</summary>
        private void EnterClientReady(ClientChannel current)
        {
            current.Phase = ClientPhase.Ready;
            current.ReadyAckPending = true;
            state = MultiplayerSessionState.Ready;
            readinessAnchored = false;
            MarkSessionChanged();
        }

        /// <summary>One readiness hold: disposing it releases the hold once, from any thread.</summary>
        private sealed class ReadinessHold : IInternalReadinessHold
        {
            private SessionEngine? engine;

            internal ReadinessHold(SessionEngine engine)
            {
                this.engine = engine;
            }

            public void ReportProgress() => Volatile.Read(ref engine)?.ReportReadinessProgress();

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref engine, null);
                owner?.Post(owner.ReleaseReadiness);
            }
        }
    }
}
