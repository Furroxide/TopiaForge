using System;

namespace TopiaForge.ModManager.Core
{
    /// <summary>The six committed phases of one authoritative session lifecycle.</summary>
    public enum SessionPhase { Idle, Preparing, LoadingWorld, StartingMode, Running, Stopping }
    public enum SessionAdmission { Accepted, Busy, StaleSession }

    /// <summary>Immutable identity allocated before extension activation.</summary>
    public sealed class SessionIdentity
    {
        public SessionIdentity(string sessionId, string requestId, LaunchPlanDescriptor selection)
        {
            SessionId = LaunchContractValues.Token(sessionId, nameof(sessionId));
            RequestId = LaunchContractValues.Token(requestId, nameof(requestId));
            Selection = selection ?? throw new ArgumentNullException(nameof(selection));
        }
        public string SessionId { get; }
        public string RequestId { get; }
        public LaunchPlanDescriptor Selection { get; }
    }

    public sealed class SessionStateSnapshot
    {
        internal SessionStateSnapshot(SessionPhase phase, SessionIdentity? identity, int sequence)
        { Phase = phase; Identity = identity; Sequence = sequence; }
        public SessionPhase Phase { get; }
        public SessionIdentity? Identity { get; }
        public int Sequence { get; }
    }

    /// <summary>A generation-bound admission lease; callbacks cannot manufacture one.</summary>
    public sealed class SessionOperationLease
    {
        internal SessionOperationLease() { }
    }

    /// <summary>Pure state authority. Its host serializes calls; extension callbacks are never invoked here.</summary>
    public sealed class SessionLifecycle
    {
        private SessionOperationLease? operation;
        private SessionStateSnapshot snapshot = new SessionStateSnapshot(SessionPhase.Idle, null, 0);
        public SessionStateSnapshot Current => snapshot;
        public bool HasOperation => operation != null;

        public SessionAdmission TryAcquire(bool nativeBusy, string? expectedSessionId, out SessionOperationLease? lease)
        {
            lease = null;
            if (expectedSessionId != null && expectedSessionId != snapshot.Identity?.SessionId) return SessionAdmission.StaleSession;
            if (nativeBusy || operation != null || (snapshot.Phase != SessionPhase.Idle && snapshot.Phase != SessionPhase.Running))
                return SessionAdmission.Busy;
            lease = operation = new SessionOperationLease();
            return SessionAdmission.Accepted;
        }

        public SessionStateSnapshot Commit(SessionOperationLease lease, SessionPhase next, SessionIdentity? identity = null)
        {
            Require(lease);
            var previous = snapshot.Phase;
            var legal = (previous == SessionPhase.Idle && next == SessionPhase.Preparing)
                || (previous == SessionPhase.Preparing && next == SessionPhase.LoadingWorld)
                || (previous == SessionPhase.LoadingWorld && next == SessionPhase.StartingMode)
                || (previous == SessionPhase.StartingMode && next == SessionPhase.Running)
                || (previous != SessionPhase.Idle && previous != SessionPhase.Stopping && next == SessionPhase.Stopping)
                || (previous == SessionPhase.Stopping && next == SessionPhase.Idle);
            if (!legal) throw new InvalidOperationException("Invalid session phase transition " + previous + " -> " + next + ".");
            if (next == SessionPhase.Preparing && identity == null) throw new ArgumentNullException(nameof(identity));
            if (next != SessionPhase.Preparing && identity != null) throw new InvalidOperationException("Session identity cannot change within a lifecycle.");
            snapshot = new SessionStateSnapshot(next, next == SessionPhase.Idle ? null : identity ?? snapshot.Identity, checked(snapshot.Sequence + 1));
            return snapshot;
        }

        public void Release(SessionOperationLease lease)
        {
            Require(lease);
            if (snapshot.Phase != SessionPhase.Idle && snapshot.Phase != SessionPhase.Running)
                throw new InvalidOperationException("A session operation cannot release ownership before readiness or cleanup.");
            operation = null;
        }

        private void Require(SessionOperationLease lease)
        {
            if (!ReferenceEquals(operation, lease) || lease == null) throw new InvalidOperationException("The session operation lease is stale.");
        }
    }
}
