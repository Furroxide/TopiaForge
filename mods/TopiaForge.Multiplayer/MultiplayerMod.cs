using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Multiplayer
{
    /// <summary>
    /// Publishes the multiplayer provider: the session engine, hosting a solo session until a player opens it or joins
    /// another, and the internal control TopiaForge's runtime drives it through.
    /// </summary>
    public sealed class MultiplayerMod : TopiaForgeMod
    {
        /// <inheritdoc />
        protected override void OnLoad()
        {
            var diagnostics = new RoutedDiagnostics(Context.Logger);
            var content = new RuntimeContent(GameBuild(), Context.Runtime.LoaderVersion.ToString());

            // Live sessions stay switched off until the release enables them; a developer can turn them on to test.
            var live = DeveloperSessions.IsLiveEnabled()
                ? new Network.LiveSessionsSetup(new Network.ModDataFiles(Context.Files), content)
                : null;
            var provider = new MultiplayerProvider(Context.Identity.Id, diagnostics, live: live);
            Context.Lifetime.Track(provider);
            Register<IMultiplayerSession>(provider);

            var developer = provider.Live == null ? null : DeveloperSessions.Create(provider.Live, Context.Logger);
            var control = new InternalControl(provider, content, diagnostics, developer);
            Context.Lifetime.Track(control);
            Register<IInternalMultiplayerControl>(control);

            // Once the runtime attaches it pumps the provider first in the frame; until then the provider pumps itself.
            Context.Lifetime.Track(Context.Events.SubscribeUpdate(_ => control.PumpUnattached()));
            Context.Logger.Info(provider.Live == null
                ? "TopiaForge Multiplayer loaded with the solo session engine; live sessions are switched off."
                : "TopiaForge Multiplayer loaded with live sessions switched on for development.");
        }

        private void Register<T>(T service) where T : class
        {
            var registration = Context.Extensions.Register(service);
            if (!registration.Succeeded)
            {
                throw new InvalidOperationException(registration.ErrorMessage);
            }
        }

        private string GameBuild() =>
            Context.Runtime.TryGetGameVersion(out var version) ? version.Patch.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown";
    }
}
