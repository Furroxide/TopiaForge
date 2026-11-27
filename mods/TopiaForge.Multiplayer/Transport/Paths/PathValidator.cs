using System.Collections.Generic;
using TopiaForge.Multiplayer.Transport.Packets;

namespace TopiaForge.Multiplayer.Transport.Paths
{
    internal enum PathValidationReason
    {
        /// <summary>The peer's newest packets came from another path: it moved, as when its NAT rebinds.</summary>
        PeerMigration = 0,

        /// <summary>This side asked to try a path, as when a relayed connection finds a direct one.</summary>
        LocalProbe = 1
    }

    /// <summary>
    /// Validates candidate paths as QUIC does. A PATH_CHALLENGE carrying eight random bytes goes out on the candidate,
    /// and a PATH_RESPONSE echoing them, arriving on any path, proves the peer receives there. At most two candidates
    /// are tried at once, each with up to three challenges, after which it is abandoned.
    /// </summary>
    internal sealed class PathValidator
    {
        internal const int MaximumCandidates = 2;
        internal const int ChallengesPerCandidate = 3;

        private readonly List<PathCandidate> candidates = new List<PathCandidate>(MaximumCandidates);
        private readonly ITransportRandom random;

        internal PathValidator(ITransportRandom random)
        {
            this.random = random;
        }

        internal int CandidateCount => candidates.Count;

        internal bool IsValidating(TransportPath path) => IndexOf(path) >= 0;

        /// <summary>Starts trying a path; false when it is already a candidate or two are being tried.</summary>
        internal bool TryStart(TransportPath path, PathValidationReason reason, long now, long interval)
        {
            if (IndexOf(path) >= 0 || candidates.Count >= MaximumCandidates) return false;
            var data = new byte[FrameType.PathDataSize];
            random.Fill(data);
            candidates.Add(new PathCandidate(path, reason, data, now, interval));
            return true;
        }

        /// <summary>Abandons every candidate started for <paramref name="reason"/>.</summary>
        internal void Cancel(PathValidationReason reason) => candidates.RemoveAll(candidate => candidate.Reason == reason);

        internal void Clear() => candidates.Clear();

        /// <summary>Matches a response to a challenge; returns the candidate it validates, or null.</summary>
        internal PathCandidate? OnResponse(byte[] data)
        {
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                if (candidate.Sends == 0 || !FixedTimeEquals(candidate.Data, data)) continue;
                candidates.RemoveAt(index);
                return candidate;
            }

            return null;
        }

        /// <summary>Collects the challenges due now and drops the candidates that used up their attempts.</summary>
        internal void Poll(long now, List<PathCandidate> due)
        {
            due.Clear();
            for (var index = candidates.Count - 1; index >= 0; index--)
            {
                var candidate = candidates[index];
                if (now < candidate.NextSendAt) continue;
                if (candidate.Sends >= ChallengesPerCandidate)
                {
                    candidates.RemoveAt(index);
                    continue;
                }

                due.Add(candidate);
            }

            due.Reverse();
        }

        /// <summary>Gets when the next challenge or abandonment falls due, or <see cref="long.MaxValue"/>.</summary>
        internal long NextTimeout()
        {
            var next = long.MaxValue;
            foreach (var candidate in candidates)
            {
                if (candidate.NextSendAt < next) next = candidate.NextSendAt;
            }

            return next;
        }

        private int IndexOf(TransportPath path)
        {
            for (var index = 0; index < candidates.Count; index++)
            {
                if (candidates[index].Path.Equals(path)) return index;
            }

            return -1;
        }

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            var difference = 0;
            for (var index = 0; index < left.Length; index++) difference |= left[index] ^ right[index];
            return difference == 0;
        }
    }

    /// <summary>One path being tried: its challenge, how often it went out, and when it goes next.</summary>
    internal sealed class PathCandidate
    {
        internal PathCandidate(TransportPath path, PathValidationReason reason, byte[] data, long now, long interval)
        {
            Path = path;
            Reason = reason;
            Data = data;
            Interval = interval;
            NextSendAt = now;
        }

        internal TransportPath Path { get; }

        internal PathValidationReason Reason { get; }

        internal byte[] Data { get; }

        internal long Interval { get; }

        internal int Sends { get; private set; }

        internal long LastSentAt { get; private set; }

        internal long NextSendAt { get; private set; }

        /// <summary>Records a challenge sent now; the next waits one interval, and the last one interval to fail.</summary>
        internal void OnSent(long now)
        {
            Sends++;
            LastSentAt = now;
            NextSendAt = now + Interval;
        }
    }
}
