using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Multiplayer.Engine
{
    internal sealed partial class SessionEngine
    {
        private CommunicationLease? communication;
        private byte[] routedVoice = Array.Empty<byte>();

        internal bool HasCommunicationRouting
        {
            get { RequireOwnerThread(); RefreshCommunication(); return communication != null; }
        }

        internal OperationResult<ICommunicationRouting> RouteCommunications(IModLifetime lifetime, CommunicationPolicy policy)
        {
            RequireOwnerThread();
            if (lifetime == null) throw new ArgumentNullException(nameof(lifetime));
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            if (Role != EngineRole.Host)
                return OperationResult<ICommunicationRouting>.Failure(ModErrorCode.NotAuthoritative, "Only a host may route communications.");
            if (IsEnded || lifetime.IsStopping)
                return OperationResult<ICommunicationRouting>.Failure(ModErrorCode.Cancelled, "The communication scope has ended.");
            RefreshCommunication();
            if (communication != null)
                return OperationResult<ICommunicationRouting>.Failure(ModErrorCode.Conflict, "Communication routing is already owned.");
            var lease = new CommunicationLease(this, lifetime, policy);
            communication = lease;
            try { lease.Track(); }
            catch { if (ReferenceEquals(communication, lease)) communication = null; throw; }
            return OperationResult<ICommunicationRouting>.Success(lease);
        }

        internal bool CanCommunicate(ParticipantId sender, ParticipantId listener)
        {
            RequireOwnerThread();
            if (Role != EngineRole.Host || IsEnded) return false;
            RefreshCommunication();
            if (communication == null) return true;
            if (!IsParticipantReady(sender) || !IsParticipantReady(listener)) return false;
            if (!communication.Policy.TryGetDistance(sender, listener, out var distance)) return false;
            if (distance == 0) return true;
            if (!(avatarSource is IInternalCommunicationPositionSource positions)) return false;
            // Source failures deny delivery without exposing exception text or untrusted coordinates in diagnostics.
            try
            {
                if (!positions.TryGetCommunicationPosition(sender, out var from) || !positions.TryGetCommunicationPosition(listener, out var to)) return false;
                var x = (double)from.X - to.X;
                var y = (double)from.Y - to.Y;
                var z = (double)from.Z - to.Z;
                return x * x + y * y + z * z <= (double)distance * distance;
            }
            catch (Exception) { return false; }
        }

        private void RefreshCommunication()
        {
            if (communication != null && !communication.IsCurrent) RetireCommunication();
        }

        private void RetireCommunication()
        {
            var retiring = communication;
            communication = null;
            retiring?.Dispose();
        }

        private bool IsGlobalCommunication(ParticipantId sender, ParticipantId listener) => communication != null
            && communication.Policy.TryGetDistance(sender, listener, out var distance) && distance == 0;

        private ArraySegment<byte> RouteVoicePresentation(ParticipantId sender, ParticipantId listener, ArraySegment<byte> datagram)
        {
            if (!IsGlobalCommunication(sender, listener)) return datagram;
            if (!VoicePacket.TryReadRelayed(datagram.AsSpan(), options.Voice!.MaximumPacketBytes, out var packet)) return datagram;
            if (routedVoice.Length == 0) routedVoice = new byte[voiceScratch.Length];
            var length = VoicePacket.WriteRelayed(routedVoice, packet.First.Speaker, packet.First.Stream,
                packet.First.Sequence, packet.First.Timestamp, packet.First.Flags | VoiceFrameFlags.NonSpatial,
                datagram.AsSpan().Slice(packet.First.PayloadOffset, packet.First.PayloadLength),
                packet.Second.Flags | VoiceFrameFlags.NonSpatial,
                packet.Count == 2 ? datagram.AsSpan().Slice(packet.Second.PayloadOffset, packet.Second.PayloadLength) : ReadOnlySpan<byte>.Empty, packet.Epoch);
            return new ArraySegment<byte>(routedVoice, 0, length);
        }

        private sealed class CommunicationLease : ICommunicationRouting
        {
            private readonly SessionEngine engine;
            private readonly IModLifetime lifetime;
            private readonly MultiplayerSessionId session;
            private volatile bool disposed;
            private IDisposable? tracking;

            internal CommunicationLease(SessionEngine engine, IModLifetime lifetime, CommunicationPolicy policy)
            { this.engine = engine; this.lifetime = lifetime; session = engine.CurrentSessionId; Policy = policy; }

            internal CommunicationPolicy Policy { get; private set; }
            internal bool IsCurrent => !disposed && !lifetime.IsStopping && !engine.IsEnded
                && engine.Role == EngineRole.Host && session.Equals(engine.CurrentSessionId) && ReferenceEquals(engine.communication, this);

            internal void Track()
            {
                var registration = lifetime.Track(this);
                if (disposed) registration.Dispose();
                else tracking = registration;
            }

            public OperationResult<bool> SetPolicy(CommunicationPolicy policy)
            {
                engine.RequireOwnerThread();
                if (policy == null) throw new ArgumentNullException(nameof(policy));
                if (!IsCurrent) return OperationResult<bool>.Failure(ModErrorCode.Cancelled, "The communication scope has ended.");
                Policy = policy;
                return OperationResult<bool>.Success(true);
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                var registration = tracking;
                tracking = null;
                try { registration?.Dispose(); }
                finally { engine.Post(() => { if (ReferenceEquals(engine.communication, this)) engine.communication = null; }); }
            }
        }
    }
}
