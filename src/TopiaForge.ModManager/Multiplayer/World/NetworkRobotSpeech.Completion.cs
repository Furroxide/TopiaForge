using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed partial class NetworkRobotSpeech
    {
        private void SendFinished(Playback operation, bool completed)
        {
            if (!readySent || generation != world.Channel.Generation || instance == 0) return;
            RobotSpeechWire.Header(writer, RobotSpeechWire.Finished, instance, challenge, operation.Nonce, operation.WireId);
            writer.WriteByte(completed ? (byte)1 : (byte)0);
            // No retry queue: a full window causes a bounded failure at the host's existing deadline.
            world.Channel.TrySendToHost(writer);
        }

        private bool ReceiveFinished(ParticipantId sender, WorldWireReader reader, ulong messageWorld,
            string messageChallenge, ulong lineNonce, uint entityId)
        {
            if (!reader.TryReadByte(out var completed) || completed > 1 || !reader.AtEnd
                || realm != NetRealm.ListenHost || messageWorld != instance || lineNonce == 0 || entityId == 0
                || services.Runtime.Control?.SessionSnapshot.TryGetParticipant(sender, out var peer) != true
                || peer == null || peer.IsLocal || !peer.IsConnected) return false;
            foreach (var operation in active)
            {
                if (operation.Nonce != lineNonce || operation.WireId != entityId
                    || operation.Cancellation.IsCancellationRequested || !operation.Current()) continue;
                foreach (var recipient in operation.Recipients)
                {
                    if (!recipient.Id.Equals(sender) || recipient.Completed || recipient.Gate.World != messageWorld
                        || recipient.Gate.Challenge != messageChallenge
                        || recipient.Gate.Generation != world.Channel.GetGeneration(sender)
                        || !ready.TryGetValue(sender, out var gate) || gate.Challenge != messageChallenge
                        || gate.Generation != recipient.Gate.Generation || !Eligible(sender, operation.Entity)) continue;
                    recipient.Completed = true;
                    if (completed == 0) operation.Failure = OperationResult<bool>.Failure(ModErrorCode.Unavailable,
                        "A robot speech listener could not complete playback.");
                    return true;
                }
            }
            return false;
        }
    }
}
