using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Voice
{
    /// <summary>Local playback gain by the host's stable player key, with session-only fallback for an unkeyed host.</summary>
    internal sealed class VoiceVolumeBook
    {
        internal const int Capacity = 256;
        private readonly Dictionary<MultiplayerPlayerKey, float> keyed = new Dictionary<MultiplayerPlayerKey, float>();
        private readonly Dictionary<ParticipantId, float> unkeyed = new Dictionary<ParticipantId, float>();
        private MultiplayerSessionId session;
        internal float Get(MultiplayerParticipant player, MultiplayerSessionId current) => player.PlayerKey.IsValid
            ? keyed.TryGetValue(player.PlayerKey, out var value) ? value : 1f
            : current.Equals(session) && unkeyed.TryGetValue(player.Id, out value) ? value : 1f;
        internal bool Set(MultiplayerParticipant player, MultiplayerSessionId current, float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || !current.IsValid) return false;
            value = Math.Max(0f, Math.Min(1f, value));
            if (!current.Equals(session)) { unkeyed.Clear(); session = current; }
            if (player.PlayerKey.IsValid)
            {
                if (value == 1f) { keyed.Remove(player.PlayerKey); return true; }
                if (!keyed.ContainsKey(player.PlayerKey) && keyed.Count + unkeyed.Count >= Capacity) return false;
                keyed[player.PlayerKey] = value;
            }
            else
            {
                if (value == 1f) { unkeyed.Remove(player.Id); return true; }
                if (!unkeyed.ContainsKey(player.Id) && keyed.Count + unkeyed.Count >= Capacity) return false;
                unkeyed[player.Id] = value;
            }
            return true;
        }
    }
}
