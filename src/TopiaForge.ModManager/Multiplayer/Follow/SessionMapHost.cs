using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    // All traffic uses the authenticated SceneFollow stream; no endpoints or file names are accepted.
    internal sealed class SessionMapHost
    {
        internal const byte Chunk = 5, Refused = 6, Request = 32;
        internal const int ChunkBytes = 16 * 1024;
        private readonly Func<string, ReadOnlyMemory<byte>> copy;
        private readonly Func<ParticipantId, bool> joining;
        private readonly Dictionary<ParticipantId, Transfer> transfers = new Dictionary<ParticipantId, Transfer>();
        private readonly List<ParticipantId> retired = new List<ParticipantId>();
        private readonly WorldWireReader reader = new WorldWireReader();
        private readonly WorldWireWriter writer = new WorldWireWriter(ChunkBytes + 256);

        internal SessionMapHost(Func<string, ReadOnlyMemory<byte>> copy, Func<ParticipantId, bool> joining)
        { this.copy = copy; this.joining = joining; }

        internal void Clear() => transfers.Clear();

        internal bool Receive(IInternalSystemChannel channel, HostWorldState state, InternalSystemMessage message)
        {
            var payload = message.Payload;
            if (payload.Length > 256) return false;
            reader.Reset(payload);
            if (!reader.TryReadByte(out var version) || version != 1 || !reader.TryReadByte(out var type) || type != Request
                || !reader.TryReadString(32, out var session) || !SessionFollowWire.IsSessionId(session)
                || !reader.TryReadString(32, out var request) || !SessionFollowWire.IsSessionId(request)
                || !reader.TryReadString(64, out var hash) || !reader.AtEnd) return false;
            var generation = channel.GetGeneration(message.Sender);
            if (generation == 0 || !joining(message.Sender)) return false;
            if (transfers.TryGetValue(message.Sender, out var prior) && prior.Generation == generation) return false;
            var bytes = state.SessionId == session && state.Map?.Sha256 == hash ? copy(session) : ReadOnlyMemory<byte>.Empty;
            if (state.Map == null || bytes.Length != state.Map.Size || bytes.Length > InternalMapIdentity.MaximumTransferBytes)
                bytes = ReadOnlyMemory<byte>.Empty;
            transfers[message.Sender] = new Transfer(generation, session, request, bytes);
            return true;
        }

        internal void Pump(IInternalSystemChannel channel, HostWorldState state)
        {
            retired.Clear();
            foreach (var pair in transfers)
            {
                var transfer = pair.Value;
                if (channel.GetGeneration(pair.Key) != transfer.Generation)
                { retired.Add(pair.Key); continue; }
                if (transfer.Done) continue;
                if (!joining(pair.Key) || state.SessionId != transfer.Session
                    || (!transfer.Bytes.IsEmpty && copy(transfer.Session).IsEmpty))
                { transfer.Finish(); continue; }
                writer.Begin(transfer.Bytes.IsEmpty ? Refused : Chunk);
                writer.WriteString(transfer.RequestId);
                var count = Math.Min(ChunkBytes, transfer.Bytes.Length - transfer.Offset);
                if (!transfer.Bytes.IsEmpty)
                {
                    writer.WriteVarUInt((ulong)transfer.Offset);
                    writer.WriteBytes(transfer.Bytes.Span.Slice(transfer.Offset, count));
                }
                var target = InternalSystemTarget.Only(pair.Key);
                // One chunk per peer per frame, only when this channel has credit. Retry the same offset on backpressure.
                if (!channel.CanSend(target, writer.Length) || !channel.Send(writer.Segment, target).TryGetValue(out var sent) || sent != 1) continue;
                transfer.Offset += count;
                if (transfer.Offset == transfer.Bytes.Length) transfer.Finish();
            }
            foreach (var peer in retired) transfers.Remove(peer);
        }

        private sealed class Transfer
        {
            internal Transfer(ulong generation, string session, string request, ReadOnlyMemory<byte> bytes)
            { Generation = generation; Session = session; RequestId = request; Bytes = bytes; }
            internal readonly ulong Generation;
            internal readonly string Session, RequestId;
            internal ReadOnlyMemory<byte> Bytes;
            internal int Offset;
            internal bool Done;
            internal void Finish() { Done = true; Bytes = ReadOnlyMemory<byte>.Empty; }
        }
    }
}
