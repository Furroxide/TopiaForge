using System;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer
{
    /// <summary>Publishes the standalone loopback implementation of the stable multiplayer contract.</summary>
    public sealed class MultiplayerMod : TopiaForgeMod
    {
        /// <inheritdoc />
        protected override void OnLoad()
        {
            var session = new LoopbackMultiplayerSession(Context.Identity.Id);
            Context.Lifetime.Track(session);
            var registration = Context.Extensions.Register<IMultiplayerSession>(session);
            if (!registration.Succeeded)
            {
                throw new InvalidOperationException(registration.ErrorMessage);
            }

            Context.Logger.Info(
                "TopiaForge Multiplayer API preview loaded with the standalone loopback provider; live transport is not enabled.");
        }
    }
}
