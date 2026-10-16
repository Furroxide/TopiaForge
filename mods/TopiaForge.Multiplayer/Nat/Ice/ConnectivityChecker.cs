using System;
using System.Collections.Generic;
using System.Net;
using TopiaForge.Multiplayer.NatTraversal.Stun;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.NatTraversal.Ice
{
    /// <summary>
    /// Connectivity checks for one remote side, ICE-lite on the shared socket: pairs of this side's bases and the remote
    /// candidates, each checked with a Binding request that carries the remote's username fragment and MESSAGE-INTEGRITY
    /// under the remote's password (RFC 8445, section 7.2). Ordinary checks go out one every 50 ms, relayed pairs first so
    /// the session can start on the relay quickly, then direct pairs by priority. A check arriving from the remote
    /// triggers one back on its pair at once, which is what opens both NATs when the two sides punch together. Nomination
    /// is left to the transport: the route keeper hands a succeeded pair to its path validation. Pairs the session uses
    /// keep consent fresh (RFC 7675): a check every 15 seconds, give or take a fifth, and consent lapses 30 seconds after
    /// the last answer.
    /// </summary>
    internal sealed class ConnectivityChecker : IStunTransactionOwner
    {
        internal const int MaximumPairs = 64;
        internal const int MaximumRemoteCandidates = 24;
        internal const int MaximumPeerReflexive = 8;
        internal const long PacingMicros = 50_000;
        internal const long ConsentIntervalMicros = 15_000_000;
        internal const long ConsentLifetimeMicros = 30_000_000;

        /// <summary>Gets how long a remote relay candidate the remote replaced stays checkable before it is forgotten.</summary>
        internal const long SupersededGraceMicros = 60_000_000;

        private readonly StunClient stun;
        private readonly ITransportRandom random;
        private readonly Func<LocalBase, IPEndPoint, TransportPath?> pathFor;
        private readonly List<LocalBase> bases = new List<LocalBase>();
        private readonly List<IceCandidate> remotes = new List<IceCandidate>();
        private readonly List<CandidatePair> pairs = new List<CandidatePair>();
        private readonly StunMessageWriter writer = new StunMessageWriter();
        private readonly byte[] buffer = new byte[StunMessage.MaximumBytes];
        private readonly byte[] jitter = new byte[1];
        private readonly Dictionary<IceCandidate, long> superseded = new Dictionary<IceCandidate, long>();
        private readonly List<IceCandidate> forgetting = new List<IceCandidate>();
        private long nextCheckAt;
        private int peerReflexive;
        private int order;

        /// <param name="pathFor">Gives the path from a base to a remote address, or null when that base cannot reach it.</param>
        internal ConnectivityChecker(IceCredentials local, bool controlling, ulong tieBreaker, StunClient stun, ITransportRandom random, Func<LocalBase, IPEndPoint, TransportPath?> pathFor)
        {
            Local = local ?? throw new ArgumentNullException(nameof(local));
            Controlling = controlling;
            TieBreaker = tieBreaker;
            this.stun = stun ?? throw new ArgumentNullException(nameof(stun));
            this.random = random ?? throw new ArgumentNullException(nameof(random));
            this.pathFor = pathFor ?? throw new ArgumentNullException(nameof(pathFor));
        }

        internal IceCredentials Local { get; }

        internal IceCredentials? Remote { get; private set; }

        internal bool Controlling { get; }

        internal ulong TieBreaker { get; }

        /// <summary>Gets whether ordinary checks have begun: the punch arrived, or its wait ran out.</summary>
        internal bool Started { get; private set; }

        internal IReadOnlyList<CandidatePair> Pairs => pairs;

        internal IReadOnlyList<IceCandidate> RemoteCandidates => remotes;

        internal int ChecksSent { get; private set; }

        internal void SetRemote(IceCredentials remote)
        {
            if (Remote != null) return;
            Remote = remote ?? throw new ArgumentNullException(nameof(remote));
        }

        internal void AddBase(LocalBase local, long now)
        {
            if (local == null) throw new ArgumentNullException(nameof(local));
            if (bases.Contains(local)) return;
            bases.Add(local);
            foreach (var remote in remotes) Pair(local, remote);
        }

        /// <summary>Stops checking from <paramref name="local"/>; pairs on it stay readable for the route keeper.</summary>
        internal void RetireBase(LocalBase local)
        {
            if (local == null) return;
            local.Retiring = true;
            foreach (var pair in pairs)
            {
                if (!ReferenceEquals(pair.Local, local)) continue;
                stun.Cancel(pair.Check);
                stun.Cancel(pair.ConsentCheck);
                pair.Check = null;
                pair.ConsentCheck = null;
                pair.ConsentWanted = false;
                if (pair.State != CandidatePairState.Succeeded) pair.State = CandidatePairState.Failed;
            }
        }

        /// <summary>
        /// Adds a signaled candidate; false when it repeats one, or the session holds as many as it may. One this side
        /// already learned from a check takes the kind the remote signals: a peer-reflexive address that is in fact the
        /// remote's new relay candidate replaces the remote's older relay candidates as a signaled one would.
        /// </summary>
        internal bool AddRemote(IceCandidate candidate, long now)
        {
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            var known = FindRemote(candidate.EndPoint);
            if (known != null)
            {
                if (known.Kind != IceCandidateKind.PeerReflexive || candidate.Kind == IceCandidateKind.PeerReflexive) return false;
                known.Promote(candidate.Kind, candidate.Priority);
                peerReflexive--;
                if (known.Kind == IceCandidateKind.Relayed) SupersedeRelays(known, now);
                return false;
            }

            if (remotes.Count >= MaximumRemoteCandidates) return false;
            var numbered = new IceCandidate(candidate.Kind, candidate.EndPoint, candidate.Priority, order++);

            // A new relay candidate means the remote moved to a fresh allocation: the older ones lapse after a grace.
            if (numbered.Kind == IceCandidateKind.Relayed) SupersedeRelays(numbered, now);
            remotes.Add(numbered);
            foreach (var local in bases) Pair(local, numbered);
            return true;
        }

        /// <summary>Gets whether the remote replaced <paramref name="remote"/> with a newer relay candidate: nothing should move onto it.</summary>
        internal bool IsSuperseded(IceCandidate remote) => superseded.ContainsKey(remote);

        /// <summary>Checks a pair again at once, as when it would not take a connection though it answered once.</summary>
        internal void Recheck(CandidatePair pair, long now)
        {
            if (pair == null || pair.Local.Retiring || pair.State == CandidatePairState.InProgress) return;
            stun.Cancel(pair.Check);
            pair.Check = null;
            pair.State = CandidatePairState.Waiting;
            pair.Triggered = true;
            if (nextCheckAt > now) nextCheckAt = now;
        }

        private void SupersedeRelays(IceCandidate newest, long now)
        {
            foreach (var older in remotes)
            {
                if (!ReferenceEquals(older, newest) && older.Kind == IceCandidateKind.Relayed && !superseded.ContainsKey(older)) superseded.Add(older, now);
            }
        }

        /// <summary>Forgets a base that was released: its pairs, and the peer-reflexive candidates only it had found.</summary>
        internal void RemoveBase(LocalBase local)
        {
            if (local == null || !bases.Remove(local)) return;
            for (var index = pairs.Count - 1; index >= 0; index--)
            {
                if (!ReferenceEquals(pairs[index].Local, local)) continue;
                CancelChecks(pairs[index]);
                pairs.RemoveAt(index);
            }

            ForgetUnpaired(IceCandidateKind.PeerReflexive);
        }

        /// <summary>Forgets superseded remote relay candidates once their grace has passed, unless a pair of theirs is in use.</summary>
        internal void Prune(long now, CandidatePair? inUse)
        {
            if (superseded.Count == 0) return;
            forgetting.Clear();
            foreach (var entry in superseded)
            {
                if (now - entry.Value >= SupersededGraceMicros && (inUse == null || !ReferenceEquals(inUse.Remote, entry.Key))) forgetting.Add(entry.Key);
            }

            foreach (var remote in forgetting)
            {
                superseded.Remove(remote);
                remotes.Remove(remote);
                for (var index = pairs.Count - 1; index >= 0; index--)
                {
                    if (!ReferenceEquals(pairs[index].Remote, remote)) continue;
                    CancelChecks(pairs[index]);
                    pairs.RemoveAt(index);
                }
            }

            forgetting.Clear();
        }

        /// <summary>Begins ordinary checks.</summary>
        internal void Start(long now)
        {
            if (Started) return;
            Started = true;
            nextCheckAt = now;
        }

        /// <summary>
        /// Takes a verified check request that arrived at <paramref name="arrival"/> from <paramref name="source"/>: an
        /// address the remote never signaled becomes a peer-reflexive candidate, and a check goes back on the pair at once.
        /// </summary>
        internal void OnRequest(LocalBase arrival, IPEndPoint source, uint priority, long now)
        {
            if (arrival == null || source == null || arrival.Retiring || !bases.Contains(arrival)) return;
            var remote = FindRemote(source);
            if (remote == null)
            {
                if (peerReflexive >= MaximumPeerReflexive || remotes.Count >= MaximumRemoteCandidates) return;
                // A peer-reflexive address is the remote's mapping toward this one base, so it pairs with that base only.
                remote = new IceCandidate(IceCandidateKind.PeerReflexive, source, priority, order++);
                remotes.Add(remote);
                peerReflexive++;
                Pair(arrival, remote);
            }

            var pair = Find(arrival, remote);
            if (pair == null || pair.State == CandidatePairState.Succeeded || pair.State == CandidatePairState.InProgress) return;
            pair.State = CandidatePairState.Waiting;
            pair.Triggered = true;
            if (nextCheckAt > now) nextCheckAt = now;
        }

        /// <summary>Marks whether a pair needs consent kept fresh; it must have succeeded first.</summary>
        internal void WantConsent(CandidatePair pair, bool wanted, long now)
        {
            if (pair == null || pair.ConsentWanted == wanted) return;
            if (wanted && pair.State != CandidatePairState.Succeeded) return;
            pair.ConsentWanted = wanted;
            if (!wanted)
            {
                stun.Cancel(pair.ConsentCheck);
                pair.ConsentCheck = null;
                return;
            }

            pair.ConsentExpiresAt = Math.Max(pair.ConsentExpiresAt, now + ConsentLifetimeMicros);
            pair.NextConsentAt = now + ConsentInterval();
        }

        internal void Poll(long now)
        {
            foreach (var pair in pairs)
            {
                if (!pair.ConsentWanted) continue;
                if (now >= pair.ConsentExpiresAt)
                {
                    // Consent lapsed: nothing goes to this pair any more until it is checked afresh.
                    pair.ConsentWanted = false;
                    stun.Cancel(pair.ConsentCheck);
                    pair.ConsentCheck = null;
                    pair.State = CandidatePairState.Failed;
                    continue;
                }

                if (pair.ConsentCheck == null && now >= pair.NextConsentAt)
                {
                    pair.NextConsentAt = now + ConsentInterval();
                    pair.ConsentCheck = Send(pair, true, now);
                }
            }

            if (Remote == null || now < nextCheckAt) return;
            var next = NextToCheck();
            if (next == null) return;
            next.Triggered = false;
            next.State = CandidatePairState.InProgress;
            next.Check = Send(next, false, now);
            if (next.Check == null) next.State = CandidatePairState.Failed;
            nextCheckAt = now + PacingMicros;
        }

        internal long NextTimeoutMicros()
        {
            var next = long.MaxValue;
            if (Remote != null && NextToCheck() != null) next = nextCheckAt;
            foreach (var pair in pairs)
            {
                if (!pair.ConsentWanted) continue;
                next = Math.Min(next, pair.ConsentExpiresAt);
                if (pair.ConsentCheck == null) next = Math.Min(next, pair.NextConsentAt);
            }

            return next;
        }

        /// <summary>Gets the pair whose transport path is <paramref name="path"/>, or null.</summary>
        internal CandidatePair? Find(TransportPath path)
        {
            foreach (var pair in pairs)
            {
                if (pair.Path.Equals(path)) return pair;
            }

            return null;
        }

        /// <summary>Gets whether any pair could still succeed: one is waiting, in progress or not yet started.</summary>
        internal bool HasPending
        {
            get
            {
                if (Remote == null) return true;
                foreach (var pair in pairs)
                {
                    if (pair.State == CandidatePairState.Waiting || pair.State == CandidatePairState.InProgress) return true;
                }

                return false;
            }
        }

        /// <summary>Cancels every check; the session is over.</summary>
        internal void Stop()
        {
            stun.CancelAll(this);
            foreach (var pair in pairs)
            {
                pair.Check = null;
                pair.ConsentCheck = null;
                pair.ConsentWanted = false;
            }
        }

        public bool Accepts(StunTransaction transaction, StunMessage response) =>
            Remote != null && response.HasFingerprint && response.HasIntegrity && response.VerifyIntegrity(Remote.Key);

        public void OnResponse(StunTransaction transaction, StunMessage response, long now)
        {
            var check = (Check)transaction.Tag!;
            var pair = check.Pair;
            if (check.Consent) pair.ConsentCheck = null;
            else pair.Check = null;
            if (response.Class != StunClass.Success || response.UnknownRequired.Count > 0)
            {
                if (!check.Consent) pair.State = CandidatePairState.Failed;
                return;
            }

            if (pair.State != CandidatePairState.Succeeded) pair.SucceededAt = now;
            pair.State = CandidatePairState.Succeeded;
            pair.ConsentExpiresAt = now + ConsentLifetimeMicros;
            pair.AnsweredAt = now;
        }

        public void OnTimeout(StunTransaction transaction, long now)
        {
            var check = (Check)transaction.Tag!;
            if (check.Consent)
            {
                // One unanswered consent check is not yet a lapse; the 30-second window decides.
                check.Pair.ConsentCheck = null;
                return;
            }

            check.Pair.Check = null;
            if (check.Pair.State == CandidatePairState.InProgress) check.Pair.State = CandidatePairState.Failed;
        }

        private StunTransaction? Send(CandidatePair pair, bool consent, long now)
        {
            var id = stun.NewTransactionId();
            writer.Begin(buffer, StunMethod.Binding, StunClass.Request, id);
            writer.AddText(StunAttribute.Username, Remote!.Ufrag + ":" + Local.Ufrag);
            writer.AddUInt32(StunAttribute.Priority, IceCandidate.ComputePriority(IceCandidateKind.PeerReflexive, (int)((pair.Local.Priority >> 8) & 0xFFFF)));
            writer.AddUInt64(Controlling ? StunAttribute.IceControlling : StunAttribute.IceControlled, TieBreaker);
            var length = writer.Finish(Remote.Key, true);
            ChecksSent++;
            return stun.Begin(pair.Path, buffer, length, id, StunTiming.Check, this, new Check(pair, consent), now);
        }

        private CandidatePair? NextToCheck()
        {
            CandidatePair? best = null;
            foreach (var pair in pairs)
            {
                if (pair.State != CandidatePairState.Waiting || pair.Local.Retiring) continue;
                if (!pair.Triggered && !Started) continue;
                if (best == null || Before(pair, best)) best = pair;
            }

            return best;
        }

        /// <summary>Triggered checks first; then relayed pairs, so the session can start; then the rest by priority.</summary>
        private static bool Before(CandidatePair candidate, CandidatePair best)
        {
            if (candidate.Triggered != best.Triggered) return candidate.Triggered;
            if (candidate.IsRelayed != best.IsRelayed) return candidate.IsRelayed;
            if (candidate.Priority != best.Priority) return candidate.Priority > best.Priority;
            return candidate.Order < best.Order;
        }

        private void Pair(LocalBase local, IceCandidate remote)
        {
            if (pairs.Count >= MaximumPairs || Find(local, remote) != null || local.Retiring) return;
            var path = pathFor(local, remote.EndPoint);
            if (path == null) return;
            var priority = Controlling ? IceCandidate.PairPriority(local.Priority, remote.Priority) : IceCandidate.PairPriority(remote.Priority, local.Priority);
            pairs.Add(new CandidatePair(local, remote, path, priority, pairs.Count) { State = CandidatePairState.Waiting });
        }

        private void CancelChecks(CandidatePair pair)
        {
            stun.Cancel(pair.Check);
            stun.Cancel(pair.ConsentCheck);
            pair.Check = null;
            pair.ConsentCheck = null;
            pair.ConsentWanted = false;
        }

        private void ForgetUnpaired(IceCandidateKind kind)
        {
            for (var index = remotes.Count - 1; index >= 0; index--)
            {
                var remote = remotes[index];
                if (remote.Kind != kind) continue;
                var paired = false;
                foreach (var pair in pairs) paired |= ReferenceEquals(pair.Remote, remote);
                if (paired) continue;
                remotes.RemoveAt(index);
                peerReflexive--;
            }
        }

        private CandidatePair? Find(LocalBase local, IceCandidate remote)
        {
            foreach (var pair in pairs)
            {
                if (ReferenceEquals(pair.Local, local) && ReferenceEquals(pair.Remote, remote)) return pair;
            }

            return null;
        }

        private IceCandidate? FindRemote(IPEndPoint endpoint)
        {
            foreach (var remote in remotes)
            {
                if (remote.SameAddress(endpoint)) return remote;
            }

            return null;
        }

        /// <summary>Fifteen seconds, give or take a fifth, so consent checks from many sessions never line up.</summary>
        private long ConsentInterval()
        {
            random.Fill(jitter);
            return (ConsentIntervalMicros * 4 / 5) + (ConsentIntervalMicros * 2 / 5 * jitter[0] / 255);
        }

        private sealed class Check
        {
            internal Check(CandidatePair pair, bool consent)
            {
                Pair = pair;
                Consent = consent;
            }

            internal CandidatePair Pair { get; }

            internal bool Consent { get; }
        }
    }
}
