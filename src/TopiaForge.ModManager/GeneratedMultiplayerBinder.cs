using System;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Binds a mod's generated multiplayer contract after its context is attached and before <c>OnLoad</c> runs, so
    /// authors never write the binding ceremony. Mods compiled before the binding marker existed do not carry it and
    /// keep binding explicitly; a mod that still binds in <c>OnLoad</c> gets an idempotent success.
    /// </summary>
    internal static class GeneratedMultiplayerBinder
    {
        internal const string MissingProviderWarning =
            "This mod has a generated multiplayer contract but does not depend on the multiplayer provider, so the " +
            "contract was not bound. Run `topiaforge mod add multiplayer` to declare the dependency.";

        internal static void Bind(object mod, IModContext context)
        {
            if (mod is not IGeneratedMultiplayerBinding) return;
            Bind(
                mod,
                () => context.TryGetMultiplayer(out var session) ? session : null,
                context.Lifetime,
                context.Logger);
        }

        internal static void Bind(
            object mod,
            Func<IMultiplayerSession?> resolveSession,
            IModLifetime lifetime,
            IModLogger logger)
        {
            if (mod is not IGeneratedMultiplayerBinding generated) return;
            var session = resolveSession();
            if (session == null)
            {
                logger.Warn(MissingProviderWarning);
                return;
            }

            var bound = generated.BindMultiplayer(session);
            if (!bound.TryGetValue(out var lease) || lease == null)
            {
                throw new InvalidOperationException(
                    "Binding the generated multiplayer contract failed (" + bound.ErrorCode + "): " + bound.ErrorMessage);
            }

            lifetime.Track(lease);
        }
    }
}
