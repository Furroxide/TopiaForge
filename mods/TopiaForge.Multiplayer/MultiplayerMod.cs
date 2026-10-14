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
            // Live sessions stay switched off until the release enables them; a developer can turn them on to test.
            var live = DeveloperSessions.IsLiveEnabled()
                ? new Network.LiveSessionsSetup(new Network.ModDataFiles(Context.Files), new Network.ProviderOnlyContent(GameBuild(), Context.Runtime.LoaderVersion.ToString()))
                : null;
            var provider = new MultiplayerProvider(Context.Identity.Id, new LoggerDiagnostics(Context.Logger), live: live);
            Context.Lifetime.Track(provider);
            var registration = Context.Extensions.Register<IMultiplayerSession>(provider);
            if (!registration.Succeeded)
            {
                throw new InvalidOperationException(registration.ErrorMessage);
            }

            var developer = provider.Live == null ? null : DeveloperSessions.Create(provider.Live, Context.Logger);
            Context.Lifetime.Track(Context.Events.SubscribeUpdate(_ =>
            {
                provider.Pump();
                developer?.Pump();
            }));
            Context.Logger.Info(provider.Live == null
                ? "TopiaForge Multiplayer loaded with the solo session engine; live sessions are switched off."
                : "TopiaForge Multiplayer loaded with live sessions switched on for development.");
        }

        private string GameBuild() =>
            Context.Runtime.TryGetGameVersion(out var version) ? version.Patch.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown";
    }
}
