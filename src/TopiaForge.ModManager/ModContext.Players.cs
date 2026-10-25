using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class ModContext
    {
        private readonly PlayerContext? playerContext;

        IPlayerDirectory IInternalPlayerContext.CreatePlayers(IMultiplayerSession session, Func<ulong> generation)
        {
            var directory = new PlayerDirectory(() => session.Snapshot, generation, playerContext);
            if (playerContext != null)
            {
                playerContext.Directory = directory;
                playerContext.Runtime.BindTickSource(playerContext, session);
            }
            Lifetime.Track(directory);
            Lifetime.Track(session.SubscribeChanged(_ => directory.Refresh()));
            return directory;
        }
    }
}
