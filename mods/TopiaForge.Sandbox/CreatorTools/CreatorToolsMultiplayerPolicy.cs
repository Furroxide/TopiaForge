using System;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.CreatorTools
{
    internal static class CreatorToolsMultiplayerPolicy
    {
        public static bool Allows(MultiplayerSessionSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            return snapshot.ProcessKind != MultiplayerProcessKind.Headless
                && !snapshot.Participants.Any(participant => participant.IsConnected && !participant.IsLocal);
        }
    }
}
