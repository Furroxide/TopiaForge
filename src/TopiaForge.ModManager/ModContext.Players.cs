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
            if (playerContext != null) playerContext.ViewContext = this;
            var logger = Logger;
            var directory = new PlayerDirectory(() => session.Snapshot, generation, playerContext,
                exception => logger.Error(exception, "A player join or leave handler failed."));
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
