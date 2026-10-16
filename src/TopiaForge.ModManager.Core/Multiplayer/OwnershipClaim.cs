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
    /// and announcements reach the claim in the order the host sent them, on one ordered channel. Every request carries
    /// its number, and the host answers each request it receives exactly once, as it handles it, echoing that number,
    /// with <see cref="OnGranted"/> or <see cref="OnRefused"/>. It announces every change of owner with
    /// <see cref="OnOwnerChanged"/> at any later point, or only the newest owner of a frame, and may leave a requester's
    /// own grant to its answer. Only an answer settles a request, the one it names: the throws and hand-backs sent
    /// before it take effect first, as the host applied them, epoch checks included, and any request before it, which
    /// no answer named, never reached the host. An announcement records the owner and epoch and settles nothing. The
    /// claim never takes an epoch older than one it has heard, and any newer epoch ends the hold it had confirmed.
    /// </para>
    /// <para>
    /// The channel may drop a request without telling this client, so <see cref="Poll"/> takes a request unanswered for
    /// the answer timeout as lost. An answer that arrives for a request the claim no longer holds is recorded like an
    /// announcement, and a grant among them is taken as a hold the host confirmed. When the order breaks, because the
    /// link closed, the client reconnected, the session was replaced or this client's own channel dropped a message,
    /// the runtime calls <see cref="Reset"/>.
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
        private uint nextRequestNumber;
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
        /// the first report after construction or a <see cref="Reset"/> across links or sessions is taken whatever its
        /// epoch.
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

        /// <summary>Why the claim last stopped waiting for something it had sent, for diagnostics.</summary>
        public OwnershipClaimRecovery LastRecovery { get; private set; }

        /// <summary>How many requests the claim has taken as lost: unanswered for the answer timeout, or skipped.</summary>
        public int LostRequests { get; private set; }

        /// <summary>
        /// The local player grabs. Returns true when <paramref name="request"/> must be sent, naming its epoch and
        /// number. Grabbing one's own throw during its release window takes it back under the epoch it is already owned
        /// by, while at most <see cref="MaximumOutstandingRequests"/> answers are due. A grab of a body this client does
        /// not hold is simulated at once, whoever holds it now: the host grants a held body only once its owner has
        /// thrown it, which the client cannot see, and a steal's first sample must reach the host before the
        /// first-sample deadline. Such a grab is not sent while an answer is due, since that answer may change the epoch
        /// it must name.
        /// </summary>
        public bool Grab(long nowMicros, out OwnershipRequest request)
        {
            RequireTime(nowMicros);
            request = default;
            var fresh = State == OwnershipState.Server;
            if (fresh ? outstandingRequests > 0 : State != OwnershipState.ReleaseWindow) return false;
            if (outstandingRequests >= MaximumOutstandingRequests) return false;
            request = new OwnershipRequest(nextRequestNumber, fresh ? Epoch : streamEpoch);
            nextRequestNumber = unchecked(nextRequestNumber + 1);
            unanswered.Add(new Message(MessageKind.Request, request.Epoch, nowMicros, request.Number));
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
            RequireTime(nowMicros);
            if (State != OwnershipState.Owned && State != OwnershipState.Requested) return false;
            Send(new Message(MessageKind.Throw, streamEpoch, nowMicros + policy.ReleaseWindowMicros, 0));
            return true;
        }

        public bool IsHandbackDue(long nowMicros) =>
            State == OwnershipState.ReleaseWindow && nowMicros >= ReleaseDeadlineMicros;

        /// <summary>Stops simulating once the window has closed; send the hand-back under <paramref name="epoch"/>.</summary>
        public bool TryCompleteHandback(long nowMicros, out uint epoch)
        {
            epoch = streamEpoch;
            if (!IsHandbackDue(nowMicros)) return false;
            Send(new Message(MessageKind.Handback, epoch, 0, 0));
            return true;
        }

        /// <summary>
        /// The host's answer to request <paramref name="requestNumber"/>: it granted it, or reported the body already
        /// this client's, under <paramref name="epoch"/>.
        /// </summary>
        public void OnGranted(uint requestNumber, uint epoch)
        {
            var newest = Record(epoch, self);
            var index = IndexOfRequest(requestNumber);
            if (index >= 0)
            {
                SettleThrough(index, true, epoch);
                return;
            }

            // A request the claim gave up on was granted after all: the host holds the body for this client now.
            if (newest && (confirmed == OwnershipState.Server || IsOlder(confirmedEpoch, epoch)))
            {
                confirmed = OwnershipState.Owned;
                confirmedEpoch = epoch;
            }

            Predict();
        }

        /// <summary>
        /// The host's answer to request <paramref name="requestNumber"/>: another participant holds the body, the
        /// requester is out of range, yielding or limited, or the request named an old epoch. A refusal changes nothing
        /// on the host, so it settles the request and records the owner and epoch it names.
        /// </summary>
        public void OnRefused(uint requestNumber, uint epoch, NetOwner owner)
        {
            Record(epoch, owner);
            var index = IndexOfRequest(requestNumber);
            if (index >= 0)
            {
                SettleThrough(index, false, 0);
                return;
            }

            EndSupersededHold();
            Predict();
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
        /// Takes every request unanswered for longer than the answer timeout as lost, oldest first, as if a later answer
        /// had skipped it: the throws and hand-backs sent before it take effect as the host applied them, and a lost grab
        /// stops simulating. The channel can drop a request without telling this client, and without this the claim
        /// would wait for its answer forever. Call it once a frame. Returns whether a request was lost.
        /// </summary>
        public bool Poll(long nowMicros)
        {
            RequireTime(nowMicros);
            var lost = false;
            while (outstandingRequests > 0)
            {
                var index = IndexOfRequest(null);
                if (nowMicros - unanswered[index].TimeMicros <= policy.AnswerTimeoutMicros) break;
                RemoveThrough(index);
                LostRequests++;
                LastRecovery = OwnershipClaimRecovery.AnswerTimeout;
                lost = true;
            }

            if (lost)
            {
                EndSupersededHold();
                ApplyIfNothingOutstanding();
                Predict();
            }

            return lost;
        }

        /// <summary>
        /// Forgets every message no answer covers and the hold the host confirmed, because the order of what arrives no
        /// longer says what the host did: the link closed, the client reconnected, the session was replaced, or this
        /// client's own channel dropped a message from the host. The claim stops simulating and may grab again at once,
        /// naming its last known epoch until it hears a report. Across links and sessions it takes the next report
        /// whatever its epoch, since a replaced session counts again; after a drop within the session it keeps refusing
        /// older ones. Request numbers count on, so a late answer to a forgotten request settles nothing.
        /// </summary>
        public void Reset(OwnershipClaimRecovery reason)
        {
            if (reason < OwnershipClaimRecovery.LinkClosed || reason > OwnershipClaimRecovery.ChannelDropped)
            {
                throw new ArgumentOutOfRangeException(nameof(reason));
            }

            unanswered.Clear();
            outstandingRequests = 0;
            confirmed = OwnershipState.Server;
            confirmedDeadlineMicros = 0;
            if (reason != OwnershipClaimRecovery.ChannelDropped) heard = false;
            if (KnownOwner.Equals(self)) KnownOwner = NetOwner.Server;
            LastRecovery = reason;
            Predict();
        }

        /// <summary>Whether <paramref name="epoch"/> comes before <paramref name="than"/>, counting across the wrap.</summary>
        private static bool IsOlder(uint epoch, uint than) => unchecked((int)(epoch - than)) < 0;

        private static void RequireTime(long nowMicros)
        {
            if (nowMicros < 0) throw new ArgumentOutOfRangeException(nameof(nowMicros));
        }

        /// <summary>
        /// Takes a report's owner and epoch, unless the claim has already heard a newer epoch. Returns whether it did.
        /// </summary>
        private bool Record(uint epoch, NetOwner owner)
        {
            if (heard && IsOlder(epoch, Epoch)) return false;
            Epoch = epoch;
            KnownOwner = owner;
            heard = true;
            return true;
        }

        /// <summary>The index of the request with this number, or of the oldest request when the number is null; else -1.</summary>
        private int IndexOfRequest(uint? number)
        {
            for (var index = 0; index < unanswered.Count; index++)
            {
                var message = unanswered[index];
                if (message.Kind == MessageKind.Request && (number == null || message.Number == number.Value)) return index;
            }

            return -1;
        }

        /// <summary>
        /// Settles the request at <paramref name="index"/> with its answer: the messages sent before it take effect, and
        /// any request among them, which no answer named, never reached the host. With no answer left due, the messages
        /// after the last request take effect too, so the release window settles on the last answer.
        /// </summary>
        private void SettleThrough(int index, bool granted, uint epoch)
        {
            RemoveThrough(index);
            if (granted)
            {
                confirmed = OwnershipState.Owned;
                confirmedEpoch = epoch;
            }

            EndSupersededHold();
            ApplyIfNothingOutstanding();
            Predict();
        }

        /// <summary>
        /// Removes the messages up to and including the request at <paramref name="index"/>. The throws and hand-backs
        /// before it take effect as the host applied them, and the requests before it, which no answer named, are lost.
        /// </summary>
        private void RemoveThrough(int index)
        {
            for (var position = 0; position < index; position++)
            {
                var message = unanswered[position];
                if (message.Kind != MessageKind.Request)
                {
                    Apply(message);
                    continue;
                }

                outstandingRequests--;
                LostRequests++;
                LastRecovery = OwnershipClaimRecovery.AnswerSkipped;
            }

            unanswered.RemoveRange(0, index + 1);
            outstandingRequests--;
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
                confirmedDeadlineMicros = message.TimeMicros;
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
                            deadline = message.TimeMicros;
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

        /// <summary>
        /// A message sent to the host under an epoch: a request with its number and the time it was sent, a throw with
        /// its release deadline, or a hand-back.
        /// </summary>
        private readonly struct Message
        {
            public Message(MessageKind kind, uint epoch, long timeMicros, uint number)
            {
                Kind = kind;
                Epoch = epoch;
                TimeMicros = timeMicros;
                Number = number;
            }

            public MessageKind Kind { get; }

            public uint Epoch { get; }

            /// <summary>When a request was sent, or when a throw's release window closes.</summary>
            public long TimeMicros { get; }

            public uint Number { get; }
        }
    }
}
