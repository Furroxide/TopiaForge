using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>The solo session a process starts when it leaves a joined session, or returns to when that session ends.</summary>
    internal sealed class SoloSessionStart
    {
        internal SoloSessionStart(MultiplayerSessionId sessionId, SessionSeed seed, ParticipantId participant, string displayName)
        {
            if (!sessionId.IsValid) throw new ArgumentException("A solo session needs an id.", nameof(sessionId));
            if (!participant.IsValid) throw new ArgumentException("A solo session needs its local player.", nameof(participant));
            if (!ProtocolLimits.IsValidIdentity(displayName)) throw new ArgumentException("A bounded display name is required.", nameof(displayName));
            SessionId = sessionId;
            Seed = seed;
            Participant = participant;
            DisplayName = displayName;
        }

        internal MultiplayerSessionId SessionId { get; }
        internal SessionSeed Seed { get; }
        internal ParticipantId Participant { get; }
        internal string DisplayName { get; }
    }

    /// <summary>How a client's session ended: the reason, the host's message, and its admission report when it refused the join.</summary>
    internal sealed class ClientSessionEnd
    {
        internal ClientSessionEnd(MultiplayerSessionEndReason reason, string message, byte[]? report)
        {
            Reason = reason;
            Message = message ?? string.Empty;
            Report = report == null ? Array.Empty<byte>() : (byte[])report.Clone();
        }

        internal MultiplayerSessionEndReason Reason { get; }
        internal string Message { get; }

        /// <summary>Gets the host's opaque admission report, empty unless the host refused the join.</summary>
        internal byte[] Report { get; }
    }

    /// <summary>
    /// Role changes. One engine serves a process for its whole life, so the registrations, facades and subscriptions mods
    /// hold stay valid when the process joins another host or comes back to solo play. To the mods, each change looks
    /// like a session replacement they already handle: the current session's token is cancelled, values return to their
    /// declared defaults, objects despawn, and the snapshot names the new session.
    /// </summary>
    internal sealed partial class SessionEngine
    {
        /// <summary>
        /// Turns this process's solo session into a client waiting to join another host through <see cref="Connect"/>.
        /// A host that still serves clients must close them first with <see cref="CloseConnections"/>.
        /// </summary>
        internal void SwitchToClient(MultiplayerSessionId placeholderSessionId)
        {
            RequireOwnerThread();
            if (scope.IsOpen) throw new InvalidOperationException("The session cannot change role inside a transaction.");
            if (ended) throw new ObjectDisposedException(nameof(SessionEngine));
            if (Role != EngineRole.Host) throw new InvalidOperationException("Only a solo host becomes a client.");
            if (ProcessKind != MultiplayerProcessKind.Interactive) throw new InvalidOperationException("A headless host has no player to join with.");
            if (connections.Count > 0) throw new InvalidOperationException("Close every client connection before joining another host.");
            if (!placeholderSessionId.IsValid) throw new ArgumentException("A placeholder session id is required.", nameof(placeholderSessionId));
            RetireLocalSession(placeholderSessionId, default, 0, "The session was replaced by a joined session.");
            participantRecords.Clear();
            participantsDirty = false;
            Role = EngineRole.Client;
            localParticipantId = null;
            hostParticipantId = null;
            tick = 0;
            hostClock = null;
            joinedBefore = false;
            state = MultiplayerSessionState.Connecting;
            MarkSessionChanged(participantsChanged: true);
            EndOperation();
        }

        /// <summary>
        /// Leaves the joined session, telling the host when the link is still up, and starts <paramref name="start"/> as
        /// a solo session. A client whose session already ended may return this way too.
        /// </summary>
        internal void SwitchToSolo(SoloSessionStart start)
        {
            if (start == null) throw new ArgumentNullException(nameof(start));
            RequireOwnerThread();
            if (scope.IsOpen) throw new InvalidOperationException("The session cannot change role inside a transaction.");
            if (Role != EngineRole.Client) throw new InvalidOperationException("Only a client returns to solo play.");
            if (channel != null) ShutdownClient();
            BecomeSolo(start);
            EndOperation();
        }

        /// <summary>
        /// Ends every client connection with <paramref name="reason"/> and carries on as a solo session: the values and
        /// objects stay, and departed players leave the participant list at once rather than after their grace period.
        /// </summary>
        internal void CloseConnections(MultiplayerSessionEndReason reason, string message)
        {
            RequireOwnerThread();
            if (scope.IsOpen) throw new InvalidOperationException("Connections cannot close inside a transaction.");
            if (ended) throw new ObjectDisposedException(nameof(SessionEngine));
            if (Role != EngineRole.Host) throw new InvalidOperationException("Only a host has client connections.");
            foreach (var connection in connections.ToArray()) EndConnection(connection, reason, message);
            foreach (var record in participantRecords) NoteVoiceSeen(record);
            if (participantRecords.RemoveAll(record => record.Identity != null) > 0) MarkParticipantsChanged();
            EndOperation();
        }

        /// <summary>
        /// Ends a client's session. When the options name a solo session to return to, the process goes back to solo
        /// play instead of ending, so mods see one more replacement rather than an ended session.
        /// </summary>
        private void FinishClientSession(ClientSessionEnd end)
        {
            var solo = options.AfterClientSessionEnds?.Invoke(end);
            if (solo != null)
            {
                BecomeSolo(solo);
                return;
            }

            EnterEnded(end.Reason);
        }

        /// <summary>Gets the authenticated identity a host knows a participant by, or null for the host's own player.</summary>
        internal string? ParticipantIdentityOf(ParticipantId participant)
        {
            RequireOwnerThread();
            if (Role != EngineRole.Host) return null;
            foreach (var record in participantRecords)
            {
                if (record.Id.Equals(participant)) return record.Identity;
            }

            return null;
        }

        private void BecomeSolo(SoloSessionStart start)
        {
            RetireLocalSession(start.SessionId, start.Seed, 0, "The joined session ended.");
            clientParticipants = new List<ParticipantEntry>();
            Role = EngineRole.Host;
            ended = false;
            endReason = MultiplayerSessionEndReason.None;
            originMicros = nowMicros;
            tick = 0;
            hostClock = null;
            nextObjectNumber = 0;
            state = MultiplayerSessionState.Ready;
            InitializeHost(start.Participant, start.DisplayName);
        }

        /// <summary>Retires the current session for every local observer, as a replacement does, whatever the role.</summary>
        private void RetireLocalSession(MultiplayerSessionId newSession, SessionSeed newSeed, ulong newEpoch, string message)
        {
            ResetLocalSession(newSession, newSeed, newEpoch, message);
            foreach (var binding in commands.Values) binding.ResetForNewSession();
            reconcileRequested = false;
            RetireVoice();
        }
    }
}
