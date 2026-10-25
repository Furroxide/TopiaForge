using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed partial class MultiplayerPlayers
    {
        private readonly Dictionary<ParticipantId, PlayerStream> playerStreams = new Dictionary<ParticipantId, PlayerStream>();
        private readonly List<ParticipantId> retiredPlayers = new List<ParticipantId>();

        private void ReconcilePlayers()
        {
            var snapshot = Snapshot;
            if (snapshot == null) return;
            retiredPlayers.Clear();
            foreach (var entry in playerStreams)
            {
                if (!snapshot.TryGetParticipant(entry.Key, out var player) || !player!.PlayerKey.Equals(entry.Value.Key) ||
                    entry.Value.Generation != (Channel?.GetGeneration(entry.Key) ?? 0)) retiredPlayers.Add(entry.Key);
            }
            for (var i = 0; i < retiredPlayers.Count; i++)
            {
                var id = retiredPlayers[i];
                playerStreams.Remove(id); inputBudgets.Remove(id); uiReplies.Remove(id); uiSends.Remove(id); scoreDeliveries.Remove(id);
                for (var c = 0; c < contexts.Count; c++) contexts[c].Forget(id);
            }
            for (var i = 0; i < snapshot.Participants.Count; i++)
            {
                var player = snapshot.Participants[i];
                if (!playerStreams.ContainsKey(player.Id))
                    playerStreams.Add(player.Id, new PlayerStream(player.PlayerKey, Channel?.GetGeneration(player.Id) ?? 0));
            }
        }

        private readonly struct PlayerStream
        {
            internal readonly MultiplayerPlayerKey Key;
            internal readonly ulong Generation;
            internal PlayerStream(MultiplayerPlayerKey key, ulong generation) { Key = key; Generation = generation; }
        }
    }

    internal sealed partial class PlayerContext
    {
        internal void Forget(ParticipantId id)
        {
            Inputs.Remove(id); poses.Remove(id); uis.Remove(id);
            if (Runtime.IsLocal(id)) SentInputs.Clear();
        }
    }
}
