using System;

namespace TopiaForge.ModManager.Core.Multiplayer
{
    /// <summary>
    /// One client's view of a body it may simulate. A grab is optimistic: the client starts simulating and streaming
    /// at once, under the epoch the host will assign if it grants the request, and rolls back if it is refused.
    /// </summary>
    internal sealed class OwnershipClaim
    {
        private readonly OwnershipPolicy policy;
        private readonly NetOwner self;

        public OwnershipClaim(int localParticipant, OwnershipPolicy? policy = null, uint knownEpoch = 0)
        {
            self = NetOwner.ForParticipant(localParticipant);
            this.policy = policy ?? OwnershipPolicy.Default;
            Epoch = knownEpoch;
        }

        public OwnershipState State { get; private set; } = OwnershipState.Server;

        /// <summary>The owner the host last reported.</summary>
        public NetOwner KnownOwner { get; private set; } = NetOwner.Server;

        /// <summary>The epoch the host last reported.</summary>
        public uint Epoch { get; private set; }

        /// <summary>Whether a grab has been sent and not yet answered.</summary>
        public bool GrantPending { get; private set; }

        public long ReleaseDeadlineMicros { get; private set; }

        /// <summary>Whether this client simulates the body and streams it.</summary>
        public bool IsSimulating => State != OwnershipState.Server;

        /// <summary>The epoch to stamp on samples: the one a pending grant will receive, or the confirmed one.</summary>
        public uint StreamEpoch => GrantPending ? unchecked(Epoch + 1) : Epoch;

        /// <summary>
        /// The local player grabs. Returns true when a request must be sent, naming <paramref name="requestEpoch"/>.
        /// Grabbing one's own throw during its release window takes it back under the epoch it is already owned by.
        /// </summary>
        public bool Grab(out uint requestEpoch)
        {
            switch (State)
            {
                case OwnershipState.Server:
                    requestEpoch = Epoch;
                    State = OwnershipState.Requested;
                    GrantPending = true;
                    return true;
                case OwnershipState.ReleaseWindow:
                    requestEpoch = StreamEpoch;
                    State = GrantPending ? OwnershipState.Requested : OwnershipState.Owned;
                    return true;
                default:
                    requestEpoch = StreamEpoch;
                    return false;
            }
        }

        /// <summary>
        /// The local player throws or drops the body. Returns true when the release must be sent, under
        /// <see cref="StreamEpoch"/>. The client keeps simulating until <see cref="ReleaseDeadlineMicros"/>.
        /// </summary>
        public bool BeginRelease(long nowMicros)
        {
            if (nowMicros < 0) throw new ArgumentOutOfRangeException(nameof(nowMicros));
            if (State != OwnershipState.Owned && State != OwnershipState.Requested) return false;
            State = OwnershipState.ReleaseWindow;
            ReleaseDeadlineMicros = nowMicros + policy.ReleaseWindowMicros;
            return true;
        }

        public bool IsHandbackDue(long nowMicros) =>
            State == OwnershipState.ReleaseWindow && nowMicros >= ReleaseDeadlineMicros;

        /// <summary>Stops simulating once the window has closed; send the hand-back under <paramref name="epoch"/>.</summary>
        public bool TryCompleteHandback(long nowMicros, out uint epoch)
        {
            epoch = StreamEpoch;
            if (!IsHandbackDue(nowMicros)) return false;
            State = OwnershipState.Server;
            return true;
        }

        /// <summary>The host granted the request (or reported it already owned) under <paramref name="epoch"/>.</summary>
        public void OnGranted(uint epoch)
        {
            Epoch = epoch;
            KnownOwner = self;
            GrantPending = false;
            if (State == OwnershipState.Requested) State = OwnershipState.Owned;
        }

        /// <summary>
        /// The host refused a request: another participant holds the body, or the request named an old epoch. The
        /// optimistic grab rolls back. A refusal that still names this client as owner changes nothing.
        /// </summary>
        public void OnRefused(uint epoch, NetOwner owner)
        {
            Epoch = epoch;
            KnownOwner = owner;
            GrantPending = false;
            if (!owner.Equals(self)) State = OwnershipState.Server;
        }

        /// <summary>
        /// The host announced a new owner and epoch. Losing the body (a reclaim, a steal, or the server accepting a
        /// hand-back) stops local simulation; hearing of this client's own grant confirms a pending grab.
        /// </summary>
        public void OnOwnerChanged(NetOwner owner, uint epoch)
        {
            Epoch = epoch;
            KnownOwner = owner;
            if (owner.Equals(self))
            {
                if (!GrantPending) return;
                GrantPending = false;
                if (State == OwnershipState.Requested) State = OwnershipState.Owned;
                return;
            }

            GrantPending = false;
            State = OwnershipState.Server;
        }
    }
}
