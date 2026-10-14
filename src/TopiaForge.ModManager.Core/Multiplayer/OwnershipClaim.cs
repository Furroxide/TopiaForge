using System;
using System.Collections.Generic;

namespace TopiaForge.ModManager.Core.Multiplayer
{
    /// <summary>
    /// One client's view of a body it may simulate. A grab is optimistic: the client starts simulating and streaming
    /// at once, under the epoch the host will assign if it grants the request, and rolls back if it is refused.
    /// </summary>
    /// <remarks>
    /// The claim keeps what the host holds for this client as far as the host has confirmed it, plus the messages this
    /// client sent that no answer covers yet: its requests, and the throws and hand-backs around them. It shows the
    /// confirmed state with those messages replayed on top, predicting a request granted when the body is free or
    /// already this client's. The host's answers and announcements must reach the claim in the order the host sent
    /// them: exactly one answer per request, <see cref="OnGranted"/> or <see cref="OnRefused"/>, and an
    /// <see cref="OnOwnerChanged"/> for every change of owner. An answer settles the oldest request. The throws and
    /// hand-backs sent before it are applied first, as the host applied them, epoch checks included; then the answer
    /// itself. So a refused catch of the client's own throw resumes the release window with the deadline the host
    /// kept, and a grant that arrives after a rollback still settles where the host is.
    /// </remarks>
    internal sealed class OwnershipClaim
    {
        /// <summary>The most requests that may wait for answers at once; a grab beyond it is not sent.</summary>
        public const int MaximumOutstandingRequests = 16;

        private readonly OwnershipPolicy policy;
        private readonly NetOwner self;

        // Between two requests a client sends at most one throw and one hand-back, so this capacity is never exceeded.
        private readonly List<Message> unanswered = new List<Message>(3 * MaximumOutstandingRequests + 2);
        private OwnershipState confirmed = OwnershipState.Server;
        private uint confirmedEpoch;
        private long confirmedDeadlineMicros;
        private int outstandingRequests;
        private bool grantAnnounced;
        private bool grantAnswered;
        private uint grantAnsweredEpoch;

        public OwnershipClaim(int localParticipant, OwnershipPolicy? policy = null, uint knownEpoch = 0)
        {
            self = NetOwner.ForParticipant(localParticipant);
            this.policy = policy ?? OwnershipPolicy.Default;
            Epoch = knownEpoch;
        }

        private enum MessageKind
        {
            Request,
            Throw,
            Handback
        }

        public OwnershipState State { get; private set; } = OwnershipState.Server;

        /// <summary>The owner the host last reported.</summary>
        public NetOwner KnownOwner { get; private set; } = NetOwner.Server;

        /// <summary>The epoch the host last reported.</summary>
        public uint Epoch { get; private set; }

        /// <summary>Whether the claim counts on a grant of a free body that the host has not confirmed yet.</summary>
        public bool GrantPending { get; private set; }

        /// <summary>How many requests wait for answers.</summary>
        public int OutstandingRequests => outstandingRequests;

        public long ReleaseDeadlineMicros { get; private set; }

        /// <summary>Whether this client simulates the body and streams it.</summary>
        public bool IsSimulating => State != OwnershipState.Server;

        /// <summary>The epoch to stamp on samples: the one a pending grant will receive, or the confirmed one.</summary>
        public uint StreamEpoch => GrantPending ? unchecked(Epoch + 1) : Epoch;

        /// <summary>
        /// The local player grabs. Returns true when a request must be sent, naming <paramref name="requestEpoch"/>.
        /// Grabbing one's own throw during its release window takes it back under the epoch it is already owned by,
        /// while at most <see cref="MaximumOutstandingRequests"/> answers are due. A grab of a free body is simulated at
        /// once; one of a body another player holds is sent but not simulated until granted. Neither is sent while an
        /// answer is due, since that answer may change the epoch the request must name.
        /// </summary>
        public bool Grab(out uint requestEpoch)
        {
            requestEpoch = State == OwnershipState.Server ? Epoch : StreamEpoch;
            if (State == OwnershipState.Server ? outstandingRequests > 0 : State != OwnershipState.ReleaseWindow) return false;
            if (outstandingRequests >= MaximumOutstandingRequests) return false;
            unanswered.Add(new Message(MessageKind.Request, requestEpoch, 0));
            outstandingRequests++;
            Predict();
            return true;
        }

        /// <summary>
        /// The local player throws or drops the body. Returns true when the release must be sent, under
        /// <see cref="StreamEpoch"/>. The client keeps simulating until <see cref="ReleaseDeadlineMicros"/>.
        /// </summary>
        public bool BeginRelease(long nowMicros)
        {
            if (nowMicros < 0) throw new ArgumentOutOfRangeException(nameof(nowMicros));
            if (State != OwnershipState.Owned && State != OwnershipState.Requested) return false;
            Send(new Message(MessageKind.Throw, StreamEpoch, nowMicros + policy.ReleaseWindowMicros));
            return true;
        }

        public bool IsHandbackDue(long nowMicros) =>
            State == OwnershipState.ReleaseWindow && nowMicros >= ReleaseDeadlineMicros;

        /// <summary>Stops simulating once the window has closed; send the hand-back under <paramref name="epoch"/>.</summary>
        public bool TryCompleteHandback(long nowMicros, out uint epoch)
        {
            epoch = StreamEpoch;
            if (!IsHandbackDue(nowMicros)) return false;
            Send(new Message(MessageKind.Handback, epoch, 0));
            return true;
        }

        /// <summary>
        /// The host granted the oldest outstanding request, or reported the body already this client's, under
        /// <paramref name="epoch"/>.
        /// </summary>
        public void OnGranted(uint epoch)
        {
            Epoch = epoch;
            KnownOwner = self;
            grantAnswered = false;
            if (grantAnnounced)
            {
                // The host announced this grant just before answering, and the announcement settled it.
                grantAnnounced = false;
            }
            else if (SettleOldestRequest())
            {
                ConfirmGrant(epoch);
                grantAnswered = true;
                grantAnsweredEpoch = epoch;
                ApplyIfNothingOutstanding();
            }

            Predict();
        }

        /// <summary>
        /// The host refused the oldest outstanding request: another participant holds the body, the requester is out of
        /// range or yielding, or the request named an old epoch. A refusal changes nothing on the host, so it settles the
        /// request and nothing more; if the body has left this client, an announcement said so first.
        /// </summary>
        public void OnRefused(uint epoch, NetOwner owner)
        {
            Epoch = epoch;
            KnownOwner = owner;
            grantAnnounced = false;
            grantAnswered = false;
            if (SettleOldestRequest()) ApplyIfNothingOutstanding();
            Predict();
        }

        /// <summary>
        /// The host announced a new owner and epoch. Losing the body (a reclaim, a steal, or the server taking a
        /// hand-back) stops local simulation. Hearing of this client's own grant before its answer settles the oldest
        /// request, and the answer that follows changes nothing more; hearing of it after the answer changes nothing.
        /// </summary>
        public void OnOwnerChanged(NetOwner owner, uint epoch)
        {
            Epoch = epoch;
            KnownOwner = owner;
            var answeredAlready = grantAnswered && grantAnsweredEpoch == epoch;
            grantAnswered = false;
            if (!owner.Equals(self))
            {
                confirmed = OwnershipState.Server;
            }
            else if (!answeredAlready && SettleOldestRequest())
            {
                ConfirmGrant(epoch);
                grantAnnounced = true;
                ApplyIfNothingOutstanding();
            }

            Predict();
        }

        /// <summary>The host holds the body for this client under <paramref name="epoch"/>.</summary>
        private void ConfirmGrant(uint epoch)
        {
            confirmed = OwnershipState.Owned;
            confirmedEpoch = epoch;
        }

        /// <summary>Applies the messages sent before the oldest request and removes them with it. False if none is due.</summary>
        private bool SettleOldestRequest()
        {
            var index = 0;
            while (index < unanswered.Count && unanswered[index].Kind != MessageKind.Request) index++;
            if (index == unanswered.Count) return false;
            for (var applied = 0; applied < index; applied++) Apply(unanswered[applied]);
            unanswered.RemoveRange(0, index + 1);
            outstandingRequests--;
            return true;
        }

        /// <summary>With no answer due, the messages after the last request take effect as the host applied them.</summary>
        private void ApplyIfNothingOutstanding()
        {
            if (outstandingRequests > 0) return;
            foreach (var message in unanswered) Apply(message);
            unanswered.Clear();
        }

        /// <summary>A throw or hand-back takes effect at once unless an answer sent before it is still due.</summary>
        private void Send(Message message)
        {
            unanswered.Add(message);
            ApplyIfNothingOutstanding();
            Predict();
        }

        /// <summary>
        /// A throw or hand-back as the host applies it: only under the epoch the body is held by, and only while it is
        /// this client's. A throw opens the window if it is not open yet; a hand-back returns the body.
        /// </summary>
        private void Apply(Message message)
        {
            if (confirmed == OwnershipState.Server || message.Epoch != confirmedEpoch) return;
            if (message.Kind == MessageKind.Handback)
            {
                confirmed = OwnershipState.Server;
            }
            else if (message.Kind == MessageKind.Throw && confirmed == OwnershipState.Owned)
            {
                confirmed = OwnershipState.ReleaseWindow;
                confirmedDeadlineMicros = message.DeadlineMicros;
            }
        }

        /// <summary>Replays the unanswered messages on the confirmed state, predicting that requests succeed where they can.</summary>
        private void Predict()
        {
            var state = confirmed;
            var deadline = confirmedDeadlineMicros;
            var pendingGrant = false;
            var free = KnownOwner.IsServer;
            foreach (var message in unanswered)
            {
                switch (message.Kind)
                {
                    case MessageKind.Request:
                        if (state == OwnershipState.ReleaseWindow)
                        {
                            state = OwnershipState.Owned;
                        }
                        else if (state == OwnershipState.Server && free)
                        {
                            state = OwnershipState.Owned;
                            pendingGrant = true;
                        }

                        break;
                    case MessageKind.Throw:
                        if (state == OwnershipState.Owned)
                        {
                            state = OwnershipState.ReleaseWindow;
                            deadline = message.DeadlineMicros;
                        }

                        break;
                    default:
                        state = OwnershipState.Server;
                        pendingGrant = false;
                        free = true;
                        break;
                }
            }

            GrantPending = pendingGrant;
            State = state == OwnershipState.Owned && pendingGrant ? OwnershipState.Requested : state;
            if (state == OwnershipState.ReleaseWindow) ReleaseDeadlineMicros = deadline;
        }

        /// <summary>A message sent to the host under an epoch: a request, a throw with its release deadline, or a hand-back.</summary>
        private readonly struct Message
        {
            public Message(MessageKind kind, uint epoch, long deadlineMicros)
            {
                Kind = kind;
                Epoch = epoch;
                DeadlineMicros = deadlineMicros;
            }

            public MessageKind Kind { get; }

            public uint Epoch { get; }

            public long DeadlineMicros { get; }
        }
    }
}
