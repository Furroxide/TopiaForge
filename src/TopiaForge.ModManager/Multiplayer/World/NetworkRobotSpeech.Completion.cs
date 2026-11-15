using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed partial class NetworkRobotSpeech
    {
        private void SendFinished(Playback operation, bool completed)
        {
            SendFinished(operation.Nonce, operation.WireId, completed ? RobotSpeechWire.Completed : RobotSpeechWire.Failed);
        }

        private bool Decline(ulong lineNonce, uint entityId)
        {
            // Separate from admission: a full receive budget must still be able to close a fresh line.
            // Bound replies too, without retaining text or creating playback, retry or per-line state.
            if (declines.TryTake(clock())) SendFinished(lineNonce, entityId, RobotSpeechWire.Declined);
            return true;
        }

        private void SendFinished(ulong lineNonce, uint entityId, byte status)
        {
            if (!readySent || generation != world.Channel.Generation || instance == 0) return;
            RobotSpeechWire.Header(writer, RobotSpeechWire.Finished, instance, challenge, lineNonce, entityId);
            writer.WriteByte(status);
            // No retry queue: a full window causes a bounded failure at the host's existing deadline.
            world.Channel.TrySendToHost(writer);
        }

        private bool ReceiveFinished(ParticipantId sender, WorldWireReader reader, ulong messageWorld,
            string messageChallenge, ulong lineNonce, uint entityId)
        {
            if (!reader.TryReadByte(out var status) || status > RobotSpeechWire.Declined || !reader.AtEnd
                || realm != NetRealm.ListenHost || messageWorld != instance || lineNonce == 0 || entityId == 0
                || services.Runtime.Control?.SessionSnapshot.TryGetParticipant(sender, out var peer) != true
                || peer == null || peer.IsLocal || !peer.IsConnected) return false;
            foreach (var operation in active)
            {
                if (operation.Nonce != lineNonce || operation.WireId != entityId
                    || operation.CancellationRequested || !operation.Current()) continue;
                for (var i = 0; i < operation.Recipients.Count; i++)
                {
                    var recipient = operation.Recipients[i];
                    if (!recipient.Id.Equals(sender) || recipient.Completed || recipient.Gate.World != messageWorld
                        || recipient.Gate.Challenge != messageChallenge
                        || recipient.Gate.Generation != world.Channel.GetGeneration(sender)
                        || !ready.TryGetValue(sender, out var gate) || gate.Challenge != messageChallenge
                        || gate.Generation != recipient.Gate.Generation || !Eligible(sender, operation.Entity, entityId)) continue;
                    if (status == RobotSpeechWire.Declined)
                    {
                        // A refusal before playback is terminal for this recipient only. Removing it also
                        // prevents later departure from failing a line it never accepted or counting as playback.
                        operation.Recipients.RemoveAt(i);
                        return true;
                    }
                    recipient.Completed = true;
                    recipient.Heard = status == RobotSpeechWire.Completed;
                    if (status == RobotSpeechWire.Failed) operation.Failure = OperationResult<bool>.Failure(ModErrorCode.Unavailable,
                        "A robot speech listener could not complete playback.");
                    return true;
                }
            }
            return false;
        }
    }
}
