using System;
using System.Collections.Generic;
using TopiaForge.Multiplayer.Transport.Packets;
using TopiaForge.Multiplayer.Transport.Paths;
using TopiaForge.Multiplayer.Transport.Recovery;

namespace TopiaForge.Multiplayer.Transport
{
    /// <summary>
    /// Paths. A connection moves to a new path only once it has validated it: when the peer's newest packets come from
    /// elsewhere, as when its NAT rebinds, or when this side asks to try a path, as when a relayed connection finds a
    /// direct one. Until the response comes back, everything else still goes out on the old path.
    /// </summary>
    internal sealed partial class TransportConnection
    {
        private const int MaximumPendingResponses = 4;

        private readonly Queue<PendingResponse> pathResponses = new Queue<PendingResponse>();
        private readonly List<PathCandidate> dueChallenges = new List<PathCandidate>(PathValidator.MaximumCandidates);
        private bool answeredThisDatagram;

        /// <summary>Gets how many times the connection moved to a validated path.</summary>
        internal int PathChanges { get; private set; }

        /// <summary>Gets whether a path is being validated.</summary>
        internal bool IsValidatingPath => validator.CandidateCount > 0;

        /// <summary>
        /// Tries <paramref name="path"/>, as when a relayed connection finds a direct one: the connection moves there once
        /// the peer answers, and stays put otherwise. Returns false when the path is current or already being tried, the
        /// endpoint's privacy setting refuses it, or the connection is not established.
        /// </summary>
        internal bool ProbePath(TransportPath path, long time)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            now = time;
            if (State != TransportConnectionState.Established || path.Equals(Path) || !endpoint.Permits(path)) return false;
            return validator.TryStart(path, PathValidationReason.LocalProbe, now, ValidationInterval);
        }

        /// <summary>Gets how long a challenge waits before the next: a probe timeout, and at least the handshake's 250 ms.</summary>
        private long ValidationInterval => Math.Max(rtt.ProbeTimeout(options.MaximumAckDelayMicros), options.InitialRetransmitMicros);

        void IFrameSink.OnPathChallenge(byte[] data)
        {
            // The answer goes back on the path the challenge came in on, never larger than the datagram that carried it.
            // One datagram earns one answer however many challenges it packs, so the answers to a datagram never
            // outweigh it; a correct peer sends one challenge per packet.
            if (receivingFrom == null || answeredThisDatagram || pathResponses.Count >= MaximumPendingResponses) return;
            pathResponses.Enqueue(new PendingResponse(receivingFrom, data, receivingSize));
            answeredThisDatagram = true;
        }

        void IFrameSink.OnPathResponse(byte[] data)
        {
            var validated = validator.OnResponse(data);
            if (validated != null && !validated.Path.Equals(Path)) MoveTo(validated);
        }

        /// <summary>Notes the path the peer's newest packet with more than probing frames came from.</summary>
        private void OnPeerUsing(TransportPath from)
        {
            if (from.Equals(Path))
            {
                // Back on the current path: a move that was being checked no longer matters.
                validator.Cancel(PathValidationReason.PeerMigration);
                return;
            }

            if (State == TransportConnectionState.Established || State == TransportConnectionState.Closing)
            {
                validator.TryStart(from, PathValidationReason.PeerMigration, now, ValidationInterval);
            }
        }

        private void MoveTo(PathCandidate candidate)
        {
            Path = candidate.Path;
            PathChanges++;

            // The new path's round trip may differ entirely, so recovery starts again from the validation's own.
            rtt = new RttEstimator(options.InitialRttMicros);
            if (candidate.Sends == 1) rtt.OnSample(now - candidate.LastSentAt, 0, options.MaximumAckDelayMicros);
            validator.Clear();
            endpoint.Report(TransportDiagnosticKind.PathChanged, Number, "The connection moved to a validated path.");
        }

        private void PollPaths()
        {
            validator.Poll(now, dueChallenges);
            foreach (var candidate in dueChallenges) SendPathChallenge(candidate);
            while (pathResponses.Count > 0) SendPathResponse(pathResponses.Dequeue());
        }

        private void ClearPaths()
        {
            validator.Clear();
            pathResponses.Clear();
        }

        /// <summary>Sends a challenge padded to a full datagram, which also proves the path carries 1,200 bytes.</summary>
        private void SendPathChallenge(PathCandidate candidate)
        {
            var writer = BeginPacket();
            FrameWriter.WritePathFrame(writer, FrameType.PathChallenge, candidate.Data);
            writer.WriteZeros(writer.Remaining);
            Seal(writer.Written, candidate.Path);
            candidate.OnSent(now);
        }

        private void SendPathResponse(PendingResponse response)
        {
            var writer = BeginPacket();
            FrameWriter.WritePathFrame(writer, FrameType.PathResponse, response.Data);
            var frames = Math.Min(ProtectedPacket.MaximumFramesBytes, response.ReceivedSize - ProtectedPacket.Overhead);
            if (frames > writer.Written) writer.WriteZeros(frames - writer.Written);
            Seal(writer.Written, response.Path);
        }

        private readonly struct PendingResponse
        {
            internal PendingResponse(TransportPath path, byte[] data, int receivedSize)
            {
                Path = path;
                Data = data;
                ReceivedSize = receivedSize;
            }

            internal TransportPath Path { get; }

            internal byte[] Data { get; }

            internal int ReceivedSize { get; }
        }
    }
}
