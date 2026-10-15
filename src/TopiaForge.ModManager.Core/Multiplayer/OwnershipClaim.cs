using System;
using System.Collections.Generic;

namespace TopiaForge.ModManager.Core.Multiplayer
{
    /// <summary>
    /// One client's view of a body it may simulate. A grab is optimistic: the client starts simulating and streaming
    /// at once, under the epoch the host will assign if it grants the request, and rolls back if it is refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The claim keeps what the host holds for this client as far as the host's answers have confirmed it, plus the
    /// messages this client sent that no answer covers yet: its requests, and the throws and hand-backs around them. It
    /// shows the confirmed state with those messages replayed on top, predicting each request granted unless a report
    /// already says the host has moved past the epoch it names.
    /// </para>
    /// <para>
    /// The delivery contract: requests, throws and hand-backs reach the host in the order sent, and the host's answers
    /// and announcements reach the claim in the order the host sent them, on one ordered channel that loses nothing
    /// while the link lasts. The host answers every request exactly once, with <see cref="OnGranted"/> or
    /// <see cref="OnRefused"/>, as it handles the request. It announces every change of owner with
    /// <see cref="OnOwnerChanged"/> at any later point, or only the newest owner of a frame, and may leave a
    /// requester's own grant to its answer. Only an answer settles a request, the oldest one due: the throws and
    /// hand-backs sent before it take effect first, as the host applied them, epoch checks included. An announcement
    /// records the owner and epoch and settles nothing. The claim never takes an epoch older than one it has heard, and
    /// any newer epoch ends the hold it had confirmed. When the order breaks, because the link closed, the client
    /// reconnected or the session was replaced, the runtime calls <see cref="Reset"/>.
    /// </para>
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
        private uint streamEpoch;
        private bool heard;

        public OwnershipClaim(int localParticipant, OwnershipPolicy? policy = null, uint knownEpoch = 0)
        {
            self = NetOwner.ForParticipant(localParticipant);
            this.policy = policy ?? OwnershipPolicy.Default;
            Epoch = knownEpoch;
            streamEpoch = knownEpoch;
        }

        private enum MessageKind
        {
            Request,
            Throw,
            Handback
        }

        public OwnershipState State { get; private set; } = OwnershipState.Server;

        /// <summary>The owner in the newest report the claim has heard.</summary>
        public NetOwner KnownOwner { get; private set; } = NetOwner.Server;

        /// <summary>
        /// The epoch in the newest report the claim has heard, which a grab names. It never moves backwards, except that
        /// the first report after construction or <see cref="Reset"/> is taken whatever its epoch.
        /// </summary>
        public uint Epoch { get; private set; }

        /// <summary>Whether the claim counts on a grant the host has not answered yet.</summary>
        public bool GrantPending { get; private set; }

        /// <summary>How many requests wait for answers.</summary>
        public int OutstandingRequests => outstandingRequests;

        public long ReleaseDeadlineMicros { get; private set; }

        /// <summary>Whether this client simulates the body and streams it.</summary>
        public bool IsSimulating => State != OwnershipState.Server;

        /// <summary>
        /// The epoch to stamp on samples, throws and hand-backs: the pending grab's epoch plus one, which the host will
        /// assign if it grants it, or the epoch of the hold the host confirmed.
        /// </summary>
        public uint StreamEpoch => streamEpoch;

        /// <summary>
        /// The local player grabs. Returns true when a request must be sent, naming <paramref name="requestEpoch"/>.
        /// Grabbing one's own throw during its release window takes it back under the epoch it is already owned by,
        /// while at most <see cref="MaximumOutstandingRequests"/> answers are due. A grab of a body this client does not
        /// hold is simulated at once, whoever holds it now: the host grants a held body only once its owner has thrown
        /// it, which the client cannot see, and a steal's first sample must reach the host before the first-sample
        /// deadline. Such a grab is not sent while an answer is due, since that answer may change the epoch it must name.
        /// </summary>
        public bool Grab(out uint requestEpoch)
        {
            var fresh = State == OwnershipState.Server;
            requestEpoch = fresh ? Epoch : streamEpoch;
            if (fresh ? outstandingRequests > 0 : State != OwnershipState.ReleaseWindow) return false;
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
            Send(new Message(MessageKind.Throw, streamEpoch, nowMicros + policy.ReleaseWindowMicros));
            return true;
        }

        public bool IsHandbackDue(long nowMicros) =>
            State == OwnershipState.ReleaseWindow && nowMicros >= ReleaseDeadlineMicros;

        /// <summary>Stops simulating once the window has closed; send the hand-back under <paramref name="epoch"/>.</summary>
        public bool TryCompleteHandback(long nowMicros, out uint epoch)
        {
            epoch = streamEpoch;
            if (!IsHandbackDue(nowMicros)) return false;
            Send(new Message(MessageKind.Handback, epoch, 0));
            return true;
        }

        /// <summary>
        /// The host's answer to the oldest outstanding request: it granted it, or reported the body already this
        /// client's, under <paramref name="epoch"/>.
        /// </summary>
        public void OnGranted(uint epoch)
        {
            Record(epoch, self);
            Settle(true, epoch);
        }

        /// <summary>
        /// The host's answer to the oldest outstanding request: another participant holds the body, the requester is out
        /// of range, yielding or limited, or the request named an old epoch. A refusal changes nothing on the host, so it
        /// settles the request and records the owner and epoch it names.
        /// </summary>
        public void OnRefused(uint epoch, NetOwner owner)
        {
            Record(epoch, owner);
            Settle(false, 0);
        }

        /// <summary>
        /// The host announced a new owner and epoch. It settles no request, even one naming this client: only answers do,
        /// because an announcement may come before its answer, after it, or after later answers. An epoch newer than the
        /// confirmed hold's means the body has left this client (a reclaim, a steal, or the server taking a hand-back),
        /// which stops local simulation; an older one is ignored.
        /// </summary>
        public void OnOwnerChanged(NetOwner owner, uint epoch)
        {
            Record(epoch, owner);
            EndSupersededHold();
            Predict();
        }

        /// <summary>
        /// Forgets every message no answer covers and the hold the host confirmed, because their answers will never come:
        /// the link closed, the client reconnected, or the session was replaced. The claim stops simulating and may grab
        /// again at once. It names its last known epoch until it hears a report, and takes the next one whatever its
        /// epoch, since a replaced session counts again.
        /// </summary>
        public void Reset()
        {
            unanswered.Clear();
            outstandingRequests = 0;
            confirmed = OwnershipState.Server;
            confirmedDeadlineMicros = 0;
            heard = false;
            if (KnownOwner.Equals(self)) KnownOwner = NetOwner.Server;
            Predict();
        }

        /// <summary>Whether <paramref name="epoch"/> comes before <paramref name="than"/>, counting across the wrap.</summary>
        private static bool IsOlder(uint epoch, uint than) => unchecked((int)(epoch - than)) < 0;

        /// <summary>Takes a report's owner and epoch, unless the claim has already heard a newer epoch.</summary>
        private void Record(uint epoch, NetOwner owner)
        {
            if (heard && IsOlder(epoch, Epoch)) return;
            Epoch = epoch;
            KnownOwner = owner;
            heard = true;
        }

        /// <summary>
        /// Settles the oldest request: the messages sent before it take effect, then the answer. With no answer left
        /// due, the messages after the last request take effect too, so the release window settles on the last answer.
        /// </summary>
        private void Settle(bool granted, uint epoch)
        {
            var index = 0;
            while (index < unanswered.Count && unanswered[index].Kind != MessageKind.Request) index++;
            if (index < unanswered.Count)
            {
                for (var applied = 0; applied < index; applied++) Apply(unanswered[applied]);
                unanswered.RemoveRange(0, index + 1);
                outstandingRequests--;
                if (granted)
                {
                    confirmed = OwnershipState.Owned;
                    confirmedEpoch = epoch;
                }
            }

            EndSupersededHold();
            ApplyIfNothingOutstanding();
            Predict();
        }

        /// <summary>A throw or hand-back takes effect at once unless an answer sent before it is still due.</summary>
        private void Send(Message message)
        {
            unanswered.Add(message);
            ApplyIfNothingOutstanding();
            Predict();
        }

        /// <summary>With no answer due, the messages after the last request take effect as the host applied them.</summary>
        private void ApplyIfNothingOutstanding()
        {
            if (outstandingRequests > 0) return;
            foreach (var message in unanswered) Apply(message);
            unanswered.Clear();
        }

        /// <summary>
        /// A throw or hand-back as the host applies it: only under the epoch the body is held by, and only while it is
        /// this client's. A throw opens the window if it is not open yet. A hand-back returns the body, and the host's
        /// epoch moves on by one, which a later grab names.
        /// </summary>
        private void Apply(Message message)
        {
            if (confirmed == OwnershipState.Server || message.Epoch != confirmedEpoch) return;
            if (message.Kind == MessageKind.Handback)
            {
                confirmed = OwnershipState.Server;
                var next = unchecked(confirmedEpoch + 1);
                if (IsOlder(Epoch, next))
                {
                    Epoch = next;
                    KnownOwner = NetOwner.Server;
                }
            }
            else if (message.Kind == MessageKind.Throw && confirmed == OwnershipState.Owned)
            {
                confirmed = OwnershipState.ReleaseWindow;
                confirmedDeadlineMicros = message.DeadlineMicros;
            }
        }

        /// <summary>A report of an epoch newer than the confirmed hold's means that hold is over.</summary>
        private void EndSupersededHold()
        {
            if (confirmed != OwnershipState.Server && IsOlder(confirmedEpoch, Epoch)) confirmed = OwnershipState.Server;
        }

        /// <summary>
        /// Replays the unanswered messages on the confirmed state. A request is predicted granted unless the claim has
        /// heard that the host moved past the epoch it names, other than by granting it to this client.
        /// </summary>
        private void Predict()
        {
            var state = confirmed;
            var epoch = confirmedEpoch;
            var deadline = confirmedDeadlineMicros;
            var pending = false;
            foreach (var message in unanswered)
            {
                switch (message.Kind)
                {
                    case MessageKind.Request:
                        if (state == OwnershipState.ReleaseWindow)
                        {
                            state = OwnershipState.Owned;
                        }
                        else if (state == OwnershipState.Server && MayBeGranted(message.Epoch))
                        {
                            state = OwnershipState.Owned;
                            epoch = unchecked(message.Epoch + 1);
                            pending = true;
                        }

                        break;
                    case MessageKind.Throw:
                        if (state == OwnershipState.Owned && message.Epoch == epoch)
                        {
                            state = OwnershipState.ReleaseWindow;
                            deadline = message.DeadlineMicros;
                        }

                        break;
                    default:
                        if (state != OwnershipState.Server && message.Epoch == epoch)
                        {
                            state = OwnershipState.Server;
                            pending = false;
                        }

                        break;
                }
            }

            GrantPending = pending;
            streamEpoch = state == OwnershipState.Server ? Epoch : epoch;
            State = state == OwnershipState.Owned && pending ? OwnershipState.Requested : state;
            if (state == OwnershipState.ReleaseWindow) ReleaseDeadlineMicros = deadline;
        }

        /// <summary>
        /// Whether a grab naming <paramref name="requestEpoch"/> can still be granted, as far as the reports say: the host
        /// is still at that epoch, or it has announced the grant itself.
        /// </summary>
        private bool MayBeGranted(uint requestEpoch) =>
            requestEpoch == Epoch || (KnownOwner.Equals(self) && Epoch == unchecked(requestEpoch + 1));

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
