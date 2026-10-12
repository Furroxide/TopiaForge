using System;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer
{
    /// <summary>Publishes the multiplayer provider: the session engine, hosting a solo session until live transport arrives.</summary>
    public sealed class MultiplayerMod : TopiaForgeMod
    {
        /// <inheritdoc />
        protected override void OnLoad()
        {
            var provider = new MultiplayerProvider(Context.Identity.Id, new LoggerDiagnostics(Context.Logger));
            Context.Lifetime.Track(provider);
            var registration = Context.Extensions.Register<IMultiplayerSession>(provider);
            if (!registration.Succeeded)
            {
                throw new InvalidOperationException(registration.ErrorMessage);
            }

            Context.Lifetime.Track(Context.Events.SubscribeUpdate(_ => provider.Pump()));
            Context.Logger.Info(
                "TopiaForge Multiplayer loaded with the solo session engine; live transport is not enabled yet.");
        }
    }
}
